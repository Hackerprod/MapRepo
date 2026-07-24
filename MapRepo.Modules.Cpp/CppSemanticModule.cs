using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClangSharp.Interop;
using static ClangSharp.Interop.CXCursorKind;
using static ClangSharp.Interop.CXChildVisitResult;
using MapRepo.Core;

namespace MapRepo.Modules.Cpp;

public sealed class CppSemanticModule : IRepositoryLanguageModule, IIncrementalAnalyzer, IRepositoryLifecycle
{
    // The default .NET native-library probe only checks the *host* executable's own directory (plus
    // system paths) — never a plugin assembly's directory. This module is loaded via
    // Assembly.LoadFrom from MapRepo.Server/modules/, so libclang.dll/libClangSharp.dll sitting right
    // next to it are otherwise invisible and every P/Invoke throws DllNotFoundException. Loading them
    // explicitly by full path up front sidesteps that: once a DLL of a given name is already resident
    // in the process, a later P/Invoke-by-simple-name resolves to it directly (Windows checks the
    // already-loaded-module list before any directory search) — no DllImportResolver registration-order
    // fragility to get right (ClangSharp.Interop may already register one of its own for this assembly).
    static CppSemanticModule()
    {
        var moduleDirectory = Path.GetDirectoryName(typeof(CppSemanticModule).Assembly.Location);
        if (moduleDirectory is null) return;
        foreach (var name in new[] { "libclang.dll", "libClangSharp.dll" })
        {
            var candidate = Path.Combine(moduleDirectory, name);
            if (File.Exists(candidate)) NativeLibrary.Load(candidate);
        }
    }

    private readonly ConcurrentDictionary<string, RepoState> _states = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CompileCommand(string Directory, string[] Arguments);

    private sealed class RepoState
    {
        public CXIndex Index { get; set; } = default!;
        public Dictionary<string, CXTranslationUnit> TranslationUnits { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public IReadOnlyDictionary<string, CompileCommand>? CompileCommands { get; set; }
    }

    public ModuleDescriptor Descriptor { get; } = new(
        "cpp-clang", "C / C++ (Clang)", ["cpp", "c", "h", "hpp"], "1.0.0", true);

    public bool CanAnalyze(string filePath) => IsSource(filePath);

    public void ReleaseRepository(string repositoryId)
    {
        if (_states.TryRemove(repositoryId, out var state))
        {
            foreach (var tu in state.TranslationUnits.Values)
                try { tu.Dispose(); } catch { }
            try { state.Index.Dispose(); } catch { }
        }
    }

    public Task<FileAnalysisDelta?> AnalyzeFilesAsync(AnalysisRequest request) => AnalyzeFilesImpl(request);

    private async Task<FileAnalysisDelta?> AnalyzeFilesImpl(AnalysisRequest request)
    {
        var changed = request.ChangedPaths.Where(CanAnalyze).Where(p => !Excluded(p, request.Repository.ExcludedPaths)).ToArray();
        if (changed.Length == 0) return new FileAnalysisDelta([], [], [], []);
        if (!_states.TryGetValue(request.Repository.Id, out var state)) return null;

        await state.Gate.WaitAsync(request.CancellationToken);
        try
        {
            var diagnostics = new List<string>();
            var symbols = new List<SymbolRecord>();
            var relationships = new List<RelationshipRecord>();
            var seenSymbols = new HashSet<string>(StringComparer.Ordinal);
            var seenRelationships = new HashSet<string>(StringComparer.Ordinal);

            foreach (var path in changed)
            {
                if (!File.Exists(path)) continue;
                var file = Relative(request.Repository.RootPath, path);
                if (state.TranslationUnits.TryGetValue(file, out var old))
                    try { old.Dispose(); } catch { }

                var tu = ParseFile(request.Repository, path, state.Index, state.CompileCommands, diagnostics);
                if (tu != default)
                {
                    state.TranslationUnits[file] = tu;
                    AnalyzeTu(request.Repository, tu, file, symbols, relationships, seenSymbols, seenRelationships, diagnostics);
                }
            }
            return new FileAnalysisDelta(
                changed.Select(p => Relative(request.Repository.RootPath, p)).ToArray(),
                symbols, relationships, diagnostics);
        }
        finally { state.Gate.Release(); }
    }

    public async Task<AnalysisSnapshot> AnalyzeAsync(AnalysisRequest request)
    {
        var diagnostics = new List<string>();
        var allSymbols = new List<SymbolRecord>();
        var allRelationships = new List<RelationshipRecord>();
        var seenSymbols = new HashSet<string>(StringComparer.Ordinal);
        var seenRelationships = new HashSet<string>(StringComparer.Ordinal);

        ReleaseRepository(request.Repository.Id);
        var state = new RepoState { Index = CXIndex.Create(), CompileCommands = LoadCompileCommands(request.Repository.RootPath, diagnostics) };
        _states[request.Repository.Id] = state;

        var files = EnumerateFiles(request.Repository.RootPath, request.Repository.ExcludedPaths).ToArray();
        foreach (var path in files)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            var file = Relative(request.Repository.RootPath, path);
            var tu = ParseFile(request.Repository, path, state.Index, state.CompileCommands, diagnostics);
            if (tu != default)
            {
                state.TranslationUnits[file] = tu;
                AnalyzeTu(request.Repository, tu, file, allSymbols, allRelationships, seenSymbols, seenRelationships, diagnostics);
            }
        }

        return new AnalysisSnapshot(request.Repository.Id, Generation(request.Repository.Id),
            allSymbols, allRelationships, diagnostics.Distinct().Take(100).ToArray(), DateTimeOffset.UtcNow);
    }

    /// <summary>Looks for a CMake/Ninja-style compilation database in the usual output locations.
    /// Without it, include/define resolution is a guess (repo root + the file's own directory +
    /// top-level include/src/lib) and will systematically under-resolve headers on anything beyond
    /// a trivial project — a compilation database carries the flags the project was actually built
    /// with.</summary>
    private static IReadOnlyDictionary<string, CompileCommand>? LoadCompileCommands(string root, List<string> diagnostics)
    {
        string[] candidates =
        [
            Path.Combine(root, "compile_commands.json"),
            Path.Combine(root, "build", "compile_commands.json"),
            Path.Combine(root, "out", "compile_commands.json"),
            Path.Combine(root, "out", "build", "compile_commands.json")
        ];
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return null;

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var map = new Dictionary<string, CompileCommand>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var file = entry.TryGetProperty("file", out var fileEl) ? fileEl.GetString() : null;
                if (string.IsNullOrEmpty(file)) continue;
                var directory = entry.TryGetProperty("directory", out var dirEl) ? dirEl.GetString() ?? root : root;

                string[]? arguments = null;
                if (entry.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
                    arguments = argsEl.EnumerateArray().Select(a => a.GetString() ?? string.Empty).ToArray();
                else if (entry.TryGetProperty("command", out var cmdEl))
                    arguments = SplitCommandLine(cmdEl.GetString() ?? string.Empty);
                if (arguments is null || arguments.Length == 0) continue;

                var fullPath = Path.IsPathRooted(file) ? Path.GetFullPath(file) : Path.GetFullPath(Path.Combine(directory, file));
                map[fullPath] = new CompileCommand(directory, arguments);
            }
            return map.Count > 0 ? map : null;
        }
        catch (Exception ex)
        {
            diagnostics.Add($"Failed to read compile_commands.json at {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Minimal whitespace/quote-aware split for the older "command" (single string) form of
    /// a compilation database entry — most modern generators emit "arguments" (already an array) instead.</summary>
    private static string[] SplitCommandLine(string command)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in command)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts.ToArray();
    }

    /// <summary>Reduces a compilation database entry's argv to flags libclang's single-file Parse can
    /// use: drops argv[0] (the compiler binary), the source file token itself (Parse takes it
    /// separately), and -c/-o (an output file makes no sense for a one-off in-memory parse).</summary>
    private static string[] AdaptCompileCommandArgs(CompileCommand command, string path)
    {
        var args = new List<string>();
        for (var i = 1; i < command.Arguments.Length; i++)
        {
            var arg = command.Arguments[i];
            if (string.Equals(arg, path, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Path.IsPathRooted(arg))
            {
                try { if (string.Equals(Path.GetFullPath(Path.Combine(command.Directory, arg)), path, StringComparison.OrdinalIgnoreCase)) continue; }
                catch { /* not a path-shaped argument */ }
            }
            if (arg == "-c") continue;
            if (arg == "-o") { i++; continue; }
            args.Add(arg);
        }
        return args.ToArray();
    }

    private static unsafe CXTranslationUnit ParseFile(RepositoryDefinition repo, string path, CXIndex index,
        IReadOnlyDictionary<string, CompileCommand>? compileCommands, List<string> diagnostics)
    {
        try
        {
            var args = new List<string>();
            if (compileCommands is not null && compileCommands.TryGetValue(Path.GetFullPath(path), out var command))
            {
                args.AddRange(AdaptCompileCommandArgs(command, path));
                args.Add("-Wno-everything");
                return ParseWithDiagnostics(repo, index, path, args, diagnostics);
            }

            args.Add("--language=" + (path.EndsWith(".c", StringComparison.OrdinalIgnoreCase) ? "c" : "c++"));
            args.Add("--std=c++20");
            args.Add("-I" + repo.RootPath);
            args.Add("-I" + Path.GetDirectoryName(path)!);
            args.Add("-Wno-everything");
            foreach (var dir in FindIncludeDirs(repo.RootPath))
                args.Add("-I" + dir);

            return ParseWithDiagnostics(repo, index, path, args, diagnostics);
        }
        catch (Exception ex)
        {
            diagnostics.Add($"Clang parse failed for {Relative(repo.RootPath, path)}: {ex.Message}");
            return default;
        }
    }

    private static unsafe CXTranslationUnit ParseWithDiagnostics(RepositoryDefinition repo, CXIndex index, string path, List<string> args, List<string> diagnostics)
    {
        // DetailedPreprocessingRecord is required for macro-definition cursors to appear at all —
        // without it, CXCursor_MacroDefinition (mapped in GetSymbolKind) never fires during the
        // visitor walk, silently hiding every #define in the codebase.
        var flags = CXTranslationUnit_Flags.CXTranslationUnit_DetailedPreprocessingRecord;
        var tu = CXTranslationUnit.Parse(index, path, args.ToArray(), ReadOnlySpan<CXUnsavedFile>.Empty, flags);
        if (tu == default) return tu;

        // -Wno-everything silences Clang's own warnings, but errors (most commonly an unresolved
        // #include) still land here — surfaced explicitly instead of the translation unit quietly
        // carrying degraded/incomplete semantic information with no diagnostic to show for it. Same
        // exclusion policy as symbols: a diagnostic whose own location is under an excludedPaths
        // entry (a vendored SDK's sample code, say) is exactly as uninteresting as that SDK's symbols
        // already are — surfacing it anyway would mean excluding a noisy vendor folder never actually
        // silences the noise it was meant to silence.
        var errorCount = 0;
        var totalErrors = 0;
        var numDiagnostics = clang.getNumDiagnostics(tu);
        for (uint i = 0; i < numDiagnostics; i++)
        {
            var diagnostic = clang.getDiagnostic(tu, i);
            try
            {
                var severity = clang.getDiagnosticSeverity(diagnostic);
                if (severity is not (CXDiagnosticSeverity.CXDiagnostic_Error or CXDiagnosticSeverity.CXDiagnostic_Fatal)) continue;
                var diagnosticFile = GetLocationFilePath(clang.getDiagnosticLocation(diagnostic));
                if (diagnosticFile is not null && !IsAllowedFile(repo, diagnosticFile)) continue;
                totalErrors++;
                if (errorCount >= 5) continue;
                var formatted = clang.formatDiagnostic(diagnostic, (uint)CXDiagnosticDisplayOptions.CXDiagnostic_DisplaySourceLocation).ToString();
                diagnostics.Add($"Clang: {formatted}");
                errorCount++;
            }
            finally { clang.disposeDiagnostic(diagnostic); }
        }
        if (totalErrors > errorCount)
            diagnostics.Add($"Clang: {Path.GetFileName(path)} has {totalErrors} error/fatal diagnostic(s) total (showing first {errorCount}).");
        return tu;
    }

    private static unsafe string? GetLocationFilePath(CXSourceLocation location)
    {
        void* filePtr = null;
        uint line = 0;
        clang.getFileLocation(location, &filePtr, &line, null, null);
        if (line == 0 || filePtr == null) return null;
        var file = new CXFile { Handle = (IntPtr)filePtr };
        var name = clang.getFileName(file).ToString();
        return string.IsNullOrEmpty(name) ? null : Path.GetFullPath(name);
    }

    private static bool IsAllowedFile(RepositoryDefinition repo, string path) =>
        (repo.AllowExternalSymbols || IsUnderRoot(repo.RootPath, path)) &&
        !PathExclusions.IsExcluded(path, repo.ExcludedPaths);

    private static unsafe void AnalyzeTu(RepositoryDefinition repo, CXTranslationUnit tu, string file,
        List<SymbolRecord> symbols, List<RelationshipRecord> relationships,
        HashSet<string> seenSymbols, HashSet<string> seenRelationships, List<string> diagnostics)
    {
        try
        {
            var modId = Hash($"{file}|module");
            var modSym = new SymbolRecord(modId, repo.Id, null, file,
                Path.GetFileName(file), file, "module", 1, 1, 1, 1, file, Language(file), "cpp-clang");
            if (seenSymbols.Add(modId)) symbols.Add(modSym);

            var ctx = new VisitContext(repo, file, modSym, symbols, relationships, seenSymbols, seenRelationships, diagnostics);
            var handle = GCHandle.Alloc(ctx);
            try
            {
                clang.visitChildren(tu.Cursor, &VisitorCallback, (void*)GCHandle.ToIntPtr(handle));
            }
            finally { handle.Free(); }
        }
        catch (Exception ex)
        {
            diagnostics.Add($"Clang analysis failed for {file}: {ex.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe CXChildVisitResult VisitorCallback(CXCursor cursor, CXCursor parent, void* data)
    {
        var handle = GCHandle.FromIntPtr((IntPtr)data);
        var ctx = (VisitContext)handle.Target!;
        return ctx.Visit(cursor, parent);
    }

    private sealed unsafe class VisitContext
    {
        private readonly RepositoryDefinition _repo;
        private readonly string _file;
        private readonly SymbolRecord _modSym;
        private readonly List<SymbolRecord> _symbols;
        private readonly List<RelationshipRecord> _relationships;
        private readonly HashSet<string> _seenSymbols;
        private readonly HashSet<string> _seenRelationships;
        private readonly List<string> _diagnostics;

        public VisitContext(RepositoryDefinition repo, string file, SymbolRecord modSym,
            List<SymbolRecord> symbols, List<RelationshipRecord> relationships,
            HashSet<string> seenSymbols, HashSet<string> seenRelationships, List<string> diagnostics)
        {
            _repo = repo; _file = file; _modSym = modSym;
            _symbols = symbols; _relationships = relationships;
            _seenSymbols = seenSymbols; _seenRelationships = seenRelationships;
            _diagnostics = diagnostics;
        }

        public CXChildVisitResult Visit(CXCursor cursor, CXCursor parent)
        {
            try
            {
                var kind = cursor.Kind;
                if (kind == CXCursor_InclusionDirective) return CXChildVisit_Continue;

                // A cursor from an included system/third-party header — or from a path the repo
                // explicitly excluded (e.g. a vendored SDK's sample code, via excludedPaths) — is
                // never interesting by default, and neither is anything nested under it (a syntactic
                // child is always in the same or a deeper-included file, never back in the repo).
                // Pruning here instead of just filtering it out of ConvertSymbol avoids walking the
                // entire subtree of every transitively-#include'd header for nothing.
                var cursorFile = GetCursorFilePath(cursor);
                if (cursorFile is not null && !AllowedFile(cursorFile))
                    return CXChildVisit_Continue;

                // Call/construct and base-specifier edges are detected independently of whether the
                // triggering cursor itself converts to a symbol — it never does (CallExpr and
                // CXXBaseSpecifier aren't declarations) — only whatever it *resolves to* needs to.
                if (kind == CXCursor_CallExpr)
                {
                    var resolved = ResolveReferenced(cursor);
                    if (resolved is not null)
                    {
                        var enclosing = FindEnclosing(cursor) ?? _modSym;
                        var refCursor = cursor.Referenced;
                        var edgeKind = refCursor.Kind == CXCursor_Constructor ? "constructs" : "calls";
                        AddEdge(enclosing, resolved, edgeKind, cursor);
                    }
                }

                if (kind == CXCursor_CXXBaseSpecifier && parent != default)
                {
                    var derived = ConvertSymbol(parent);
                    var baseSymbol = ResolveReferenced(cursor);
                    if (derived is not null && baseSymbol is not null)
                        AddEdge(derived, baseSymbol, "inherits", cursor);
                    return CXChildVisit_Continue;
                }

                var symbol = ConvertSymbol(cursor);
                if (symbol is not null && _seenSymbols.Add(symbol.Id))
                {
                    _symbols.Add(symbol);

                    if (symbol.Kind is not "translationunit" and not "namespace")
                        AddEdge(_modSym, symbol, "contains", cursor);

                    if (parent != default)
                    {
                        var parentSym = ConvertSymbol(parent);
                        if (parentSym is not null && _seenSymbols.Add(parentSym.Id))
                            _symbols.Add(parentSym);
                        if (parentSym is not null)
                            AddEdge(parentSym, symbol, "contains", cursor);
                    }
                }

                return CXChildVisit_Recurse;
            }
            catch (Exception ex)
            {
                _diagnostics.Add($"Clang cursor skip: {ex.Message}");
                return CXChildVisit_Continue;
            }
        }

        // Only cursor kinds that are actual declarations become symbols. Without this filter, every
        // expression/reference/operator/literal the visitor walks through — a += b, a string literal,
        // a TypeRef at a usage site — got its own SymbolRecord: a 9-line test file produced 37
        // "symbols" for 8 real declarations. Reference-kind cursors (TypeRef, DeclRefExpr, CallExpr,
        // CXXBaseSpecifier, ...) still matter for edges, resolved via cursor.Referenced, which points
        // back to the real declaration — they just never get a SymbolRecord of their own.
        private static readonly HashSet<CXCursorKind> DeclarationKinds =
        [
            CXCursor_FunctionDecl, CXCursor_CXXMethod, CXCursor_Constructor, CXCursor_Destructor,
            CXCursor_ClassDecl, CXCursor_ClassTemplate, CXCursor_StructDecl, CXCursor_UnionDecl,
            CXCursor_Namespace, CXCursor_EnumDecl, CXCursor_EnumConstantDecl, CXCursor_FieldDecl,
            CXCursor_VarDecl, CXCursor_ParmDecl, CXCursor_TypedefDecl, CXCursor_TypeAliasDecl,
            CXCursor_MacroDefinition, CXCursor_TranslationUnit
        ];

        private SymbolRecord? ConvertSymbol(CXCursor cursor)
        {
            try
            {
                if (cursor == default || cursor.IsNull) return null;
                if (!DeclarationKinds.Contains(cursor.Kind)) return null;
                var spelling = cursor.Spelling.ToString();
                if (string.IsNullOrWhiteSpace(spelling)) return null;

                var sourcePath = GetCursorFilePath(cursor);
                if (sourcePath is null || !AllowedFile(sourcePath)) return null;
                var relativeFile = Relative(_repo.RootPath, sourcePath);

                uint line = 0, column = 0;
                clang.getFileLocation(cursor.Location, null, &line, &column, null);
                if (line == 0) return null;

                var kind = GetSymbolKind(cursor);
                var qualified = GetQualifiedName(cursor);
                uint endLine = line, endColumn = column;
                try
                {
                    var extent = cursor.Extent;
                    clang.getFileLocation(clang.getRangeEnd(extent), null, &endLine, &endColumn, null);
                } catch { }

                // clang_getCursorUSR is Clang's own cross-translation-unit-stable, signature-aware
                // symbol identity (it disambiguates overloads by parameter types, unlike a bare
                // qualified name) — the C++ equivalent of what Roslyn's SymbolDisplayFormat gives the
                // C# module. Deliberately hashed WITHOUT a file prefix: a USR is the same for an
                // in-class declaration and its out-of-line .cpp definition by design, so salting it
                // with the (different) file each is seen in would silently re-split them back into two
                // unrelated symbols — defeating the entire point of using USR over line/column. It
                // comes back empty for cursors with no meaningful identity beyond their declaration
                // site (locals, parameters) — those fall back to file+position, same reasoning already
                // applied to TypeScript local variables: a local has no stable identity beyond where
                // it's declared anyway, so file+position IS its identity, not a workaround.
                var usr = cursor.Usr.ToString();
                var id = !string.IsNullOrEmpty(usr) ? Hash(usr) : Hash($"{relativeFile}|{kind}|{qualified}|{line}|{column}");
                return new SymbolRecord(id, _repo.Id, null, relativeFile, spelling, qualified, kind,
                    (int)line, (int)column, (int)endLine, (int)endColumn, qualified, Language(relativeFile), "cpp-clang");
            }
            catch { return null; }
        }

        private static unsafe string? GetCursorFilePath(CXCursor cursor)
        {
            void* filePtr = null;
            uint line = 0;
            clang.getFileLocation(cursor.Location, &filePtr, &line, null, null);
            if (line == 0 || filePtr == null) return null;
            var file = new CXFile { Handle = (IntPtr)filePtr };
            var name = clang.getFileName(file).ToString();
            return string.IsNullOrEmpty(name) ? null : Path.GetFullPath(name);
        }

        private SymbolRecord? ResolveReferenced(CXCursor callCursor)
        {
            try
            {
                var callee = callCursor.Referenced;
                if (callee == default || callee.IsNull) return null;
                return ConvertSymbol(callee);
            }
            catch { return null; }
        }

        // clang_getCursorLexicalParent is unreliable for statement/expression cursors — it's oriented
        // around declaration nesting (class members, namespaces), not "what function body is this
        // expression physically inside." Confirmed empirically: every call, nested or not, resolved
        // to _modSym instead of its real enclosing method with LexicalParent. SemanticParent is the
        // right tool here — GetQualifiedName already walks it successfully for the same nesting
        // (a local variable's qualified name correctly comes out as "Foo::greet::name", proving the
        // walk reaches the enclosing method). Only callable kinds qualify as "the enclosing symbol
        // making this call" — the first SemanticParent step for `int x = add(1, 2);` lands on the
        // VarDecl for `x` itself (a valid declaration kind), which would otherwise stop the climb one
        // level too early and misattribute the call to the local variable instead of the method it's
        // actually written inside.
        private static readonly HashSet<string> CallableKinds = ["function", "method", "constructor", "destructor"];

        private SymbolRecord? FindEnclosing(CXCursor cursor)
        {
            var parent = cursor.SemanticParent;
            while (parent != default && !parent.IsNull)
            {
                var sym = ConvertSymbol(parent);
                if (sym is not null && CallableKinds.Contains(sym.Kind))
                    return sym;
                parent = parent.SemanticParent;
            }
            return null;
        }

        private static string GetSymbolKind(CXCursor cursor) => cursor.Kind switch
        {
            CXCursor_FunctionDecl => "function",
            CXCursor_CXXMethod => "method",
            CXCursor_Constructor => "constructor",
            CXCursor_Destructor => "destructor",
            CXCursor_ClassDecl or CXCursor_ClassTemplate => "class",
            CXCursor_StructDecl => "struct",
            CXCursor_UnionDecl => "union",
            CXCursor_Namespace => "namespace",
            CXCursor_EnumDecl => "enum",
            CXCursor_EnumConstantDecl => "enum-member",
            CXCursor_FieldDecl => "field",
            CXCursor_VarDecl => "variable",
            CXCursor_ParmDecl => "parameter",
            CXCursor_TypedefDecl => "typedef",
            CXCursor_TypeAliasDecl => "using",
            CXCursor_MacroDefinition => "macro",
            CXCursor_TranslationUnit => "translationunit",
            _ => cursor.KindSpelling.ToString()
        };

        private static string GetQualifiedName(CXCursor cursor)
        {
            try
            {
                var parts = new Stack<string>();
                var c = cursor;
                // Stop at the translation-unit cursor rather than including it: its "spelling" is the
                // absolute path of the main file that was parsed, not a namespace/scope name — walking
                // past it leaked the parsing machine's own absolute filesystem path (e.g.
                // "C:\Projects\Game\...::demo") into every translation-unit-scoped (non-namespaced)
                // free function or global's qualified name, visible directly in agent-facing responses.
                while (c != default && !c.IsNull && c.Kind != CXCursor_TranslationUnit)
                {
                    var s = c.Spelling.ToString();
                    if (!string.IsNullOrWhiteSpace(s))
                        parts.Push(s);
                    c = c.SemanticParent;
                }
                return string.Join("::", parts);
            }
            catch { return cursor.Spelling.ToString() ?? "unknown"; }
        }

        // evidence is the cursor where this relationship is actually observed — the call expression
        // for "calls"/"constructs", the base-specifier for "inherits", the child's own declaration
        // for "contains" — not the source symbol's declaration site. Without it every call made from
        // inside the same function reported the identical (wrong) line: the enclosing function's own
        // declaration line, not each call's real location.
        private void AddEdge(SymbolRecord source, SymbolRecord target, string kind, CXCursor evidence)
        {
            var location = EvidenceLocation(evidence) ?? (File: source.FilePath, Line: source.StartLine, Column: source.StartColumn);
            var id = Hash($"{source.Id}|{target.Id}|{kind}|{location.File}|{location.Line}|{location.Column}");
            if (_seenRelationships.Add(id))
                _relationships.Add(new RelationshipRecord(id, _repo.Id, source.Id, target.Id, kind,
                    location.File, location.Line, location.Column, "semantic", source.Language, "cpp-clang"));
        }

        private unsafe (string File, int Line, int Column)? EvidenceLocation(CXCursor cursor)
        {
            if (cursor == default || cursor.IsNull) return null;
            var path = GetCursorFilePath(cursor);
            if (path is null || !AllowedFile(path)) return null;
            uint line = 0, column = 0;
            clang.getFileLocation(cursor.Location, null, &line, &column, null);
            if (line == 0) return null;
            return (Relative(_repo.RootPath, path), (int)line, (int)column);
        }

        private bool AllowedFile(string path) => IsAllowedFile(_repo, path);
    }

    private static IEnumerable<string> FindIncludeDirs(string root)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in new[] { "include", "src", "lib" })
        {
            var path = Path.Combine(root, candidate);
            if (Directory.Exists(path)) dirs.Add(path);
        }
        return dirs;
    }

    private static IEnumerable<string> EnumerateFiles(string root, IReadOnlyList<string>? excluded) =>
        PathExclusions.EnumerateFiles(root, "*.*", excluded).Where(IsSource);

    private static bool IsSource(string path) =>
        path.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".cc", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".cxx", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".c", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".h", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".hpp", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".hxx", StringComparison.OrdinalIgnoreCase);

    private static string Language(string path) =>
        path.EndsWith(".c", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".h", StringComparison.OrdinalIgnoreCase) ? "c" : "cpp";

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static bool Excluded(string path, IReadOnlyList<string>? extra = null) => PathExclusions.IsExcluded(path, extra);
    private static bool IsUnderRoot(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
    private static string Generation(string repositoryId) => Hash($"{repositoryId}|{DateTimeOffset.UtcNow:yyyyMMddHHmm}")[..16];
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
}
