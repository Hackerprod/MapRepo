using System.Diagnostics;
using System.Text.Json;

namespace MapRepo.Modules.TypeScript;

/// <summary>
/// One persistent `node ts-semantic.mjs` child process per repository, kept alive across analyses.
/// Protocol: one JSON request per line on stdin, one JSON response per line on stdout. Building the
/// TypeScript Program is the expensive part of semantic analysis; keeping the process (and its
/// Program/checker) alive lets subsequent requests reuse unchanged files' parsed/bound state
/// instead of re-parsing the whole repository on every save.
/// </summary>
internal sealed class TsDaemon : IAsyncDisposable
{
    private readonly Process _process;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _alive = true;

    private TsDaemon(Process process) => _process = process;

    public bool IsAlive => _alive && !_process.HasExited;

    public static TsDaemon? Start(string scriptPath, string root, string tsLib, string repositoryId)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = root,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardInputEncoding = System.Text.Encoding.UTF8
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--root"); startInfo.ArgumentList.Add(root);
        startInfo.ArgumentList.Add("--ts"); startInfo.ArgumentList.Add(tsLib);
        startInfo.ArgumentList.Add("--repo"); startInfo.ArgumentList.Add(repositoryId);

        var process = Process.Start(startInfo);
        if (process is null) return null;
        // Drain stderr continuously so the child never blocks writing to a full pipe; content is
        // only surfaced (truncated) if a request fails while this daemon was still the active one.
        _ = Task.Run(async () =>
        {
            try { while (await process.StandardError.ReadLineAsync() is not null) { } }
            catch { /* process gone */ }
        });
        return new TsDaemon(process);
    }

    /// <summary>Sends one request line and awaits the matching response line. Returns null (and
    /// marks the daemon dead) on timeout, process exit, or malformed output — callers should
    /// discard this instance and let the next call start a fresh one.</summary>
    public async Task<JsonDocument?> RequestAsync(object requestPayload, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!IsAlive) return null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsAlive) return null;
            var line = JsonSerializer.Serialize(requestPayload);
            await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            string? responseLine;
            try { responseLine = await _process.StandardOutput.ReadLineAsync(timeoutSource.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _alive = false; // this call's own timeout, not caller cancellation: daemon is stuck, discard it
                return null;
            }
            if (responseLine is null) { _alive = false; return null; }
            return JsonDocument.Parse(responseLine);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            _alive = false;
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _alive = false;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        try { await _process.WaitForExitAsync(); } catch { /* best effort */ }
        _process.Dispose();
    }
}
