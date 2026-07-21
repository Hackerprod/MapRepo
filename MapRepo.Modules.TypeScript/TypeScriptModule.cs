using System.Security.Cryptography;
using System.Text;
using MapRepo.Core;

namespace MapRepo.Modules.TypeScript;

public sealed class TypeScriptModule : IRepositoryLanguageModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        "typescript-syntax", "TypeScript / TSX", ["typescript", "tsx", "javascript", "jsx"], "1.0.0", false);

    public bool CanAnalyze(string filePath) => IsSource(filePath);

    public async Task<AnalysisSnapshot> AnalyzeAsync(AnalysisRequest request)
    {
        var diagnostics = new List<string>();
        var files = EnumerateFiles(request.Repository.RootPath);
        var units = new List<FileUnit>();
        foreach (var path in files)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            try
            {
                var source = await File.ReadAllTextAsync(path, request.CancellationToken);
                units.Add(ParseFile(request.Repository, path, source));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { diagnostics.Add($"TypeScript read failed for {Relative(request.Repository.RootPath, path)}: {ex.Message}"); }
        }

        var allSymbols = units.SelectMany(u => u.Symbols).ToArray();
        var byName = allSymbols.Where(s => s.Kind is not "module")
            .GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var byFile = units.ToDictionary(u => u.FilePath, StringComparer.OrdinalIgnoreCase);
        var edges = new Dictionary<string, RelationshipRecord>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            foreach (var import in unit.Imports)
            {
                var targetPath = ResolveImport(unit.FilePath, import.Path, byFile.Keys);
                if (targetPath is null || !byFile.TryGetValue(targetPath, out var target)) continue;
                AddEdge(edges, Edge(unit.ModuleSymbol, target.ModuleSymbol, "imports", unit.FilePath, import.Line, import.Column, "syntax"), request.Repository.Id);
            }
            foreach (var declaration in unit.Declarations)
            {
                if (declaration.Extends is not null && byName.TryGetValue(declaration.Extends, out var baseSymbol))
                    AddEdge(edges, Edge(declaration.Symbol, baseSymbol, "inherits", unit.FilePath, declaration.Line, declaration.Column, "syntax"), request.Repository.Id);
                foreach (var implemented in declaration.Implements)
                    if (byName.TryGetValue(implemented, out var interfaceSymbol))
                        AddEdge(edges, Edge(declaration.Symbol, interfaceSymbol, "implements", unit.FilePath, declaration.Line, declaration.Column, "syntax"), request.Repository.Id);
            }
            foreach (var call in unit.Calls)
            {
                if (!byName.TryGetValue(call.Name, out var target)) continue;
                var source = Enclosing(unit.Declarations, call.Offset)?.Symbol ?? unit.ModuleSymbol;
                var kind = call.IsConstruction ? "constructs" : "calls";
                AddEdge(edges, Edge(source, target, kind, unit.FilePath, call.Line, call.Column, "syntax"), request.Repository.Id);
            }
            foreach (var reference in unit.References)
            {
                if (!byName.TryGetValue(reference.Name, out var target)) continue;
                var source = Enclosing(unit.Declarations, reference.Offset)?.Symbol ?? unit.ModuleSymbol;
                if (source.Id == target.Id) continue;
                AddEdge(edges, Edge(source, target, "references", unit.FilePath, reference.Line, reference.Column, "syntax"), request.Repository.Id);
            }
        }

        return new AnalysisSnapshot(request.Repository.Id, Generation(request.Repository.Id), allSymbols,
            edges.Values.ToArray(), diagnostics.Distinct().Take(100).ToArray(), DateTimeOffset.UtcNow);
    }

    private static FileUnit ParseFile(RepositoryDefinition repository, string path, string source)
    {
        var file = Relative(repository.RootPath, path);
        var tokens = Scan(source);
        var declarations = new List<Declaration>();
        var symbols = new List<SymbolRecord>();
        var module = new SymbolRecord(Hash($"{file}|module"), repository.Id, null, file, Path.GetFileName(path), file,
            "module", 1, 1, Math.Max(1, source.Count(c => c == '\n') + 1), 1, file, Language(path), "typescript-syntax");
        symbols.Add(module);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var cursor = i;
            while (cursor < tokens.Count && IsModifier(tokens[cursor].Text)) cursor++;
            if (cursor >= tokens.Count) break;
            var keyword = tokens[cursor].Text;
            if (keyword is "class" or "interface" or "enum" or "type" or "function" or "namespace")
            {
                if (cursor + 1 >= tokens.Count || !IsIdentifier(tokens[cursor + 1].Text)) continue;
                var name = tokens[cursor + 1];
                var end = FindDeclarationEnd(tokens, cursor + 1);
                var declaration = NewDeclaration(repository, file, name.Text, keyword, name, end, source, tokens, cursor);
                declarations.Add(declaration); symbols.Add(declaration.Symbol);
                if (keyword is "class" or "interface") AddClassMembers(repository, file, source, tokens, cursor, end, declaration, declarations, symbols);
                else if (keyword == "type")
                {
                    var objectOpen = FindObjectBody(tokens, cursor + 1);
                    if (objectOpen >= 0) AddObjectMembers(repository, file, source, tokens, objectOpen, end, declaration, declarations, symbols);
                }
                i = cursor + 1;
                continue;
            }
            if (keyword is "const" or "let" or "var")
            {
                if (cursor + 1 < tokens.Count && IsIdentifier(tokens[cursor + 1].Text))
                {
                    var name = tokens[cursor + 1];
                    var objectOpen = FindObjectBody(tokens, cursor + 1);
                    var end = objectOpen >= 0 ? FindMatchingBrace(tokens, objectOpen) : FindStatementEnd(tokens, cursor + 1);
                    var declaration = NewDeclaration(repository, file, name.Text, "variable", name, end, source, tokens, cursor);
                    declarations.Add(declaration); symbols.Add(declaration.Symbol); i = cursor + 1;
                    if (objectOpen >= 0)
                        AddObjectMembers(repository, file, source, tokens, objectOpen, end, declaration, declarations, symbols);
                }
            }
        }
        var imports = new List<ImportRef>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text is "import" or "export")
            {
                for (var j = i + 1; j < Math.Min(tokens.Count, i + 20); j++)
                {
                    if (tokens[j].Text is "from" && j + 1 < tokens.Count) { imports.Add(new ImportRef(tokens[j + 1].Text.Trim('"', '\''), tokens[j + 1].Line, tokens[j + 1].Column)); break; }
                    if (tokens[j].Text.StartsWith('"') || tokens[j].Text.StartsWith('\'')) { imports.Add(new ImportRef(tokens[j].Text.Trim('"', '\''), tokens[j].Line, tokens[j].Column)); break; }
                    if (tokens[j].Text is ";") break;
                }
            }
        }
        var calls = new List<CallRef>(); var references = new List<NameRef>();
        var declarationOffsets = declarations.Select(d => d.Offset).ToHashSet();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!IsIdentifier(tokens[i].Text) || declarationOffsets.Contains(tokens[i].Offset)) continue;
            var previous = i > 0 ? tokens[i - 1].Text : string.Empty;
            var next = i + 1 < tokens.Count ? tokens[i + 1].Text : string.Empty;
            if (next == "(" && !ControlWords.Contains(tokens[i].Text)) calls.Add(new CallRef(tokens[i].Text, tokens[i].Offset, tokens[i].Line, tokens[i].Column, previous == "new"));
            else if (previous != "." || next != ":") references.Add(new NameRef(tokens[i].Text, tokens[i].Offset, tokens[i].Line, tokens[i].Column));
        }
        return new FileUnit(file, module, symbols, declarations, imports, calls, references);
    }

    private static void AddClassMembers(RepositoryDefinition repository, string file, string source, List<Token> tokens, int start, int end, Declaration parent, List<Declaration> declarations, List<SymbolRecord> symbols)
    {
        for (var i = start + 2; i < end; i++)
        {
            if (!IsIdentifier(tokens[i].Text) || IsModifier(tokens[i].Text)) continue;
            var next = i + 1 < end ? tokens[i + 1].Text : string.Empty;
            if (next != "(" && next != ":") continue;
            var kind = next == "(" ? (tokens[i].Text == "constructor" ? "constructor" : "method") : "property";
            var memberEnd = FindMemberEnd(tokens, i, end);
            var member = NewDeclaration(repository, file, tokens[i].Text, kind, tokens[i], memberEnd, source, tokens, i);
            declarations.Add(member); symbols.Add(member.Symbol); i = Math.Max(i, memberEnd - 1);
        }
    }

    // Object/type literals are a first-class TS API shape (for example `const Msg: { ... }`).
    // Index their members just like interface properties so generated declaration maps remain queryable.
    private static void AddObjectMembers(RepositoryDefinition repository, string file, string source, List<Token> tokens, int open, int end, Declaration parent, List<Declaration> declarations, List<SymbolRecord> symbols)
    {
        for (var i = open + 1; i < end; i++)
        {
            if (!IsIdentifier(tokens[i].Text) || IsModifier(tokens[i].Text)) continue;
            var next = i + 1 < end ? tokens[i + 1].Text : string.Empty;
            if (next != ":" && next != "(") continue;
            var kind = next == "(" ? "method" : "property";
            var memberEnd = FindMemberEnd(tokens, i, end);
            var member = NewDeclaration(repository, file, tokens[i].Text, kind, tokens[i], memberEnd, source, tokens, i);
            declarations.Add(member); symbols.Add(member.Symbol); i = Math.Max(i, memberEnd - 1);
        }
    }

    private static Declaration NewDeclaration(RepositoryDefinition repository, string file, string name, string kind, Token token, int end, string source, List<Token> tokens, int cursor)
    {
        var start = token; var endToken = end < tokens.Count ? tokens[end] : start;
        var qualified = name; var symbol = new SymbolRecord(Hash($"{file}|{kind}|{name}|{start.Line}|{start.Column}"), repository.Id, null, file, name, qualified, kind,
            start.Line, start.Column, endToken.Line, endToken.Column, Signature(source, start.Offset, endToken.Offset), Language(file), "typescript-syntax");
        var extendsName = (kind is "class" or "interface") ? FindTypeAfter(tokens, cursor + 2, "extends") : null;
        var implementsNames = (kind == "class") ? FindTypesAfter(tokens, cursor + 2, "implements") : [];
        return new Declaration(symbol, name, kind, start.Offset, Math.Max(start.Offset, endToken.Offset), start.Line, start.Column, extendsName, implementsNames);
    }

    private static string? FindTypeAfter(List<Token> tokens, int start, string keyword)
    { for (var i = start; i < Math.Min(tokens.Count, start + 30); i++) if (tokens[i].Text == keyword && i + 1 < tokens.Count) return LastName(tokens[i + 1].Text); return null; }
    private static IReadOnlyList<string> FindTypesAfter(List<Token> tokens, int start, string keyword)
    { for (var i = start; i < Math.Min(tokens.Count, start + 30); i++) if (tokens[i].Text == keyword) return tokens.Skip(i + 1).TakeWhile(t => t.Text != "{").Where(t => IsIdentifier(t.Text)).Select(t => t.Text).ToArray(); return []; }
    private static string LastName(string value) => value.Split('.').Last();
    private static int FindDeclarationEnd(List<Token> tokens, int start) { var brace = tokens.FindIndex(start, t => t.Text == "{"); if (brace < 0) return FindStatementEnd(tokens, start); var depth = 0; for (var i = brace; i < tokens.Count; i++) { if (tokens[i].Text == "{") depth++; else if (tokens[i].Text == "}" && --depth == 0) return i; } return tokens.Count - 1; }
    private static int FindObjectBody(List<Token> tokens, int start)
    {
        for (var i = start + 1; i < Math.Min(tokens.Count, start + 12); i++)
            if (tokens[i].Text == "{") return i;
            else if (tokens[i].Text is ";" or "=") { if (tokens[i].Text == "=") continue; break; }
        return -1;
    }
    private static int FindMatchingBrace(List<Token> tokens, int open)
    {
        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            if (tokens[i].Text == "{") depth++;
            else if (tokens[i].Text == "}" && --depth == 0) return i;
        }
        return tokens.Count - 1;
    }
    private static int FindMemberEnd(List<Token> tokens, int start, int enclosingEnd)
    {
        var parens = 0;
        for (var i = start; i < enclosingEnd; i++)
        {
            if (tokens[i].Text == "(") parens++;
            else if (tokens[i].Text == ")") parens--;
            else if (parens == 0 && tokens[i].Text is ";" or ",") return i;
            else if (parens == 0 && tokens[i].Text == "{") return FindDeclarationEnd(tokens, i);
        }
        return Math.Max(start, enclosingEnd - 1);
    }
    private static int FindStatementEnd(List<Token> tokens, int start) { for (var i = start; i < tokens.Count; i++) if (tokens[i].Text is ";" or "}") return i; return tokens.Count - 1; }
    private static Declaration? Enclosing(IReadOnlyList<Declaration> declarations, int offset) => declarations.Where(d => d.Offset <= offset && d.EndOffset >= offset).OrderBy(d => d.EndOffset - d.Offset).FirstOrDefault();

    private static List<Token> Scan(string source)
    {
        var result = new List<Token>(); var i = 0; var line = 1; var column = 1;
        while (i < source.Length)
        {
            var c = source[i]; if (char.IsWhiteSpace(c)) { if (c == '\n') { line++; column = 1; } else column++; i++; continue; }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/') { while (i < source.Length && source[i] != '\n') { i++; column++; } continue; }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*') { i += 2; column += 2; while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) { if (source[i] == '\n') { line++; column = 1; } else column++; i++; } i += Math.Min(2, source.Length - i); column += 2; continue; }
            var start = i; var sl = line; var sc = column;
            if (char.IsLetter(c) || c is '_' or '$') { i++; column++; while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] is '_' or '$')) { i++; column++; } result.Add(new Token(source[start..i], start, sl, sc)); continue; }
            if (c is '\'' or '"' or '`') { var quote = c; i++; column++; while (i < source.Length) { if (source[i] == '\\') { i += Math.Min(2, source.Length - i); column += 2; continue; } if (source[i] == quote) { i++; column++; break; } if (source[i] == '\n') { line++; column = 1; } else column++; i++; } result.Add(new Token(source[start..i], start, sl, sc)); continue; }
            var two = i + 1 < source.Length ? source.Substring(i, 2) : string.Empty; var text = two is "=>" or "?." or "??" or "==" or "&&" or "||" ? two : c.ToString(); i += text.Length; column += text.Length; result.Add(new Token(text, start, sl, sc));
        }
        return result;
    }

    private static IEnumerable<string> EnumerateFiles(string root) => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories).Where(IsSource).Where(path => !Excluded(path));
    private static bool Excluded(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
        || p.Equals("dist", StringComparison.OrdinalIgnoreCase) || p.Equals("build", StringComparison.OrdinalIgnoreCase)
        || p.Equals("coverage", StringComparison.OrdinalIgnoreCase) || p.Equals(".git", StringComparison.OrdinalIgnoreCase)
        || p.Equals(".tmp", StringComparison.OrdinalIgnoreCase) || p.Equals("packages", StringComparison.OrdinalIgnoreCase)
        || p.Equals("Data", StringComparison.OrdinalIgnoreCase) || p.Equals("bin", StringComparison.OrdinalIgnoreCase)
        || p.Equals("obj", StringComparison.OrdinalIgnoreCase));
    private static bool IsSource(string path) => path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase);
    private static string Language(string path) => path.EndsWith("x", StringComparison.OrdinalIgnoreCase) ? "tsx" : path.EndsWith("ts", StringComparison.OrdinalIgnoreCase) ? "typescript" : "javascript";
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string Signature(string source, int start, int end) => source[Math.Clamp(start, 0, source.Length)..Math.Clamp(Math.Max(start, end), 0, source.Length)].Split('\n')[0].Trim()[..Math.Min(240, source[Math.Clamp(start, 0, source.Length)..Math.Clamp(Math.Max(start, end), 0, source.Length)].Split('\n')[0].Trim().Length)];
    private static bool IsIdentifier(string value) => value.Length > 0 && (char.IsLetter(value[0]) || value[0] is '_' or '$');
    private static bool IsModifier(string value) => value is "export" or "default" or "declare" or "abstract" or "async" or "public" or "private" or "protected" or "readonly" or "static";
    private static readonly HashSet<string> ControlWords = ["if", "for", "while", "switch", "catch", "with", "function"];
    private static string? ResolveImport(string file, string import, IEnumerable<string> files)
    {
        if (!import.StartsWith('.')) return null;
        var basePath = NormalizeRelative(Path.Combine(Path.GetDirectoryName(file) ?? string.Empty, import).Replace('\\', '/'));
        foreach (var candidate in new[] { basePath, basePath + ".ts", basePath + ".tsx", basePath + ".js", basePath + ".jsx", $"{basePath.TrimEnd('/')}/index.ts" })
        {
            var normalized = candidate.Replace("./", string.Empty, StringComparison.Ordinal);
            var found = files.FirstOrDefault(f => string.Equals(f.Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase));
            if (found is not null) return found;
        }
        return null;
    }
    private static string NormalizeRelative(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == ".." && parts.Count > 0) parts.RemoveAt(parts.Count - 1);
            else if (part != "..") parts.Add(part);
        }
        return string.Join('/', parts);
    }
    private static RelationshipRecord Edge(SymbolRecord source, SymbolRecord target, string kind, string file, int line, int column, string confidence) => new(Hash($"{source.Id}|{target.Id}|{kind}|{file}|{line}|{column}"), source.RepositoryId, source.Id, target.Id, kind, file, line, column, confidence, source.Language, "typescript-syntax");
    private static void AddEdge(Dictionary<string, RelationshipRecord> edges, RelationshipRecord edge, string repositoryId) => edges.TryAdd(edge.Id, edge);
    private static string Generation(string repositoryId) => Hash($"{repositoryId}|{DateTimeOffset.UtcNow:yyyyMMddHHmm}")[..16];
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];

    private sealed record Token(string Text, int Offset, int Line, int Column);
    private sealed record ImportRef(string Path, int Line, int Column);
    private sealed record CallRef(string Name, int Offset, int Line, int Column, bool IsConstruction);
    private sealed record NameRef(string Name, int Offset, int Line, int Column);
    private sealed record Declaration(SymbolRecord Symbol, string Name, string Kind, int Offset, int EndOffset, int Line, int Column, string? Extends, IReadOnlyList<string> Implements);
    private sealed record FileUnit(string FilePath, SymbolRecord ModuleSymbol, IReadOnlyList<SymbolRecord> Symbols, IReadOnlyList<Declaration> Declarations, IReadOnlyList<ImportRef> Imports, IReadOnlyList<CallRef> Calls, IReadOnlyList<NameRef> References);
}
