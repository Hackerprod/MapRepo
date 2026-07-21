using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MapRepo.Core;

namespace MapRepo.Modules.TypeScript;

/// <summary>
/// Runs the TypeScript compiler API (embedded ts-semantic.mjs) through a persistent per-repository
/// Node.js daemon for checker-resolved calls, references, inheritance and imports. Returns null when
/// Node or a typescript library cannot be found, so the caller can fall back to syntax analysis.
/// </summary>
public static class TsSemanticEngine
{
    private static readonly Lazy<string?> NodeVersion = new(ProbeNode);
    private static string? _scriptPath;
    private static readonly ConcurrentDictionary<string, TsDaemon> Daemons = new(StringComparer.OrdinalIgnoreCase);

    public static string? NodeVersionString => NodeVersion.Value;

    public static bool IsAvailable(string rootPath) => NodeVersion.Value is not null && FindTypeScriptLib(rootPath) is not null;

    public static string? FindTypeScriptLib(string? rootPath)
    {
        var candidates = new List<string?>
        {
            rootPath is null ? null : Path.Combine(rootPath, "node_modules", "typescript", "lib", "typescript.js"),
            Environment.GetEnvironmentVariable("MAPREPO_TS_LIB"),
            Path.Combine(AppContext.BaseDirectory, "tools", "node_modules", "typescript", "lib", "typescript.js"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "tools", "node_modules", "typescript", "lib", "typescript.js")),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "typescript", "lib", "typescript.js"),
            "/usr/local/lib/node_modules/typescript/lib/typescript.js",
            "/usr/lib/node_modules/typescript/lib/typescript.js"
        };
        return candidates.FirstOrDefault(c => c is not null && File.Exists(c));
    }

    /// <summary>Full snapshot analysis: every symbol/edge in the repository. Starts the repository's
    /// daemon if it isn't already running (first call, or after a previous crash).</summary>
    public static async Task<AnalysisSnapshot?> TryAnalyzeAsync(AnalysisRequest request, List<string> diagnostics)
    {
        var daemon = GetOrStartDaemon(request, diagnostics);
        if (daemon is null) return null;

        var response = await daemon.RequestAsync(new { files = (string[]?)null }, TimeSpan.FromMinutes(5), request.CancellationToken);
        if (response is null)
        {
            diagnostics.Add("ts-semantic: full analysis timed out or the daemon exited; it will restart on the next run");
            Daemons.TryRemove(request.Repository.Id, out _);
            return null;
        }
        if (TryReadError(response, diagnostics)) return null;

        var payload = Deserialize<FullPayload>(response, diagnostics);
        if (payload is null) return null;
        diagnostics.AddRange(payload.Diagnostics.Take(20));
        var symbols = ToSymbols(payload.Symbols, request.Repository.Id);
        var edges = ToEdges(payload.Relationships, request.Repository.Id);
        return new AnalysisSnapshot(request.Repository.Id, Generation(request.Repository.Id), symbols, edges,
            diagnostics.Distinct().Take(100).ToArray(), DateTimeOffset.UtcNow);
    }

    /// <summary>Incremental analysis: only the changed files are re-walked. Returns null (full run
    /// required) when no daemon is running yet for this repository — the caller then invokes
    /// <see cref="TryAnalyzeAsync"/>, which starts one and warms it with a full pass.</summary>
    public static async Task<FileAnalysisDelta?> TryAnalyzeFilesAsync(AnalysisRequest request, List<string> diagnostics)
    {
        if (!Daemons.TryGetValue(request.Repository.Id, out var daemon) || !daemon.IsAlive) return null;

        var absolutePaths = request.ChangedPaths.Select(Path.GetFullPath).ToArray();
        var response = await daemon.RequestAsync(new { files = absolutePaths }, TimeSpan.FromSeconds(90), request.CancellationToken);
        if (response is null)
        {
            diagnostics.Add("ts-semantic: incremental request timed out or the daemon exited; falling back to a full run");
            Daemons.TryRemove(request.Repository.Id, out _);
            return null;
        }
        if (TryReadError(response, diagnostics)) return null;

        var payload = Deserialize<IncrementalPayload>(response, diagnostics);
        if (payload is null) return null;
        diagnostics.AddRange(payload.Diagnostics.Take(20));
        var symbols = ToSymbols(payload.Symbols, request.Repository.Id);
        var edges = ToEdges(payload.Relationships, request.Repository.Id);
        return new FileAnalysisDelta(payload.FilePaths, symbols, edges, diagnostics);
    }

    /// <summary>Kills and forgets the repository's daemon (close_repository/remove_repository).</summary>
    public static void ReleaseRepository(string repositoryId)
    {
        if (Daemons.TryRemove(repositoryId, out var daemon)) _ = daemon.DisposeAsync();
    }

    private static TsDaemon? GetOrStartDaemon(AnalysisRequest request, List<string> diagnostics)
    {
        if (Daemons.TryGetValue(request.Repository.Id, out var existing) && existing.IsAlive) return existing;
        Daemons.TryRemove(request.Repository.Id, out _);

        if (NodeVersion.Value is null) { diagnostics.Add("ts-semantic unavailable: node not found on PATH"); return null; }
        var tsLib = FindTypeScriptLib(request.Repository.RootPath);
        if (tsLib is null) { diagnostics.Add("ts-semantic unavailable: typescript library not found (repo node_modules, MAPREPO_TS_LIB, server tools/, npm -g)"); return null; }

        var script = ExtractScript();
        var daemon = TsDaemon.Start(script, request.Repository.RootPath, tsLib, request.Repository.Id);
        if (daemon is null) { diagnostics.Add("ts-semantic: failed to start node"); return null; }
        Daemons[request.Repository.Id] = daemon;
        return daemon;
    }

    private static bool TryReadError(JsonDocument response, List<string> diagnostics)
    {
        if (!response.RootElement.TryGetProperty("error", out var errorElement)) return false;
        diagnostics.Add($"ts-semantic: {errorElement.GetString()}");
        return true;
    }

    private static T? Deserialize<T>(JsonDocument response, List<string> diagnostics) where T : class
    {
        try { return response.Deserialize<T>(JsonOptions); }
        catch (JsonException ex) { diagnostics.Add($"ts-semantic: invalid response shape: {ex.Message}"); return null; }
    }

    private static SymbolRecord[] ToSymbols(List<SemanticSymbol> symbols, string repositoryId) =>
        symbols.Select(s => new SymbolRecord(s.Id, repositoryId, s.Project, s.FilePath,
            s.Name, s.QualifiedName, s.Kind, s.StartLine, s.StartColumn, s.EndLine, s.EndColumn,
            s.Signature, s.Language, s.ModuleId)).ToArray();

    private static RelationshipRecord[] ToEdges(List<SemanticEdge> edges, string repositoryId) =>
        edges.Select(e => new RelationshipRecord(e.Id, repositoryId, e.SourceId, e.TargetId, e.Kind, e.FilePath,
            e.Line, e.Column, e.Confidence, e.Language, e.ModuleId)).ToArray();

    private static string ExtractScript()
    {
        if (_scriptPath is not null && File.Exists(_scriptPath)) return _scriptPath;
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("MapRepo.Modules.TypeScript.ts-semantic.mjs")
            ?? throw new InvalidOperationException("Embedded ts-semantic.mjs missing");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = reader.ReadToEnd();
        var target = Path.Combine(Path.GetTempPath(),
            $"maprepo-ts-semantic-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..12].ToLowerInvariant()}.mjs");
        if (!File.Exists(target)) File.WriteAllText(target, content, new UTF8Encoding(false));
        _scriptPath = target;
        return target;
    }

    private static string? ProbeNode()
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("node", "--version")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null) return null;
            var version = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return process.ExitCode == 0 && version.StartsWith('v') ? version : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { return null; }
    }

    private static string Generation(string repositoryId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{repositoryId}|{DateTimeOffset.UtcNow:O}"))).ToLowerInvariant()[..16];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record FullPayload(List<SemanticSymbol> Symbols, List<SemanticEdge> Relationships, List<string> Diagnostics);
    private sealed record IncrementalPayload(List<string> FilePaths, List<SemanticSymbol> Symbols, List<SemanticEdge> Relationships, List<string> Diagnostics);
    private sealed record SemanticSymbol(string Id, string? Project, string FilePath, string Name, string QualifiedName,
        string Kind, int StartLine, int StartColumn, int EndLine, int EndColumn, string Signature, string Language, string ModuleId);
    private sealed record SemanticEdge(string Id, string SourceId, string TargetId, string Kind, string FilePath,
        int Line, int Column, string Confidence, string Language, string ModuleId);
}
