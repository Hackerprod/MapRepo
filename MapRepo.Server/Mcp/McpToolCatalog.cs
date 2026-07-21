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
            ArrayProperty("excludedPaths", "Extra path substrings (case-insensitive) to skip during indexing and watching, beyond the built-in .git/node_modules/bin/obj/dist/build/coverage list — e.g. a project-specific build-verification scratch folder.", "string")
        ], ["rootPath"])),
        new("list_repositories", "List every registered repository with its index status. Call this first to discover repositoryId values.", Schema([], [])),
        new("repository_status", "Return index generation, counts, diagnostics and watcher state", RepoSchema()),
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
        new("repo_overview", "Token-cheap orientation map: symbol/edge counts by kind, language and project, top files and the most connected hub symbols. Ideal first call on an unknown repository.", RepoSchema()),
        new("search_symbols", "Find symbols with exact source file and line evidence. Supports kind and path filters.", Schema([
            StringProperty("repositoryId", "Repository id."),
            StringProperty("query", "Symbol name or source text to find."),
            IntegerProperty("limit", "Maximum result count."),
            StringProperty("kind", "Optional symbol kind filter, e.g. Method, NamedType, class, property."),
            StringProperty("pathContains", "Optional substring the file path must contain."),
            BooleanProperty("includeTextual", "Include textual-evidence matches from string literals (default false)."),
            BooleanProperty("includeRelationships", "Attach up to 24 edges per result (default false; use get_symbol/find_* instead).")
        ], ["repositoryId", "query"])),
        new("get_symbol", "Full detail for one symbol: record, incoming and outgoing edges, and neighbor symbols", Schema([
            StringProperty("repositoryId", "Repository id."),
            StringProperty("symbolId", "Symbol id from search_symbols or graph results."),
            IntegerProperty("limit", "Maximum edges per direction (default 40; hub symbols can have hundreds — raise only when outgoingTruncated/incomingTruncated come back true).")
        ], ["repositoryId", "symbolId"])),
        new("file_outline", "All declarations in one file ordered by line — read this instead of the file to save tokens", Schema([
            StringProperty("repositoryId", "Repository id."),
            StringProperty("filePath", "Repository-relative file path with forward slashes.")
        ], ["repositoryId", "filePath"])),
        new("list_files", "List indexed files with their declaration counts; optional substring filter", Schema([
            StringProperty("repositoryId", "Repository id."),
            StringProperty("contains", "Optional substring the file path must contain."),
            IntegerProperty("limit", "Maximum file count.")
        ], ["repositoryId"])),
        new("get_source", "Read an exact line range (max 400 lines) from a repository file. Use after search_symbols/file_outline so only relevant lines are fetched.", Schema([
            StringProperty("repositoryId", "Repository id."),
            StringProperty("filePath", "Repository-relative file path."),
            IntegerProperty("startLine", "1-based first line (default 1)."),
            IntegerProperty("endLine", "1-based last line (default startLine+60).")
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
        new("find_callers", "Find methods that call a symbol", SymbolLookupSchema()),
        new("find_callees", "Find symbols called by a method", SymbolLookupSchema()),
        new("find_references", "Find reference edges around a symbol", SymbolLookupSchema()),
        new("get_graph", "Return a bounded symbol graph for Canvas or agent reasoning", GraphLookupSchema())
    ];

    private static object RepoSchema() => Schema([StringProperty("repositoryId", "Repository id.")], ["repositoryId"]);

    // find_callers/find_callees/find_references each hard-code their own single edge kind
    // (calls/calls/references) in the dispatcher — edgeKinds would be a no-op parameter for them.
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
