using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MapRepo.Core;

namespace MapRepo.Modules.TypeScript;

/// <summary>
/// Runs the TypeScript compiler API (embedded ts-semantic.mjs) through Node.js for
/// checker-resolved calls, references, inheritance and imports. Returns null when
/// Node or a typescript library cannot be found, so the caller can fall back to syntax analysis.
/// </summary>
public static class TsSemanticEngine
{
    private static readonly Lazy<string?> NodeVersion = new(ProbeNode);
    private static string? _scriptPath;

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

    public static async Task<AnalysisSnapshot?> TryAnalyzeAsync(AnalysisRequest request, List<string> diagnostics)
    {
        if (NodeVersion.Value is null) { diagnostics.Add("ts-semantic unavailable: node not found on PATH"); return null; }
        var tsLib = FindTypeScriptLib(request.Repository.RootPath);
        if (tsLib is null) { diagnostics.Add("ts-semantic unavailable: typescript library not found (repo node_modules, MAPREPO_TS_LIB, server tools/, npm -g)"); return null; }

        var script = ExtractScript();
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = request.Repository.RootPath
        };
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("--root"); startInfo.ArgumentList.Add(request.Repository.RootPath);
        startInfo.ArgumentList.Add("--ts"); startInfo.ArgumentList.Add(tsLib);
        startInfo.ArgumentList.Add("--repo"); startInfo.ArgumentList.Add(request.Repository.Id);

        using var process = Process.Start(startInfo);
        if (process is null) { diagnostics.Add("ts-semantic: failed to start node"); return null; }
        var stdoutTask = process.StandardOutput.ReadToEndAsync(request.CancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(request.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            diagnostics.Add("ts-semantic: analysis timed out after 5 minutes");
            return null;
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            diagnostics.Add($"ts-semantic failed (exit {process.ExitCode}): {Truncate(stderr, 300)}");
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<SemanticPayload>(stdout, JsonOptions);
            if (payload is null) { diagnostics.Add("ts-semantic: empty payload"); return null; }
            diagnostics.AddRange(payload.Diagnostics.Take(20));
            var symbols = payload.Symbols.Select(s => new SymbolRecord(s.Id, request.Repository.Id, s.Project, s.FilePath,
                s.Name, s.QualifiedName, s.Kind, s.StartLine, s.StartColumn, s.EndLine, s.EndColumn,
                s.Signature, s.Language, s.ModuleId)).ToArray();
            var edges = payload.Relationships.Select(e => new RelationshipRecord(e.Id, request.Repository.Id, e.SourceId,
                e.TargetId, e.Kind, e.FilePath, e.Line, e.Column, e.Confidence, e.Language, e.ModuleId)).ToArray();
            return new AnalysisSnapshot(request.Repository.Id, Generation(request.Repository.Id), symbols, edges,
                diagnostics.Distinct().Take(100).ToArray(), DateTimeOffset.UtcNow);
        }
        catch (JsonException ex)
        {
            diagnostics.Add($"ts-semantic: invalid JSON output: {ex.Message}");
            return null;
        }
    }

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
            var startInfo = new ProcessStartInfo("node", "--version")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(startInfo);
            if (process is null) return null;
            var version = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return process.ExitCode == 0 && version.StartsWith('v') ? version : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { return null; }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
    private static string Generation(string repositoryId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{repositoryId}|{DateTimeOffset.UtcNow:O}"))).ToLowerInvariant()[..16];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record SemanticPayload(List<SemanticSymbol> Symbols, List<SemanticEdge> Relationships, List<string> Diagnostics);
    private sealed record SemanticSymbol(string Id, string? Project, string FilePath, string Name, string QualifiedName,
        string Kind, int StartLine, int StartColumn, int EndLine, int EndColumn, string Signature, string Language, string ModuleId);
    private sealed record SemanticEdge(string Id, string SourceId, string TargetId, string Kind, string FilePath,
        int Line, int Column, string Confidence, string Language, string ModuleId);
}
