namespace MapRepo.Server;

/// <summary>A single MCP tool's advertised shape. A real type instead of an anonymous object so
/// callers (e.g. `/info`) can read <see cref="Name"/> directly instead of reflecting over it.</summary>
public sealed record McpToolDefinition(string Name, string Description, object InputSchema);

/// <summary>The fixed list of tools this server advertises over MCP (`tools/list`) — the schema
/// contract consumed by agents. Kept separate from dispatch (<see cref="McpDispatcher"/>) so the
/// "what tools exist" list and the "how a call executes" logic can be read independently.</summary>
public static class McpToolCatalog
{
    public static IReadOnlyList<McpToolDefinition> All { get; } =
    [
        new("open_repository", "Register/open a repository, index it and start its live file watcher. Reuses the existing index unless reindex=true.", Schema([
            StringProperty("rootPath", "Absolute repository root path."),
            StringProperty("id", "Optional stable repository id (defaults to the folder name)."),
            StringProperty("solutionPath", "Optional .sln/.slnx/.csproj path used by the C# module."),
            ArrayProperty("enabledModules", "Optional module filter such as csharp-roslyn or typescript-syntax.", "string"),
            BooleanProperty("reindex", "Force a full reindex even when the stored index is populated."),
            BooleanProperty("includeTextualEvidence", "Also index identifier-like words found in string literals (default false; adds noise, ~30% more rows)."),
            StringProperty("tsEngine", "TypeScript analysis engine: auto (default; semantic when Node+typescript are available), semantic, or syntax."),
            ArrayProperty("excludedPaths", "Extra path substrings (case-insensitive) to skip during indexing and watching, beyond the built-in .git/node_modules/bin/obj/dist/build/coverage list — e.g. a project-specific build-verification scratch folder.", "string"),
            BooleanProperty("allowExternalSymbols", "C# only: when the .sln references a project outside rootPath (a sibling repo via ProjectReference), also index its symbols/edges. Default false keeps every repository's index isolated to its own files.")
        ], ["rootPath"])),
        new("list_repositories", "List every registered repository with a compact status (id, rootPath, symbols, relationships, indexing, diagnosticCount, textualEvidence). Call this first to discover repositoryId values.", Schema([
            BooleanProperty("includeDiagnostics", "Return the full definition and diagnostics text per repository instead of the compact default. Expensive on repositories with many MSBuild/analysis warnings.")
        ], [])),
        new("repository_status", "Return index generation, counts, diagnostics and watcher state. diagnostics is actual problems (MSBuild/analysis errors); routine per-module index stats (file/symbol/edge counts) come back separately in indexSummary, not mixed in.", RepoSchema()),
        new("reindex_repository", "Force a full reindex of a registered repository", RepoSchema()),
        new("close_repository", "Stop the watcher and release the in-memory session. Data and registration are kept.", RepoSchema()),
        new("exclude_path", "Add a path substring to a repository's exclude list (on top of the built-in .git/node_modules/bin/obj/dist/build/coverage list) and force a full reindex so previously indexed symbols from that path are purged. Use for project-specific scratch/generated folders that keep polluting search results.", Schema([
            StringProperty("repositoryId", "Repository id."),
            StringProperty("path", "Case-insensitive substring to match anywhere in a file's path, e.g. \"verify-build\" or \"scripts/generated\".")
        ], ["repositoryId", "path"])),
        new("remove_repository", "Unregister a repository; optionally delete its database", Schema([
            StringProperty("repositoryId", "Repository id."),
            BooleanProperty("deleteData", "Also delete the per-repository database directory.")
        ], ["repositoryId"])),
        new("repo_overview", "Token-cheap orientation map: symbol/edge counts by kind, language and project, top files and the most connected hub symbols. Ideal first call on an unknown repository. Cached per index generation, so only the first call after (re)indexing is slow.", Schema([
            StringProperty("repositoryId", "Repository id."),
            BooleanProperty("includeGenerated", "Include tool-generated files (designer/.g.cs/.pb.cs/AssemblyInfo/obj) in topFiles and hubs. Default false keeps the orientation map focused on hand-written code.")
        ], ["repositoryId"])),
        new("search_symbols", "Find symbols with exact source file and line evidence. Supports kind and path filters. Only searches declared symbols by default — pass includeTextual:true to also search string-literal text (config names, error messages, etc.), which requires the repository to have been indexed with includeTextualEvidence:true; a repository indexed without it returns an explicit diagnostic telling you to fall back to a plain-text tool (grep/rg) instead of silently coming back empty.", Schema([
            StringProperty("repositoryId", "Repository id."),
            StringProperty("query", "Symbol name or source text to find."),
            IntegerProperty("limit", "Maximum result count."),
            StringProperty("kind", "Optional symbol kind filter, e.g. Method, NamedType, class, property."),
            StringProperty("pathContains", "Optional substring the file path must contain."),
            BooleanProperty("includeTextual", "Also search string-literal text, not just declared symbols (default false). Only finds anything if this repository was indexed with includeTextualEvidence:true (see open_repository) — otherwise use grep/rg for literal text."),
            BooleanProperty("includeRelationships", "Attach up to 24 edges per result (default false; use get_symbol/find_* instead).")
        ], ["repositoryId", "query"])),
        new("get_symbol", "Full detail for one symbol: record, incoming and outgoing edges, and neighbor symbols", Schema([
            StringProperty("repositoryId", "Repository id."),
            StringProperty("symbolId", "Symbol id from search_symbols or graph results."),
            IntegerProperty("limit", "Maximum edges per direction (default 40; hub symbols can have hundreds — raise only when outgoingTruncated/incomingTruncated come back true).")
        ], ["repositoryId", "symbolId"])),
        new("file_outline", "All declarations in one file ordered by line — read this instead of the file to save tokens. A huge generated file (thousands of flat declarations) can still cost tens of thousands of tokens; use maxSymbols and/or compact to cut that down.", Schema([
            StringProperty("repositoryId", "Repository id."),
            StringProperty("filePath", "Repository-relative file path with forward slashes."),
            IntegerProperty("maxSymbols", "Maximum declarations to return (default 500, the hard cap)."),
            BooleanProperty("compact", "Return only name/kind/startLine per declaration (drop id/qualifiedName/project/signature) — for orienting in a huge generated file at a fraction of the token cost.")
        ], ["repositoryId", "filePath"])),
        new("list_files", "List indexed files with their declaration counts; optional substring filter", Schema([
            StringProperty("repositoryId", "Repository id."),
            StringProperty("contains", "Optional substring the file path must contain."),
            IntegerProperty("limit", "Maximum file count.")
        ], ["repositoryId"])),
        new("get_source", "Read an exact line range (max 400 lines) from a repository file. Use after search_symbols/file_outline so only relevant lines are fetched. Rejects a nonsensical range (startLine>endLine, or startLine past EOF) as an error by default instead of silently returning something else.", Schema([
            StringProperty("repositoryId", "Repository id."),
            StringProperty("filePath", "Repository-relative file path."),
            IntegerProperty("startLine", "1-based first line (default 1)."),
            IntegerProperty("endLine", "1-based last line (default startLine+60)."),
            BooleanProperty("clamp", "When true, auto-correct an invalid range (startLine>endLine, or startLine past EOF) instead of returning an error.")
        ], ["repositoryId", "filePath"])),
        new("batch", "Execute up to 10 tool calls in one request (e.g. search_symbols then get_source). Results return in order; a failing call does not abort the rest. The combined response is capped (~200KB) so one batch can't blow past what fits in a reply — if the cap is hit, the response carries truncated:true and nextIndex: resend the remaining calls in a new batch starting at that index.", new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["calls"] = new
                {
                    type = "array",
                    description = "Tool invocations to run sequentially.",
                    items = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["tool"] = new { type = "string", description = "Tool name (any tool except batch)." },
                            ["arguments"] = new { type = "object", description = "Arguments for that tool." }
                        },
                        required = new[] { "tool" }
                    },
                    minItems = 1,
                    maxItems = 10
                }
            },
            required = new[] { "calls" },
            additionalProperties = false
        }),
        new("find_callers", "Find methods that call a symbol. Defaults to calls edges; pass edgeKinds to widen (e.g. [\"calls\",\"constructs\"]), or wide:true for calls+references (TypeScript in particular often models real usage — a handler table, a property holding a function — as references, not calls).", CallGraphLookupSchema()),
        new("find_callees", "Find symbols called or constructed by a method. Defaults to calls+constructs edges; pass edgeKinds to narrow or widen further, or wide:true to also include references (see find_callers).", CallGraphLookupSchema()),
        new("find_references", "Find reference edges around a symbol", SymbolLookupSchema()),
        new("get_graph", "Return a bounded symbol graph for Canvas or agent reasoning", GraphLookupSchema())
    ];

    private static object RepoSchema() => Schema([StringProperty("repositoryId", "Repository id.")], ["repositoryId"]);

    // find_references hard-codes "references" in the dispatcher — edgeKinds would be a no-op there.
    private static object SymbolLookupSchema() => Schema([
        StringProperty("repositoryId", "Repository id."),
        StringProperty("symbolId", "Symbol id from search_symbols or graph results."),
        IntegerProperty("depth", "Graph traversal depth."),
        IntegerProperty("limit", "Maximum node/edge count.")
    ], ["repositoryId", "symbolId"]);

    private static object GraphLookupSchema() => Schema([
        StringProperty("repositoryId", "Repository id."),
        StringProperty("symbolId", "Symbol id from search_symbols or graph results."),
        IntegerProperty("depth", "Graph traversal depth."),
        IntegerProperty("limit", "Maximum node/edge count."),
        ArrayProperty("edgeKinds", "Only traverse these edge kinds (calls, references, contains, constructs, inherits, implements, imports). Unset = all.", "string")
    ], ["repositoryId", "symbolId"]);

    // find_callers/find_callees only: an explicit edgeKinds always wins over wide, so passing both
    // is never ambiguous — wide only changes what the *default* (no edgeKinds given) resolves to.
    private static object CallGraphLookupSchema() => Schema([
        StringProperty("repositoryId", "Repository id."),
        StringProperty("symbolId", "Symbol id from search_symbols or graph results."),
        IntegerProperty("depth", "Graph traversal depth."),
        IntegerProperty("limit", "Maximum node/edge count."),
        ArrayProperty("edgeKinds", "Only traverse these edge kinds (calls, references, contains, constructs, inherits, implements, imports). Overrides wide when set.", "string"),
        BooleanProperty("wide", "Ignored if edgeKinds is set. When true, broadens the default edge set to also include references — useful in TypeScript where a real call site (a handler table, a property holding a function) can be modeled as a reference instead of a call. Default false.")
    ], ["repositoryId", "symbolId"]);

    private static object Schema(IEnumerable<KeyValuePair<string, object>> properties, string[] required) => new
    {
        type = "object",
        properties = properties.ToDictionary(x => x.Key, x => x.Value),
        required,
        additionalProperties = false
    };

    private static KeyValuePair<string, object> StringProperty(string name, string description) =>
        new(name, new { type = "string", description });

    private static KeyValuePair<string, object> IntegerProperty(string name, string description) =>
        new(name, new { type = "integer", description, minimum = 1 });

    private static KeyValuePair<string, object> BooleanProperty(string name, string description) =>
        new(name, new { type = "boolean", description });

    private static KeyValuePair<string, object> ArrayProperty(string name, string description, string itemType) =>
        new(name, new { type = "array", description, items = new { type = itemType } });
}
