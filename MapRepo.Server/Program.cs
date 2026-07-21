using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting.WindowsServices;
using MapRepo.Core;
using MapRepo.Modules.CSharp;
using MapRepo.Modules.TypeScript;
using MapRepo.Server;

// A Windows Service starts with its working directory in System32, not the install folder —
// pin ContentRootPath to the executable's own folder only in that mode, so data-v4/ and wwwroot/
// resolve correctly. `dotnet run` and the Scheduled Task launch (which sets "Start in") keep
// relying on the current directory, unchanged from before.
var runningAsService = OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = runningAsService ? AppContext.BaseDirectory : null
});
builder.Host.UseWindowsService(options => options.ServiceName = "MapRepoServer");
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
    var registry = new ModuleRegistry(sp.GetRequiredService<ILogger<ModuleRegistry>>());
    registry.Register(new CSharpRoslynModule());
    registry.Register(new TypeScriptModule());
    registry.Discover(Path.Combine(AppContext.BaseDirectory, "modules"));
    return registry;
});
builder.Services.AddSingleton<RepositorySessionManager>();
builder.Services.AddSingleton<McpDispatcher>();

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

app.MapGet("/api/repos/{id}/status", async (string id, RepositorySessionManager manager, McpDispatcher dispatcher, CancellationToken ct) =>
{
    try
    {
        if (manager.Definition(id) is null) throw new KeyNotFoundException($"Repository '{id}' is not registered");
        return Results.Ok(await dispatcher.GetStatusAsync(id, ct));
    }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (SqliteException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
});

app.MapGet("/api/repos/{id}/overview", async (string id, bool? includeGenerated, IRepositoryStore store, CancellationToken ct) =>
    Results.Ok(await store.OverviewAsync(id, includeGenerated ?? false, ct)));

app.MapGet("/api/repos/{id}/files", async (string id, string? contains, int? limit, IRepositoryStore store, CancellationToken ct) =>
    Results.Ok(await store.FilesAsync(id, contains, limit ?? 500, ct)));

app.MapGet("/api/repos/{id}/outline", async (string id, string path, IRepositoryStore store, CancellationToken ct) =>
    Results.Ok(await store.OutlineAsync(id, path, cancellationToken: ct)));

app.MapGet("/api/repos/{id}/source", async (string id, string path, int? start, int? end, RepositorySessionManager manager, CancellationToken ct) =>
{
    // The UI browses source by clicking around a rendered graph, not composing an exact range up
    // front, so it opts into the lenient auto-corrected behavior rather than erroring on a stray click.
    try { return Results.Ok(await manager.SourceAsync(id, path, start ?? 1, end ?? 0, clamp: true, ct)); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/search/{id}", async (string id, string q, int? limit, string? kind, string? path, bool? textual, IRepositoryStore store, CancellationToken ct) =>
    // The UI expects a bare array (see app.js's results.map) — unwrap SearchOutcome rather than
    // changing the response shape for a viewer that has no use for the truncated flag today.
    Results.Ok((await store.SearchAsync(id, q, limit ?? 20, new SearchFilter(kind, path, textual ?? false), ct)).Items));

app.MapGet("/api/graph/{id}/{symbolId}", async (string id, string symbolId, int? depth, int? limit, string? kinds, IRepositoryStore store, CancellationToken ct) =>
    Results.Ok(await store.GraphAsync(id, symbolId, depth ?? 2, limit ?? 80,
        string.IsNullOrWhiteSpace(kinds) ? null : kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), ct)));

app.MapGet("/api/symbol/{id}/{symbolId}", async (string id, string symbolId, int? limit, IRepositoryStore store, CancellationToken ct) =>
{
    var detail = await store.SymbolAsync(id, symbolId, limit ?? 100, ct);
    return detail is null ? Results.NotFound(new { error = $"Symbol '{symbolId}' not found" }) : Results.Ok(detail);
});

app.MapPost("/mcp", async (HttpContext context, McpDispatcher dispatcher, CancellationToken ct) =>
{
    try
    {
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
        var method = document.RootElement.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
        if (method is "notifications/initialized" or "initialized")
        {
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return Results.Empty;
        }

        var response = await dispatcher.HandleRequestAsync(document.RootElement, ct);
        var payload = JsonSerializer.Serialize(response, dispatcher.JsonOptions);
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
    catch (JsonException ex) { return Results.Json(new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = ex.Message } }, statusCode: 200); }
    catch (Exception ex) when (ex is KeyNotFoundException or DirectoryNotFoundException or InvalidOperationException or SqliteException)
    { return Results.Json(new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32000, message = ex.Message } }, statusCode: 200); }
});

// Streamable HTTP and legacy SSE transports are both exposed for MCP clients that probe GET first.
app.MapGet("/mcp", async (HttpContext context, McpDispatcher dispatcher, CancellationToken ct) =>
{
    var sessionId = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault() ?? Guid.NewGuid().ToString("N");
    var channel = mcpSessions.GetOrAdd(sessionId, _ => Channel.CreateUnbounded<string>());
    context.Response.Headers["Mcp-Session-Id"] = sessionId;
    context.Response.ContentType = "text/event-stream; charset=utf-8";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    await context.Response.WriteAsync($"event: endpoint\ndata: {JsonSerializer.Serialize(new { type = "connection", message = "MCP stream ready", sessionId }, dispatcher.JsonOptions)}\n\n", ct);
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

app.MapGet("/sse", async (HttpContext context, McpDispatcher dispatcher, CancellationToken ct) =>
{
    var sessionId = Guid.NewGuid().ToString("N");
    var channel = Channel.CreateUnbounded<string>();
    mcpSessions[sessionId] = channel;
    context.Response.ContentType = "text/event-stream; charset=utf-8";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    await context.Response.WriteAsync($"event: endpoint\ndata: {JsonSerializer.Serialize(new { type = "endpoint", uri = $"/message?sessionId={sessionId}" }, dispatcher.JsonOptions)}\n\n", ct);
    await context.Response.Body.FlushAsync(ct);
    try { while (await channel.Reader.WaitToReadAsync(ct)) while (channel.Reader.TryRead(out var message)) { await context.Response.WriteAsync($"event: message\ndata: {message}\n\n", ct); await context.Response.Body.FlushAsync(ct); } }
    catch (OperationCanceledException) { }
    finally { mcpSessions.TryRemove(sessionId, out _); }
});

app.MapPost("/message", async (HttpContext context, McpDispatcher dispatcher, CancellationToken ct) =>
{
    var sessionId = context.Request.Query["sessionId"].FirstOrDefault();
    if (sessionId is null || !mcpSessions.TryGetValue(sessionId, out var channel)) return Results.BadRequest(new { error = "Invalid or missing sessionId" });
    try
    {
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
        var response = await dispatcher.HandleRequestAsync(document.RootElement, ct);
        await channel.Writer.WriteAsync(JsonSerializer.Serialize(response, dispatcher.JsonOptions), ct);
        return Results.StatusCode(StatusCodes.Status202Accepted);
    }
    catch (JsonException) { return Results.BadRequest(new { error = "Parse error" }); }
});

app.MapGet("/info", () => Results.Ok(new
{
    service = "map-repo-server",
    protocol = "JSON-RPC 2.0",
    endpoints = new { mcp = "/mcp", sse = "/sse", message = "/message" },
    methods = new[] { "initialize", "ping", "tools/list", "tools/call" },
    tools = McpToolCatalog.All.Select(t => t.Name)
}));

app.Run();
