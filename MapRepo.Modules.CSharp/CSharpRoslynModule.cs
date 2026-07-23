using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using MapRepo.Core;

namespace MapRepo.Modules.CSharp;

public sealed class CSharpRoslynModule : IRepositoryLanguageModule, IIncrementalAnalyzer, IRepositoryLifecycle
{
    private static int _msbuildRegistered;

    // One live MSBuildWorkspace per repository so incremental reindexing skips the solution reload.
    private readonly ConcurrentDictionary<string, RepoWorkspace> _workspaces = new(StringComparer.OrdinalIgnoreCase);

    private sealed class RepoWorkspace
    {
        public required MSBuildWorkspace Workspace { get; set; }
        public required Solution Solution { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    public ModuleDescriptor Descriptor { get; } = new(
        "csharp-roslyn", "C# / Roslyn", ["csharp"], "1.0.0", true);

    public bool CanAnalyze(string filePath) => filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    public async Task<AnalysisSnapshot> AnalyzeAsync(AnalysisRequest request)
    {
        var diagnostics = new List<string>();
        var symbols = new List<SymbolRecord>();
        var relationships = new List<RelationshipRecord>();
        var seenSymbols = new HashSet<string>(StringComparer.Ordinal);
        var seenRelationships = new HashSet<string>(StringComparer.Ordinal);
        if (!PathExclusions.EnumerateFiles(request.Repository.RootPath, "*.cs", request.Repository.ExcludedPaths).Any())
            return new AnalysisSnapshot(request.Repository.Id, CreateGeneration(request.Repository), [], [], [], DateTimeOffset.UtcNow);
        var solutionPath = ResolveSolution(request.Repository);

        MSBuildWorkspace? workspace = null;
        try
        {
            RegisterMsBuild(diagnostics);
            workspace = MSBuildWorkspace.Create();
            workspace.RegisterWorkspaceFailedHandler(diagnostic => diagnostics.Add(diagnostic.Diagnostic.Message));
            Solution solution;
            if (solutionPath is not null && (solutionPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
                solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: request.CancellationToken);
            else if (solutionPath is not null)
                solution = await workspace.OpenProjectAsync(solutionPath, cancellationToken: request.CancellationToken)
                    is { } project ? project.Solution : workspace.CurrentSolution;
            else
                throw new InvalidOperationException("No .sln or .csproj found");

            CacheWorkspace(request.Repository.Id, workspace, solution);

            foreach (var project in solution.Projects)
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                var compilation = await project.GetCompilationAsync(request.CancellationToken);
                if (compilation is null) continue;
                foreach (var document in project.Documents.Where(d => d.FilePath?.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) == true
                    && !Excluded(d.FilePath, request.Repository.ExcludedPaths)
                    && (request.Repository.AllowExternalSymbols || IsUnderRoot(request.Repository.RootPath, d.FilePath!))))
                {
                    try
                    {
                        await AnalyzeDocumentAsync(request, project, document, compilation, symbols,
                            relationships, seenSymbols, seenRelationships, diagnostics);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        diagnostics.Add($"Roslyn document skipped: {RelativePath(request.Repository.RootPath, document.FilePath ?? project.FilePath ?? project.Name)}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReleaseRepository(request.Repository.Id);
            workspace?.Dispose();
            diagnostics.Add($"Roslyn workspace fallback: {ex.Message}");
            await AnalyzeLooseFilesAsync(request, solutionPath, symbols, relationships, seenSymbols, seenRelationships, diagnostics);
        }

        return new AnalysisSnapshot(request.Repository.Id, CreateGeneration(request.Repository),
            symbols, relationships, diagnostics.Distinct().Take(100).ToArray(), DateTimeOffset.UtcNow);
    }

    private void CacheWorkspace(string repositoryId, MSBuildWorkspace workspace, Solution solution)
    {
        if (_workspaces.TryRemove(repositoryId, out var previous) && !ReferenceEquals(previous.Workspace, workspace))
            previous.Workspace.Dispose();
        _workspaces[repositoryId] = new RepoWorkspace { Workspace = workspace, Solution = solution };
    }

    public void ReleaseRepository(string repositoryId)
    {
        if (_workspaces.TryRemove(repositoryId, out var state)) state.Workspace.Dispose();
    }

    /// <summary>Incremental path: swap the changed documents' text into the cached solution and
    /// reanalyze only those documents. Returns null (full run required) when there is no cached
    /// workspace or the change set contains files unknown to the solution (new files).</summary>
    public async Task<FileAnalysisDelta?> AnalyzeFilesAsync(AnalysisRequest request)
    {
        var changed = request.ChangedPaths.Where(CanAnalyze).Where(path => !Excluded(path, request.Repository.ExcludedPaths)).ToArray();
        if (changed.Length == 0) return new FileAnalysisDelta([], [], [], []);
        if (!_workspaces.TryGetValue(request.Repository.Id, out var state)) return null;

        await state.Gate.WaitAsync(request.CancellationToken);
        try
        {
            var solution = state.Solution;
            var targets = new List<DocumentId>();
            var files = new List<string>();
            foreach (var path in changed)
            {
                var full = Path.GetFullPath(path);
                var relative = RelativePath(request.Repository.RootPath, full);
                var documentIds = solution.GetDocumentIdsWithFilePath(full);
                if (documentIds.IsDefaultOrEmpty)
                {
                    if (File.Exists(full)) return null; // new file: the project must be reloaded
                    continue;                            // deleted file the solution never knew about
                }
                if (!File.Exists(full))
                {
                    foreach (var documentId in documentIds) solution = solution.RemoveDocument(documentId);
                    files.Add(relative);
                    continue;
                }
                var text = Microsoft.CodeAnalysis.Text.SourceText.From(
                    await File.ReadAllTextAsync(full, request.CancellationToken));
                foreach (var documentId in documentIds) solution = solution.WithDocumentText(documentId, text);
                targets.AddRange(documentIds);
                files.Add(relative);
            }
            state.Solution = solution;

            var diagnostics = new List<string>();
            var symbols = new List<SymbolRecord>();
            var relationships = new List<RelationshipRecord>();
            var seenSymbols = new HashSet<string>(StringComparer.Ordinal);
            var seenRelationships = new HashSet<string>(StringComparer.Ordinal);
            foreach (var documentId in targets)
            {
                var document = solution.GetDocument(documentId);
                if (document is null) continue;
                var compilation = await document.Project.GetCompilationAsync(request.CancellationToken);
                if (compilation is null) continue;
                try
                {
                    await AnalyzeDocumentAsync(request, document.Project, document, compilation,
                        symbols, relationships, seenSymbols, seenRelationships, diagnostics);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    diagnostics.Add($"Incremental document skipped: {document.FilePath}: {ex.Message}");
                }
            }
            return new FileAnalysisDelta(files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                symbols, relationships, diagnostics);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static string? ResolveSolution(RepositoryDefinition repository)
    {
        if (!string.IsNullOrWhiteSpace(repository.SolutionPath)) return Path.GetFullPath(repository.SolutionPath);
        var root = Path.GetFullPath(repository.RootPath);
        var candidates = PathExclusions.EnumerateFiles(root, "*.*", repository.ExcludedPaths)
            .Where(path => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(path => new { Path = path, Score = ProjectScore(root, path) })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Path.Length)
            .ToArray();
        return candidates.FirstOrDefault()?.Path;
    }

    private static int ProjectScore(string root, string path)
    {
        var normalized = path.Replace('\\', '/');
        var score = path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ? 20 : 0;
        score -= Math.Max(0, normalized[(root.Length)..].Count(c => c == '/') - 1);
        return score;
    }

    private static void RegisterMsBuild(List<string> diagnostics)
    {
        if (Interlocked.Exchange(ref _msbuildRegistered, 1) != 0) return;
        try { MSBuildLocator.RegisterDefaults(); }
        catch (Exception ex) { diagnostics.Add($"MSBuild discovery: {ex.Message}"); }
    }

    private static async Task AnalyzeDocumentAsync(
        AnalysisRequest request, Project project, Document document, Compilation compilation,
        List<SymbolRecord> symbols, List<RelationshipRecord> relationships,
        HashSet<string> seenSymbols, HashSet<string> seenRelationships, List<string> diagnostics)
    {
        var root = await document.GetSyntaxRootAsync(request.CancellationToken);
        var model = await document.GetSemanticModelAsync(request.CancellationToken);
        if (root is null || model is null || document.FilePath is null) return;
        var file = RelativePath(request.Repository.RootPath, document.FilePath);
        var tree = root.SyntaxTree;
        var sourceSymbols = new Dictionary<SyntaxNode, ISymbol>();

        foreach (var node in root.DescendantNodes().Where(IsDeclaration))
        {
            var symbol = model.GetDeclaredSymbol(node, request.CancellationToken);
            if (symbol is null || symbol.IsImplicitlyDeclared) continue;
            sourceSymbols[node] = symbol;
            AddSymbol(symbol, request.Repository.Id, project.Name, file, request.Repository.RootPath, request.Repository.AllowExternalSymbols, tree, symbols, seenSymbols);
            var parent = model.GetEnclosingSymbol(node.SpanStart, request.CancellationToken);
            if (parent is not null && parent.Kind != SymbolKind.Namespace)
                AddRelationship(parent, symbol, "contains", file, node.SpanStart, tree, request, relationships, seenRelationships);
        }

        if (request.Repository.IncludeTextualEvidence)
        {
            foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>()
                .Where(node => node.IsKind(SyntaxKind.StringLiteralExpression)))
            {
                AddTextualEvidence(literal.Token.ValueText, literal.SpanStart, literal.Span.Length, request.Repository.Id,
                    project.Name, file, tree, symbols, seenSymbols);
            }
        }

        foreach (var node in root.DescendantNodes())
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            if (node is InvocationExpressionSyntax invocation)
            {
                var target = model.GetSymbolInfo(invocation.Expression, request.CancellationToken).Symbol;
                var source = model.GetEnclosingSymbol(invocation.SpanStart, request.CancellationToken);
                if (target is not null && source is not null)
                    AddRelationship(source, target, "calls", file, invocation.SpanStart, tree, request, relationships, seenRelationships);
            }
            else if (node is ObjectCreationExpressionSyntax creation)
            {
                var target = model.GetSymbolInfo(creation.Type, request.CancellationToken).Symbol;
                var source = model.GetEnclosingSymbol(creation.SpanStart, request.CancellationToken);
                if (target is not null && source is not null)
                    AddRelationship(source, target, "constructs", file, creation.SpanStart, tree, request, relationships, seenRelationships);
            }
            else if (node is BaseTypeSyntax baseType)
            {
                var target = model.GetTypeInfo(baseType, request.CancellationToken).Type;
                var source = model.GetEnclosingSymbol(baseType.SpanStart, request.CancellationToken);
                if (target is not null && source is not null)
                {
                    var sourceType = source as INamedTypeSymbol;
                    var kind = sourceType?.TypeKind == TypeKind.Interface ? "inherits"
                        : target.TypeKind == TypeKind.Interface ? "implements" : "inherits";
                    AddRelationship(source, target, kind, file, baseType.SpanStart, tree, request, relationships, seenRelationships);
                }
            }
            else if (node is SimpleNameSyntax name && node.Parent is not MemberAccessExpressionSyntax)
            {
                var target = model.GetSymbolInfo(name, request.CancellationToken).Symbol;
                var source = model.GetEnclosingSymbol(name.SpanStart, request.CancellationToken);
                if (target is not null && source is not null && target.Kind is not SymbolKind.Local and not SymbolKind.Parameter)
                    AddRelationship(source, target, "references", file, name.SpanStart, tree, request, relationships, seenRelationships);
            }
        }
    }

    private static bool IsDeclaration(SyntaxNode node) => node is BaseTypeDeclarationSyntax
        or MethodDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax
        or EventDeclarationSyntax or FieldDeclarationSyntax or LocalFunctionStatementSyntax
        or DelegateDeclarationSyntax or EnumMemberDeclarationSyntax;

    private static void AddTextualEvidence(string value, int literalStart, int literalLength, string repositoryId, string? project, string file,
        SyntaxTree tree, List<SymbolRecord> output, HashSet<string> seen)
    {
        foreach (Match match in Regex.Matches(value, @"\b[A-Za-z_][A-Za-z0-9_]{3,}\b"))
        {
            var name = match.Value;
            if (!name.Any(char.IsUpper) && !name.Contains('_')) continue;
            var safeStart = Math.Clamp(literalStart + match.Index, literalStart, literalStart + Math.Max(0, literalLength - 1));
            var safeLength = Math.Min(match.Length, Math.Max(1, literalStart + literalLength - safeStart));
            var span = tree.GetLineSpan(new Microsoft.CodeAnalysis.Text.TextSpan(safeStart, safeLength));
            var id = Hash($"{file}|text|{name}|{span.StartLinePosition.Line + 1}|{span.StartLinePosition.Character + 1}");
            if (!seen.Add(id)) continue;
            output.Add(new SymbolRecord(id, repositoryId, project, file, name, name, "textual-evidence",
                span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
                span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1, name,
                "csharp", "csharp-roslyn"));
        }
    }

    private static void AddSymbol(ISymbol symbol, string repositoryId, string project, string file, string rootPath,
        bool allowExternal, SyntaxTree tree, List<SymbolRecord> output, HashSet<string> seen)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is null) return;
        var sourceTree = location.SourceTree ?? tree;
        if (!allowExternal && !string.IsNullOrWhiteSpace(sourceTree.FilePath) && !IsUnderRoot(rootPath, sourceTree.FilePath)) return;
        if (!TryGetLineSpan(sourceTree, location.SourceSpan, out var span)) return;
        var symbolFile = !string.IsNullOrWhiteSpace(sourceTree.FilePath)
            ? RelativePath(rootPath, sourceTree.FilePath)
            : file;
        var id = SymbolId(symbol, symbolFile);
        if (!seen.Add(id)) return;
        output.Add(new SymbolRecord(id, repositoryId, project, symbolFile, symbol.Name,
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), symbol.Kind.ToString(),
            span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1, span.EndLinePosition.Character + 1,
            symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), "csharp", "csharp-roslyn"));
    }

    private static void AddRelationship(ISymbol source, ISymbol target, string kind, string file, int position,
        SyntaxTree tree, AnalysisRequest request, List<RelationshipRecord> output, HashSet<string> seen)
    {
        var sourceLocation = source.Locations.FirstOrDefault(l => l.IsInSource);
        var targetLocation = target.Locations.FirstOrDefault(l => l.IsInSource);
        if (sourceLocation is null || targetLocation is null) return;
        var sourceTree = sourceLocation.SourceTree;
        var targetTree = targetLocation.SourceTree;
        if (sourceTree is null || targetTree is null) return;
        if (!request.Repository.AllowExternalSymbols &&
            (!IsUnderRoot(request.Repository.RootPath, sourceTree.FilePath) || !IsUnderRoot(request.Repository.RootPath, targetTree.FilePath))) return;
        if (!TryGetLineSpan(sourceTree, sourceLocation.SourceSpan, out _) ||
            !TryGetLineSpan(targetTree, targetLocation.SourceSpan, out _)) return;
        var sourceId = SymbolId(source, RelativePath(request.Repository.RootPath, sourceTree.FilePath));
        var targetId = SymbolId(target, RelativePath(request.Repository.RootPath, targetTree.FilePath));
        if (!TryGetLineSpan(tree, new Microsoft.CodeAnalysis.Text.TextSpan(position, 0), out var line)) return;
        var edgeId = Hash($"{sourceId}|{targetId}|{kind}|{file}|{line.StartLinePosition.Line + 1}");
        if (!seen.Add(edgeId)) return;
        output.Add(new RelationshipRecord(edgeId, request.Repository.Id, sourceId, targetId, kind,
            file, line.StartLinePosition.Line + 1, line.StartLinePosition.Character + 1,
            "semantic", "csharp", "csharp-roslyn"));
    }

    private static async Task AnalyzeLooseFilesAsync(AnalysisRequest request, string? solutionPath, List<SymbolRecord> symbols,
        List<RelationshipRecord> relationships, HashSet<string> seenSymbols, HashSet<string> seenRelationships,
        List<string> diagnostics)
    {
        diagnostics.Add("Roslyn could not load the solution; configure a valid .sln/.csproj and MSBuild SDK");
        var scopeRoot = solutionPath is not null && solutionPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(solutionPath)! : solutionPath is not null ? Path.GetDirectoryName(solutionPath)! : request.Repository.RootPath;
        var files = PathExclusions.EnumerateFiles(scopeRoot, "*.cs", request.Repository.ExcludedPaths);
        foreach (var path in files)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            try
            {
                var text = await File.ReadAllTextAsync(path, request.CancellationToken);
                var tree = CSharpSyntaxTree.ParseText(text, cancellationToken: request.CancellationToken);
                var root = await tree.GetRootAsync(request.CancellationToken);
                var file = RelativePath(request.Repository.RootPath, path);
                var local = new Dictionary<string, SymbolRecord>(StringComparer.Ordinal);
                if (request.Repository.IncludeTextualEvidence)
                    foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>()
                        .Where(node => node.IsKind(SyntaxKind.StringLiteralExpression)))
                        AddTextualEvidence(literal.Token.ValueText, literal.SpanStart, literal.Span.Length, request.Repository.Id,
                            null, file, tree, symbols, seenSymbols);
                foreach (var node in root.DescendantNodes().Where(IsDeclaration))
                {
                    var name = node switch
                    {
                        BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
                        MethodDeclarationSyntax method => method.Identifier.ValueText,
                        ConstructorDeclarationSyntax ctor => ctor.Identifier.ValueText,
                        PropertyDeclarationSyntax property => property.Identifier.ValueText,
                        EventDeclarationSyntax evt => evt.Identifier.ValueText,
                        FieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText ?? "field",
                        LocalFunctionStatementSyntax localFunction => localFunction.Identifier.ValueText,
                        DelegateDeclarationSyntax del => del.Identifier.ValueText,
                        EnumMemberDeclarationSyntax member => member.Identifier.ValueText,
                        _ => "symbol"
                    };
                    var line = tree.GetLineSpan(node.Span);
                    var id = Hash($"{file}|loose|{name}|{line.StartLinePosition.Line + 1}|{line.StartLinePosition.Character + 1}");
                    var symbol = new SymbolRecord(id, request.Repository.Id, null, file, name, name,
                        node.Kind().ToString(), line.StartLinePosition.Line + 1, line.StartLinePosition.Character + 1,
                        line.EndLinePosition.Line + 1, line.EndLinePosition.Character + 1, node.ToString().Split('\n')[0].Trim(), "csharp", "csharp-roslyn");
                    if (seenSymbols.Add(id)) { symbols.Add(symbol); local.TryAdd(name, symbol); }
                }
                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var name = invocation.Expression switch { IdentifierNameSyntax identifier => identifier.Identifier.ValueText, MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText, _ => null };
                    if (name is null || !local.TryGetValue(name, out var target)) continue;
                    var sourceNode = invocation.Ancestors().FirstOrDefault(IsDeclaration);
                    if (sourceNode is null) continue;
                    var sourceName = sourceNode switch { BaseTypeDeclarationSyntax type => type.Identifier.ValueText, MethodDeclarationSyntax method => method.Identifier.ValueText, ConstructorDeclarationSyntax ctor => ctor.Identifier.ValueText, _ => null };
                    if (sourceName is null || !local.TryGetValue(sourceName, out var source)) continue;
                    var position = tree.GetLineSpan(invocation.Span);
                    var edgeId = Hash($"{source.Id}|{target.Id}|calls|{file}|{position.StartLinePosition.Line + 1}");
                    if (seenRelationships.Add(edgeId)) relationships.Add(new RelationshipRecord(edgeId, request.Repository.Id, source.Id, target.Id, "calls", file, position.StartLinePosition.Line + 1, position.StartLinePosition.Character + 1, "syntax", "csharp", "csharp-roslyn"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { diagnostics.Add($"Fallback read failed for {path}: {ex.Message}"); }
        }
    }

    private static string RelativePath(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static bool TryGetLineSpan(SyntaxTree tree, Microsoft.CodeAnalysis.Text.TextSpan textSpan, out FileLinePositionSpan lineSpan)
    {
        lineSpan = default;
        if (textSpan.Start < 0) return false;
        var textLength = tree.GetText().Length;
        if (textSpan.Start > textLength || textSpan.End > textLength) return false;
        lineSpan = tree.GetLineSpan(textSpan);
        return true;
    }

    private static bool Excluded(string path, IReadOnlyList<string>? extra = null) => PathExclusions.IsExcluded(path, extra);
    private static bool IsUnderRoot(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
    // FullyQualifiedFormat's MemberOptions is None: for a member symbol (method/property/field) it
    // renders only the bare member name, dropping the containing type. Two same-named members in
    // different classes in the same file then hash to the same id and the second silently vanishes
    // (seenSymbols/seenRelationships treat it as an already-emitted duplicate). CSharpErrorMessageFormat
    // includes the containing type for members (it's built for "Type.Member(args)" diagnostic text),
    // which is what the id actually needs to stay unique.
    private static string SymbolId(ISymbol symbol, string file) => Hash($"{file}|{symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)}");
    private static string CreateGeneration(RepositoryDefinition repository) => Hash($"{repository.Id}|{DateTimeOffset.UtcNow:yyyyMMddHHmm}")[..16];
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
}
