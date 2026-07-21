using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using MapRepo.Core;
using MapRepo.Modules.CSharp;
using MapRepo.Modules.TypeScript;
using MapRepo.Server;

var JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};
var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddSingleton<SqliteRepositoryStore>();
builder.Services.AddSingleton<IRepositoryStore>(sp => sp.GetRequiredService<SqliteRepositoryStore>());
builder.Services.AddSingleton<RepositoryCatalog>();
builder.Services.AddSingleton(sp =>
{
    var registry = new ModuleRegistry();
    registry.Register(new CSharpRoslynModule());
    registry.Register(new TypeScriptModule());
    registry.Discover(Path.Combine(AppContext.BaseDirectory, "modules"));
    return registry;
});
builder.Services.AddSingleton<RepositorySessionManager>();

var app = builder.Build();
await app.Services.GetRequiredService<IRepositoryStore>().InitializeAsync();
// Restore cataloged repositories in the background so startup stays instant.
_ = Task.Run(() => app.Services.GetRequiredService<RepositorySessionManager>().RestoreAsync());
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // The Atlas UI iterates often; force revalidation so browsers never serve a stale app.js.
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache, must-revalidate"
});
var mcpSessions = new ConcurrentDictionary<string, Channel<string>>(StringComparer.Ordinal);

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "map-repo-server" }));
app.MapGet("/api/modules", (RepositorySessionManager manager) => Results.Ok(manager.Modules.Select(m => m.Descriptor)));

app.MapGet("/api/engines", () => Results.Ok(new
{
    roslyn = new { available = true, engine = "MSBuildWorkspace" },
    typescript = new
    {
        node = TsSemanticEngine.NodeVersionString,
        typescriptLib = TsSemanticEngine.FindTypeScriptLib(null),
        semanticAvailable = TsSemanticEngine.NodeVersionString is not null && TsSemanticEngine.FindTypeScriptLib(null) is not null,
        note = "Per-repo node_modules/typescript is also probed at analysis time; tsEngine accepts auto | semantic | syntax."
    }
}));

app.MapGet("/api/repos", async (RepositorySessionManager manager, CancellationToken ct) =>
    Results.Ok(await manager.ListAsync(ct)));

app.MapPost("/api/repos/open", async (RepositoryDefinition definition, bool? reindex, RepositorySessionManager manager, CancellationToken ct) =>
{
    try { return Results.Ok(await manager.OpenAsync(definition, reindex ?? false, ct)); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or InvalidOperationException or SqliteException)
    { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/repos/{id}/reindex", async (string id, RepositorySessionManager manager, CancellationToken ct) =>
{
    try
    {
        var definition = manager.Definition(id) ?? throw new KeyNotFoundException($"Repository '{id}' is not registered");
        return Results.Ok(await manager.OpenAsync(definition, reindex: true, ct));
    }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (DirectoryNotFoundException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapDelete("/api/repos/{id}", async (string id, bool? deleteData, RepositorySessionManager manager, CancellationToken ct) =>
{
    var removed = await manager.RemoveAsync(id, deleteData ?? false, ct);
    return removed ? Results.Ok(new { removed = true, id }) : Results.NotFound(new { error = $"Repository '{id}' is not registered" });
});

app.MapGet("/api/repos/{id}/status", async (string id, RepositorySessionManager manager, IRepositoryStore store, CancellationToken ct) =>
{
    try
    {
        var status = manager.Definition(id) is null
            ? throw new KeyNotFoundException($"Repository '{id}' is not registered")
            : await StatusFor(id, manager, store, ct);
        return Results.Ok(status);
    }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (SqliteException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
});

app.MapGet("/api/repos/{id}/overview", async (string id, IRepositoryStore store, CancellationToken ct) =>
    Results.Ok(await store.OverviewAsync(id, ct)));

app.MapGet("/api/repos/{id}/files", async (string id, string? contains, int? limit, IRepositoryStore store, CancellationToken ct) =>
    Results.Ok(await store.FilesAsync(id, contains, limit ?? 500, ct)));

app.MapGet("/api/repos/{id}/outline", async (string id, string path, IRepositoryStore store, CancellationToken ct) =>
    Results.Ok(await store.OutlineAsync(id, path, ct)));

app.MapGet("/api/repos/{id}/source", async (string id, string path, int? start, int? end, RepositorySessionManager manager, CancellationToken ct) =>
{
    try { return Results.Ok(await manager.SourceAsync(id, path, start ?? 1, end ?? 0, ct)); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/search/{id}", async (string id, string q, int? limit, string? kind, string? path, bool? textual, IRepositoryStore store, CancellationToken ct) =>
    Results.Ok(await store.SearchAsync(id, q, limit ?? 20, new SearchFilter(kind, path, textual ?? false), ct)));

app.MapGet("/api/graph/{id}/{symbolId}", async (string id, string symbolId, int? depth, int? limit, string? kinds, IRepositoryStore store, CancellationToken ct) =>
    Results.Ok(await store.GraphAsync(id, symbolId, depth ?? 2, limit ?? 80,
        string.IsNullOrWhiteSpace(kinds) ? null : kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), ct)));

app.MapGet("/api/symbol/{id}/{symbolId}", async (string id, string symbolId, int? limit, IRepositoryStore store, CancellationToken ct) =>
{
    var detail = await store.SymbolAsync(id, symbolId, limit ?? 100, ct);
    return detail is null ? Results.NotFound(new { error = $"Symbol '{symbolId}' not found" }) : Results.Ok(detail);
});

app.MapPost("/mcp", async (HttpContext context, RepositorySessionManager manager, IRepositoryStore store, CancellationToken ct) =>
{
    try
    {
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
        var request = document.RootElement.Clone();
        var id = request.TryGetProperty("id", out var requestId) ? requestId : (JsonElement?)null;
        var method = request.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(method)) return Results.Json(RpcError(id, -32600, "Invalid Request"), statusCode: 200);
        if (method is "notifications/initialized" or "initialized")
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return Results.Empty;
        }
        object response;
        if (method == "initialize")
            response = RpcResult(id, new { protocolVersion = "2024-11-05", serverInfo = new { name = "map-repo-server", version = "2.0.0" }, capabilities = new { tools = new { listChanged = false } } });
        else if (method == "ping") response = RpcResult(id, new { });
        else if (method == "tools/list") response = RpcResult(id, new { tools = ToolDefinitions() });
        else if (method == "tools/call")
        {
            var parameters = request.TryGetProperty("params", out var p) ? p : throw new JsonException("Missing params");
            var tool = parameters.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : throw new JsonException("Missing tool name");
            var arguments = parameters.TryGetProperty("arguments", out var args) ? args : JsonDocument.Parse("{}").RootElement;
            try
            {
                var result = Envelope(await CallToolAsync(tool, arguments, manager, store, ct));
                response = RpcResult(id, new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result, JsonOptions) } }, structuredContent = result, isError = false });
            }
            catch (Exception ex) when (ex is KeyNotFoundException or DirectoryNotFoundException or FileNotFoundException or InvalidOperationException or SqliteException)
            {
                // Tool-level failures return isError so agents can recover without breaking the RPC stream.
                response = RpcResult(id, new { content = new[] { new { type = "text", text = ex.Message } }, isError = true });
            }
        }
        else response = RpcError(id, -32601, $"Method not found: {method}");

        var payload = JsonSerializer.Serialize(response, JsonOptions);
        var acceptsSse = context.Request.Headers.Accept.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
        if (acceptsSse)
        {
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            await context.Response.WriteAsync($"data: {payload}\n\n", ct);
            await context.Response.Body.FlushAsync(ct);
            return Results.Empty;
        }
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(payload, ct);
        return Results.Empty;
    }
    catch (JsonException ex) { return Results.Json(RpcError(null, -32700, ex.Message), statusCode: 200); }
    catch (Exception ex) when (ex is KeyNotFoundException or DirectoryNotFoundException or InvalidOperationException or SqliteException)
    { return Results.Json(RpcError(null, -32000, ex.Message), statusCode: 200); }
});

// Streamable HTTP and legacy SSE transports are both exposed for MCP clients that probe GET first.
app.MapGet("/mcp", async (HttpContext context, CancellationToken ct) =>
{
    var sessionId = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString("N");
    var channel = mcpSessions.GetOrAdd(sessionId, _ => Channel.CreateUnbounded<string>());
    context.Response.Headers["Mcp-Session-Id"] = sessionId;
    context.Response.ContentType = "text/event-stream; charset=utf-8";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    await context.Response.WriteAsync($"event: endpoint\ndata: {JsonSerializer.Serialize(new { type = "connection", message = "MCP stream ready", sessionId }, JsonOptions)}\n\n", ct);
    await context.Response.Body.FlushAsync(ct);
    try
    {
        while (!ct.IsCancellationRequested)
        {
            var read = await channel.Reader.WaitToReadAsync(ct);
            if (!read) break;
            while (channel.Reader.TryRead(out var message))
            {
                await context.Response.WriteAsync($"event: message\ndata: {message}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
            }
        }
    }
    catch (OperationCanceledException) { }
    finally { mcpSessions.TryRemove(sessionId, out _); }
});

app.MapGet("/sse", async (HttpContext context, CancellationToken ct) =>
{
    var sessionId = Guid.NewGuid().ToString("N");
    var channel = Channel.CreateUnbounded<string>();
    mcpSessions[sessionId] = channel;
    context.Response.ContentType = "text/event-stream; charset=utf-8";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    await context.Response.WriteAsync($"event: endpoint\ndata: {JsonSerializer.Serialize(new { type = "endpoint", uri = $"/message?sessionId={sessionId}" }, JsonOptions)}\n\n", ct);
    await context.Response.Body.FlushAsync(ct);
    try { while (await channel.Reader.WaitToReadAsync(ct)) while (channel.Reader.TryRead(out var message)) { await context.Response.WriteAsync($"event: message\ndata: {message}\n\n", ct); await context.Response.Body.FlushAsync(ct); } }
    catch (OperationCanceledException) { }
    finally { mcpSessions.TryRemove(sessionId, out _); }
});

app.MapPost("/message", async (HttpContext context, RepositorySessionManager manager, IRepositoryStore store, CancellationToken ct) =>
{
    var sessionId = context.Request.Query["sessionId"].FirstOrDefault();
    if (sessionId is null || !mcpSessions.TryGetValue(sessionId, out var channel)) return Results.BadRequest(new { error = "Invalid or missing sessionId" });
    try
    {
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
        var request = document.RootElement;
        var id = request.TryGetProperty("id", out var requestId) ? requestId : (JsonElement?)null;
        var method = request.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
        object response = method switch
        {
            "initialize" => RpcResult(id, new { protocolVersion = "2024-11-05", serverInfo = new { name = "map-repo-server", version = "2.0.0" }, capabilities = new { tools = new { } } }),
            "ping" => RpcResult(id, new { }),
            "tools/list" => RpcResult(id, new { tools = ToolDefinitions() }),
            "tools/call" => await CallLegacyToolAsync(request, manager, store, ct),
            _ => RpcError(id, -32601, $"Method not found: {method}")
        };
        await channel.Writer.WriteAsync(JsonSerializer.Serialize(response, JsonOptions), ct);
        return Results.StatusCode(StatusCodes.Status202Accepted);
    }
    catch (JsonException) { return Results.BadRequest(new { error = "Parse error" }); }
});

app.MapGet("/info", () => Results.Ok(new { service = "map-repo-server", protocol = "JSON-RPC 2.0", endpoints = new { mcp = "/mcp", sse = "/sse", message = "/message" }, methods = new[] { "initialize", "ping", "tools/list", "tools/call" }, tools = ToolDefinitions().Select(t => (string)t.GetType().GetProperty("name")!.GetValue(t)!) }));

app.Run();

static async Task<RepositoryStatus> StatusFor(string id, RepositorySessionManager manager, IRepositoryStore store, CancellationToken ct)
{
    try { return await manager.Get(id).StatusAsync(ct); }
    catch (KeyNotFoundException) { return await store.StatusAsync(id, ct); }
}

static object[] ToolDefinitions() =>
[
    new { name = "open_repository", description = "Register/open a repository, index it and start its live file watcher. Reuses the existing index unless reindex=true.", inputSchema = Schema([
        StringProperty("rootPath", "Absolute repository root path."),
        StringProperty("id", "Optional stable repository id (defaults to the folder name)."),
        StringProperty("solutionPath", "Optional .sln/.slnx/.csproj path used by the C# module."),
        ArrayProperty("enabledModules", "Optional module filter such as csharp-roslyn or typescript-syntax.", "string"),
        BooleanProperty("reindex", "Force a full reindex even when the stored index is populated."),
        BooleanProperty("includeTextualEvidence", "Also index identifier-like words found in string literals (default false; adds noise, ~30% more rows)."),
        StringProperty("tsEngine", "TypeScript analysis engine: auto (default; semantic when Node+typescript are available), semantic, or syntax.")
    ], ["rootPath"]) },
    new { name = "list_repositories", description = "List every registered repository with its index status. Call this first to discover repositoryId values.", inputSchema = Schema([], []) },
    new { name = "repository_status", description = "Return index generation, counts, diagnostics and watcher state", inputSchema = RepoSchema() },
    new { name = "reindex_repository", description = "Force a full reindex of a registered repository", inputSchema = RepoSchema() },
    new { name = "close_repository", description = "Stop the watcher and release the in-memory session. Data and registration are kept.", inputSchema = RepoSchema() },
    new { name = "remove_repository", description = "Unregister a repository; optionally delete its database", inputSchema = Schema([
        StringProperty("repositoryId", "Repository id."),
        BooleanProperty("deleteData", "Also delete the per-repository database directory.")
    ], ["repositoryId"]) },
    new { name = "repo_overview", description = "Token-cheap orientation map: symbol/edge counts by kind, language and project, top files and the most connected hub symbols. Ideal first call on an unknown repository.", inputSchema = RepoSchema() },
    new { name = "search_symbols", description = "Find symbols with exact source file and line evidence. Supports kind and path filters.", inputSchema = Schema([
        StringProperty("repositoryId", "Repository id."),
        StringProperty("query", "Symbol name or source text to find."),
        IntegerProperty("limit", "Maximum result count."),
        StringProperty("kind", "Optional symbol kind filter, e.g. Method, NamedType, class, property."),
        StringProperty("pathContains", "Optional substring the file path must contain."),
        BooleanProperty("includeTextual", "Include textual-evidence matches from string literals (default false)."),
        BooleanProperty("includeRelationships", "Attach up to 24 edges per result (default false; use get_symbol/find_* instead).")
    ], ["repositoryId", "query"]) },
    new { name = "get_symbol", description = "Full detail for one symbol: record, incoming and outgoing edges, and neighbor symbols", inputSchema = Schema([
        StringProperty("repositoryId", "Repository id."),
        StringProperty("symbolId", "Symbol id from search_symbols or graph results."),
        IntegerProperty("limit", "Maximum edges per direction.")
    ], ["repositoryId", "symbolId"]) },
    new { name = "file_outline", description = "All declarations in one file ordered by line — read this instead of the file to save tokens", inputSchema = Schema([
        StringProperty("repositoryId", "Repository id."),
        StringProperty("filePath", "Repository-relative file path with forward slashes.")
    ], ["repositoryId", "filePath"]) },
    new { name = "list_files", description = "List indexed files with their declaration counts; optional substring filter", inputSchema = Schema([
        StringProperty("repositoryId", "Repository id."),
        StringProperty("contains", "Optional substring the file path must contain."),
        IntegerProperty("limit", "Maximum file count.")
    ], ["repositoryId"]) },
    new { name = "get_source", description = "Read an exact line range (max 400 lines) from a repository file. Use after search_symbols/file_outline so only relevant lines are fetched.", inputSchema = Schema([
        StringProperty("repositoryId", "Repository id."),
        StringProperty("filePath", "Repository-relative file path."),
        IntegerProperty("startLine", "1-based first line (default 1)."),
        IntegerProperty("endLine", "1-based last line (default startLine+60).")
    ], ["repositoryId", "filePath"]) },
    new { name = "batch", description = "Execute up to 10 tool calls in one request (e.g. search_symbols then get_source). Results return in order; a failing call does not abort the rest.", inputSchema = new
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
    } },
    new { name = "find_callers", description = "Find methods that call a symbol", inputSchema = GraphLookupSchema() },
    new { name = "find_callees", description = "Find symbols called by a method", inputSchema = GraphLookupSchema() },
    new { name = "find_references", description = "Find reference edges around a symbol", inputSchema = GraphLookupSchema() },
    new { name = "get_graph", description = "Return a bounded symbol graph for Canvas or agent reasoning", inputSchema = GraphLookupSchema() }
];

static object RepoSchema() => Schema([StringProperty("repositoryId", "Repository id.")], ["repositoryId"]);

static object GraphLookupSchema() => Schema([
    StringProperty("repositoryId", "Repository id."),
    StringProperty("symbolId", "Symbol id from search_symbols or graph results."),
    IntegerProperty("depth", "Graph traversal depth."),
    IntegerProperty("limit", "Maximum node/edge count."),
    ArrayProperty("edgeKinds", "Only traverse these edge kinds (calls, references, contains, constructs, inherits, implements, imports). Unset = all.", "string")
], ["repositoryId", "symbolId"]);

static object Schema(IEnumerable<KeyValuePair<string, object>> properties, string[] required) => new
{
    type = "object",
    properties = properties.ToDictionary(x => x.Key, x => x.Value),
    required,
    additionalProperties = false
};

static KeyValuePair<string, object> StringProperty(string name, string description) =>
    new(name, new { type = "string", description });

static KeyValuePair<string, object> IntegerProperty(string name, string description) =>
    new(name, new { type = "integer", description, minimum = 1 });

static KeyValuePair<string, object> BooleanProperty(string name, string description) =>
    new(name, new { type = "boolean", description });

static KeyValuePair<string, object> ArrayProperty(string name, string description, string itemType) =>
    new(name, new { type = "array", description, items = new { type = itemType } });

async Task<object> CallLegacyToolAsync(JsonElement request, RepositorySessionManager manager, IRepositoryStore store, CancellationToken ct)
{
    var parameters = request.TryGetProperty("params", out var p) ? p : throw new JsonException("Missing params");
    var tool = parameters.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : throw new JsonException("Missing tool name");
    var arguments = parameters.TryGetProperty("arguments", out var args) ? args : JsonDocument.Parse("{}").RootElement;
    var id = request.TryGetProperty("id", out var requestId) ? requestId : (JsonElement?)null;
    try
    {
        var result = Envelope(await CallToolAsync(tool, arguments, manager, store, ct));
        return RpcResult(id, new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result, JsonOptions) } }, structuredContent = result, isError = false });
    }
    catch (Exception ex) when (ex is KeyNotFoundException or DirectoryNotFoundException or FileNotFoundException or InvalidOperationException or SqliteException)
    {
        return RpcResult(id, new { content = new[] { new { type = "text", text = ex.Message } }, isError = true });
    }
}

static async Task<object> CallToolAsync(string tool, JsonElement args, RepositorySessionManager manager, IRepositoryStore store, CancellationToken ct)
{
    string Required(string name) => args.GetProperty(name).GetString() ?? throw new JsonException($"Missing argument: {name}");
    string? Optional(string name) => args.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    int OptionalInt(string name, int value) => args.TryGetProperty(name, out var element) && element.TryGetInt32(out var result) ? result : value;
    bool OptionalBool(string name, bool value) => args.TryGetProperty(name, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False ? element.GetBoolean() : value;
    switch (tool)
    {
        case "open_repository":
            var id = Optional("id");
            var root = Required("rootPath");
            var solution = Optional("solutionPath");
            var modules = args.TryGetProperty("enabledModules", out var modulesValue) && modulesValue.ValueKind == JsonValueKind.Array
                ? modulesValue.EnumerateArray().Select(x => x.GetString()!).ToArray() : null;
            return await manager.OpenAsync(new RepositoryDefinition(id ?? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), root, solution, modules, OptionalBool("includeTextualEvidence", false), Optional("tsEngine")), OptionalBool("reindex", false), ct);
        case "batch":
            if (!args.TryGetProperty("calls", out var callsValue) || callsValue.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("batch requires a calls array");
            var batchResults = new List<object>();
            foreach (var call in callsValue.EnumerateArray().Take(10))
            {
                var name = call.TryGetProperty("tool", out var toolName) ? toolName.GetString() ?? string.Empty : string.Empty;
                var callArguments = call.TryGetProperty("arguments", out var callArgs) ? callArgs : JsonDocument.Parse("{}").RootElement;
                if (name is "batch" or "") { batchResults.Add(new { tool = name, ok = false, error = "invalid tool name" }); continue; }
                try { batchResults.Add(new { tool = name, ok = true, result = Envelope(await CallToolAsync(name, callArguments, manager, store, ct)) }); }
                catch (Exception ex) when (ex is KeyNotFoundException or DirectoryNotFoundException or FileNotFoundException or InvalidOperationException or JsonException or SqliteException)
                { batchResults.Add(new { tool = name, ok = false, error = ex.Message }); }
            }
            return new { results = batchResults };
        case "list_repositories":
            return await manager.ListAsync(ct);
        case "repository_status":
            var statusId = Required("repositoryId");
            if (manager.Definition(statusId) is null) throw new KeyNotFoundException($"Repository '{statusId}' is not registered");
            return await StatusFor(statusId, manager, store, ct);
        case "reindex_repository":
            var reindexId = Required("repositoryId");
            var definition = manager.Definition(reindexId) ?? throw new KeyNotFoundException($"Repository '{reindexId}' is not registered");
            return await manager.OpenAsync(definition, reindex: true, ct);
        case "close_repository":
            return new { closed = await manager.CloseAsync(Required("repositoryId")) };
        case "remove_repository":
            return new { removed = await manager.RemoveAsync(Required("repositoryId"), OptionalBool("deleteData", false), ct) };
        case "repo_overview":
            var overview = await store.OverviewAsync(Required("repositoryId"), ct);
            return new
            {
                overview.RepositoryId, overview.Generation, overview.Symbols, overview.Relationships,
                overview.Kinds, overview.Languages, overview.Projects, overview.EdgeKinds, overview.TopFiles,
                hubs = overview.Hubs.Select(h => new { symbol = CompactSymbol(h.Symbol), h.Degree }).ToArray()
            };
        case "search_symbols":
            var searchResults = await store.SearchAsync(Required("repositoryId"), Required("query"), OptionalInt("limit", 20),
                new SearchFilter(Optional("kind"), Optional("pathContains"), OptionalBool("includeTextual", false)), ct);
            var withRelationships = OptionalBool("includeRelationships", false);
            return searchResults.Select(r => new
            {
                symbol = CompactSymbol(r.Symbol),
                r.Score,
                relationships = withRelationships ? CompactEdges(r.Relationships) : null
            }).ToArray();
        case "get_symbol":
            var detail = await store.SymbolAsync(Required("repositoryId"), Required("symbolId"), OptionalInt("limit", 100), ct)
                ?? throw new KeyNotFoundException($"Symbol not found: {args.GetProperty("symbolId").GetString()}");
            return new
            {
                symbol = CompactSymbol(detail.Symbol),
                outgoing = CompactEdges(detail.Outgoing),
                incoming = CompactEdges(detail.Incoming),
                neighbors = detail.Neighbors.Select(CompactSymbol).ToArray()
            };
        case "file_outline":
            var outline = await store.OutlineAsync(Required("repositoryId"), Required("filePath"), ct);
            return new { outline.RepositoryId, outline.FilePath, symbols = outline.Symbols.Select(CompactSymbol).ToArray() };
        case "list_files":
            return await store.FilesAsync(Required("repositoryId"), Optional("contains"), OptionalInt("limit", 500), ct);
        case "get_source":
            return await manager.SourceAsync(Required("repositoryId"), Required("filePath"), OptionalInt("startLine", 1), OptionalInt("endLine", 0), ct);
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
            var graph = await store.GraphAsync(Required("repositoryId"), Required("symbolId"), OptionalInt("depth", 2), OptionalInt("limit", 80), kinds, ct);
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

// Compact wire format for MCP responses: constant/derivable fields (repositoryId, moduleId,
// language, confidence, edge ids, end columns) are omitted — they were ~45% of the JSON.
static object CompactSymbol(SymbolRecord s) => new
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
static object[] CompactEdges(IEnumerable<RelationshipRecord> edges) => edges
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

// The MCP spec requires structuredContent to be a JSON object; array results get wrapped.
static object Envelope(object result) => result is System.Collections.IEnumerable and not string ? new { items = result } : result;

static object RpcResult(JsonElement? id, object result) => new { jsonrpc = "2.0", id = id ?? JsonDocument.Parse("null").RootElement, result };
static object RpcError(JsonElement? id, int code, string message) => new { jsonrpc = "2.0", id = id ?? JsonDocument.Parse("null").RootElement, error = new { code, message } };
