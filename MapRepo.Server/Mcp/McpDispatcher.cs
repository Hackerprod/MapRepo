using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MapRepo.Core;

namespace MapRepo.Server;

/// <summary>
/// Owns the MCP JSON-RPC contract: framing (initialize/ping/tools list/tools call, both the
/// Streamable-HTTP and legacy SSE transports route through <see cref="HandleRequestAsync"/>) and
/// the per-tool dispatch (<see cref="DispatchToolAsync"/>). Endpoint mappings in Program.cs only
/// handle HTTP transport concerns (SSE vs plain body, status codes) and delegate here.
/// </summary>
public sealed class McpDispatcher
{
    // A batch of 10 calls (get_source, search_symbols, ...) can otherwise return enough combined
    // JSON to trigger the calling agent's own context compaction. Bounding one batch response and
    // handing back a resume cursor (nextIndex) keeps every response individually small instead.
    private const int BatchByteBudget = 200_000;

    private readonly RepositorySessionManager _manager;
    private readonly IRepositoryStore _store;

    public McpDispatcher(RepositorySessionManager manager, IRepositoryStore store)
    {
        _manager = manager;
        _store = store;
    }

    public JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Handles one JSON-RPC request object and returns the response object to serialize.
    /// Shared by the `/mcp` Streamable-HTTP endpoint and the legacy `/message` SSE transport.</summary>
    public async Task<object> HandleRequestAsync(JsonElement request, CancellationToken cancellationToken)
    {
        var id = request.TryGetProperty("id", out var requestId) ? requestId : (JsonElement?)null;
        var method = request.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(method)) return RpcError(id, -32600, "Invalid Request");
        return method switch
        {
            "initialize" => RpcResult(id, new { protocolVersion = "2024-11-05", serverInfo = new { name = "map-repo-server", version = "2.0.0" }, capabilities = new { tools = new { listChanged = false } } }),
            "ping" => RpcResult(id, new { }),
            "tools/list" => RpcResult(id, new { tools = McpToolCatalog.All }),
            "tools/call" => await HandleToolCallAsync(id, request, cancellationToken),
            _ => RpcError(id, -32601, $"Method not found: {method}")
        };
    }

    private async Task<object> HandleToolCallAsync(JsonElement? id, JsonElement request, CancellationToken cancellationToken)
    {
        var parameters = request.TryGetProperty("params", out var p) ? p : throw new JsonException("Missing params");
        var tool = parameters.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : throw new JsonException("Missing tool name");
        var arguments = parameters.TryGetProperty("arguments", out var args) ? args : JsonDocument.Parse("{}").RootElement;
        try
        {
            // content[0].text is the sole payload: no tool declares outputSchema, so a parallel
            // structuredContent field (the MCP spec's pairing for outputSchema-typed results)
            // would only double every response's size for no client benefit.
            var result = await DispatchToolAsync(tool, arguments, cancellationToken);
            return RpcResult(id, new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result, JsonOptions) } }, isError = false });
        }
        catch (Exception ex) when (IsToolFailure(ex))
        {
            // Tool-level failures return isError so agents can recover without breaking the RPC stream.
            return RpcResult(id, new { content = new[] { new { type = "text", text = ex.Message } }, isError = true });
        }
    }

    public async Task<RepositoryStatus> GetStatusAsync(string id, CancellationToken cancellationToken)
    {
        try { return await _manager.Get(id).StatusAsync(cancellationToken); }
        catch (KeyNotFoundException) { return await _store.StatusAsync(id, cancellationToken); }
    }

    public async Task<object> DispatchToolAsync(string tool, JsonElement args, CancellationToken ct)
    {
        string Required(string name) => args.GetProperty(name).GetString() ?? throw new JsonException($"Missing argument: {name}");
        string? Optional(string name) => args.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        int OptionalInt(string name, int value) => args.TryGetProperty(name, out var element) && element.TryGetInt32(out var result) ? result : value;
        bool OptionalBool(string name, bool value) => args.TryGetProperty(name, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False ? element.GetBoolean() : value;
        string[]? StringArray(string name) => args.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Select(x => x.GetString()!).ToArray() : null;
        switch (tool)
        {
            case "open_repository":
                var id = Optional("id");
                var root = Required("rootPath");
                var solution = Optional("solutionPath");
                var modules = StringArray("enabledModules");
                var excludedPaths = StringArray("excludedPaths");
                return await _manager.OpenAsync(new RepositoryDefinition(id ?? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), root, solution, modules, OptionalBool("includeTextualEvidence", false), Optional("tsEngine"), excludedPaths), OptionalBool("reindex", false), ct);
            case "exclude_path":
                var (updatedDefinition, removed) = await _manager.ExcludePathAsync(Required("repositoryId"), Required("path"), ct);
                return new { updatedDefinition.Id, updatedDefinition.ExcludedPaths, symbolsRemoved = removed };
            case "batch":
                if (!args.TryGetProperty("calls", out var callsValue) || callsValue.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("batch requires a calls array");
                var callArray = callsValue.EnumerateArray().Take(10).ToArray();
                var batchResults = new List<object>();
                var usedBytes = 0;
                var stoppedAt = -1;
                for (var i = 0; i < callArray.Length; i++)
                {
                    // Check the budget BEFORE running the call, not after: a call whose own
                    // result blows the budget still gets to run once (progress is never zero),
                    // but we never pay for one we're about to discard.
                    if (usedBytes >= BatchByteBudget) { stoppedAt = i; break; }
                    var call = callArray[i];
                    var name = call.TryGetProperty("tool", out var toolName) ? toolName.GetString() ?? string.Empty : string.Empty;
                    var callArguments = call.TryGetProperty("arguments", out var callArgs) ? callArgs : JsonDocument.Parse("{}").RootElement;
                    object entry;
                    if (name is "batch" or "") entry = new { tool = name, ok = false, error = "invalid tool name" };
                    else
                    {
                        try { entry = new { tool = name, ok = true, result = await DispatchToolAsync(name, callArguments, ct) }; }
                        catch (Exception ex) when (IsToolFailure(ex)) { entry = new { tool = name, ok = false, error = ex.Message }; }
                    }
                    batchResults.Add(entry);
                    usedBytes += JsonSerializer.Serialize(entry, JsonOptions).Length;
                }
                if (stoppedAt < 0) return new { results = batchResults };
                return new
                {
                    results = batchResults,
                    truncated = true,
                    nextIndex = stoppedAt,
                    note = $"Stopped after {batchResults.Count}/{callArray.Length} call(s): response size budget reached. Resend the remaining calls (starting at index {stoppedAt}) in a new batch."
                };
            case "list_repositories":
                return await _manager.ListAsync(ct);
            case "repository_status":
                var statusId = Required("repositoryId");
                if (_manager.Definition(statusId) is null) throw new KeyNotFoundException($"Repository '{statusId}' is not registered");
                return await GetStatusAsync(statusId, ct);
            case "reindex_repository":
                var reindexId = Required("repositoryId");
                var definition = _manager.Definition(reindexId) ?? throw new KeyNotFoundException($"Repository '{reindexId}' is not registered");
                return await _manager.OpenAsync(definition, reindex: true, ct);
            case "close_repository":
                return new { closed = await _manager.CloseAsync(Required("repositoryId")) };
            case "remove_repository":
                return new { removed = await _manager.RemoveAsync(Required("repositoryId"), OptionalBool("deleteData", false), ct) };
            case "repo_overview":
                var overview = await _store.OverviewAsync(Required("repositoryId"), ct);
                return new
                {
                    overview.RepositoryId, overview.Generation, overview.Symbols, overview.Relationships,
                    overview.Kinds, overview.Languages, overview.Projects, overview.EdgeKinds, overview.TopFiles,
                    hubs = overview.Hubs.Select(h => new { symbol = CompactSymbol(h.Symbol), h.Degree }).ToArray()
                };
            case "search_symbols":
                var searchLimit = OptionalInt("limit", 20);
                var searchResults = await _store.SearchAsync(Required("repositoryId"), Required("query"), searchLimit,
                    new SearchFilter(Optional("kind"), Optional("pathContains"), OptionalBool("includeTextual", false)), ct);
                var withRelationships = OptionalBool("includeRelationships", false);
                return new
                {
                    items = searchResults.Select(r => new
                    {
                        symbol = CompactSymbol(r.Symbol),
                        r.Score,
                        relationships = withRelationships ? CompactEdges(r.Relationships) : null
                    }).ToArray(),
                    // SearchAsync clamps to 200 internally; a full result set means more may exist. Raise limit to see them.
                    truncated = searchResults.Count == Math.Clamp(searchLimit, 1, 200)
                };
            case "get_symbol":
                // Hub symbols can have hundreds of edges; default kept modest so one call doesn't
                // pull an unbounded amount of context — raise limit explicitly for popular symbols.
                var symbolLimit = OptionalInt("limit", 40);
                var detail = await _store.SymbolAsync(Required("repositoryId"), Required("symbolId"), symbolLimit, ct)
                    ?? throw new KeyNotFoundException($"Symbol not found: {args.GetProperty("symbolId").GetString()}");
                var symbolEffectiveLimit = Math.Clamp(symbolLimit, 1, 400);
                return new
                {
                    symbol = CompactSymbol(detail.Symbol),
                    outgoing = CompactEdges(detail.Outgoing),
                    outgoingTruncated = detail.Outgoing.Count == symbolEffectiveLimit,
                    incoming = CompactEdges(detail.Incoming),
                    incomingTruncated = detail.Incoming.Count == symbolEffectiveLimit,
                    neighbors = detail.Neighbors.Select(CompactSymbol).ToArray()
                };
            case "file_outline":
                var outline = await _store.OutlineAsync(Required("repositoryId"), Required("filePath"), ct);
                return new { outline.RepositoryId, outline.FilePath, symbols = outline.Symbols.Select(CompactSymbol).ToArray() };
            case "list_files":
                var filesLimit = OptionalInt("limit", 500);
                var files = await _store.FilesAsync(Required("repositoryId"), Optional("contains"), filesLimit, ct);
                return new { items = files, truncated = files.Count == Math.Clamp(filesLimit, 1, 2000) };
            case "get_source":
                return await _manager.SourceAsync(Required("repositoryId"), Required("filePath"), OptionalInt("startLine", 1), OptionalInt("endLine", 0), ct);
            case "find_callers":
            case "find_callees":
            case "find_references":
            case "get_graph":
                // find_* only ever need one edge kind: filter in SQL so the node/edge budget is spent on relevant rows.
                var requestedKinds = args.TryGetProperty("edgeKinds", out var kindsValue) && kindsValue.ValueKind == JsonValueKind.Array
                    ? kindsValue.EnumerateArray().Select(x => x.GetString()!).ToArray() : null;
                string[]? kinds = tool switch
                {
                    "find_callers" or "find_callees" => ["calls"],
                    "find_references" => ["references"],
                    _ => requestedKinds
                };
                var graph = await _store.GraphAsync(Required("repositoryId"), Required("symbolId"), OptionalInt("depth", 2), OptionalInt("limit", 80), kinds, ct);
                if (tool == "get_graph")
                    return new { graph.RepositoryId, graph.Generation, nodes = graph.Nodes.Select(CompactSymbol).ToArray(), edges = CompactEdges(graph.Edges), graph.Truncated };
                var rootId = args.GetProperty("symbolId").GetString()!;
                var edges = tool switch
                {
                    "find_callers" => graph.Edges.Where(e => e.Kind == "calls" && e.TargetId == rootId).ToArray(),
                    "find_callees" => graph.Edges.Where(e => e.Kind == "calls" && e.SourceId == rootId).ToArray(),
                    _ => graph.Edges.Where(e => e.Kind == "references" && (e.SourceId == rootId || e.TargetId == rootId)).ToArray()
                };
                var involved = edges.SelectMany(e => new[] { e.SourceId, e.TargetId }).Append(rootId).ToHashSet(StringComparer.Ordinal);
                return new { graph.RepositoryId, graph.Generation, nodes = graph.Nodes.Where(n => involved.Contains(n.Id)).Select(CompactSymbol).ToArray(), edges = CompactEdges(edges), graph.Truncated };
            default: throw new InvalidOperationException($"Unknown tool: {tool}");
        }
    }

    public static bool IsToolFailure(Exception ex) =>
        ex is KeyNotFoundException or DirectoryNotFoundException or FileNotFoundException or InvalidOperationException or JsonException or SqliteException;

    // Compact wire format for MCP responses: constant/derivable fields (repositoryId, moduleId,
    // language, confidence, edge ids, end columns) are omitted — they were ~45% of the JSON.
    private static object CompactSymbol(SymbolRecord s) => new
    {
        id = s.Id,
        name = s.Name,
        qualifiedName = s.QualifiedName == s.Name ? null : s.QualifiedName,
        kind = s.Kind,
        project = s.Project,
        filePath = s.FilePath,
        startLine = s.StartLine,
        endLine = s.EndLine == s.StartLine ? (int?)null : s.EndLine,
        signature = s.Signature == s.Name ? null : s.Signature
    };

    // One logical edge per (source, target, kind, file); call sites collapse into a bounded lines list.
    private static object[] CompactEdges(IEnumerable<RelationshipRecord> edges) => edges
        .GroupBy(e => (e.SourceId, e.TargetId, e.Kind, e.FilePath))
        .Select(g => new
        {
            sourceId = g.Key.SourceId,
            targetId = g.Key.TargetId,
            kind = g.Key.Kind,
            filePath = g.Key.FilePath,
            lines = g.Select(e => e.Line).Distinct().OrderBy(l => l).Take(8).ToArray(),
            count = g.Count() > 8 ? (int?)g.Count() : null
        })
        .ToArray();

    private static object RpcResult(JsonElement? id, object result) => new { jsonrpc = "2.0", id = id ?? JsonDocument.Parse("null").RootElement, result };
    private static object RpcError(JsonElement? id, int code, string message) => new { jsonrpc = "2.0", id = id ?? JsonDocument.Parse("null").RootElement, error = new { code, message } };
}
