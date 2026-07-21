using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using MapRepo.Core;

namespace MapRepo.Server;

public sealed class RepositorySessionManager : IAsyncDisposable
{
    private readonly ModuleRegistry _registry;
    private readonly IRepositoryStore _store;
    private readonly RepositoryCatalog _catalog;
    private readonly ConcurrentDictionary<string, RepositorySession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public RepositorySessionManager(ModuleRegistry registry, IRepositoryStore store, RepositoryCatalog catalog)
    {
        _registry = registry;
        _store = store;
        _catalog = catalog;
    }

    public IReadOnlyCollection<IRepositoryLanguageModule> Modules => _registry.Modules;

    /// <summary>Reopens every cataloged repository. Existing indexes are reused; only empty ones reindex.</summary>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        foreach (var definition in _catalog.All())
        {
            try { await OpenAsync(definition, reindex: false, cancellationToken); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or InvalidOperationException or SqliteException)
            {
                // A missing path or a corrupt database for one repository must not stop the server
                // from restoring the rest; it stays cataloged and can be repaired/reindexed later.
                _ = ex;
            }
        }
    }

    public async Task<RepositoryStatus> OpenAsync(RepositoryDefinition definition, bool reindex = false, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(definition.RootPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Repository root not found: {root}");
        var normalized = definition with { RootPath = root, SolutionPath = string.IsNullOrWhiteSpace(definition.SolutionPath) ? null : Path.GetFullPath(definition.SolutionPath) };
        var session = _sessions.GetOrAdd(normalized.Id, _ => new RepositorySession(normalized, _registry, _store));
        _catalog.Upsert(normalized);
        session.StartWatcher();
        await session.EnsureInitialIndexStartedAsync(reindex, cancellationToken);
        return await session.StatusAsync(cancellationToken);
    }

    public RepositorySession Get(string id) => _sessions.TryGetValue(id, out var session)
        ? session
        : throw new KeyNotFoundException($"Repository '{id}' is not open. Use open_repository or list_repositories.");

    public async Task<IReadOnlyList<RepositorySummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var summaries = new List<RepositorySummary>();
        foreach (var definition in _catalog.All())
        {
            var status = _sessions.TryGetValue(definition.Id, out var session)
                ? await session.StatusAsync(cancellationToken)
                : await _store.StatusAsync(definition.Id, cancellationToken);
            summaries.Add(new RepositorySummary(definition, status));
        }
        return summaries;
    }

    public async Task<bool> CloseAsync(string id)
    {
        if (!_sessions.TryRemove(id, out var session)) return false;
        await session.DisposeAsync();
        ReleaseModuleState(id);
        return true;
    }

    public async Task<bool> RemoveAsync(string id, bool deleteData, CancellationToken cancellationToken = default)
    {
        var existed = _catalog.Remove(id);
        if (_sessions.TryRemove(id, out var session)) { await session.DisposeAsync(); existed = true; }
        ReleaseModuleState(id);
        if (deleteData) await _store.DeleteAsync(id, cancellationToken);
        return existed;
    }

    private void ReleaseModuleState(string id)
    {
        foreach (var module in _registry.Modules.OfType<IRepositoryLifecycle>()) module.ReleaseRepository(id);
    }

    public RepositoryDefinition? Definition(string id) =>
        _sessions.TryGetValue(id, out var session) ? session.Definition
        : _catalog.All().FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads an exact line range from a file inside the repository root. Never leaves the root.</summary>
    public async Task<SourceSlice> SourceAsync(string id, string relativePath, int startLine, int endLine, CancellationToken cancellationToken = default)
    {
        var definition = Definition(id) ?? throw new KeyNotFoundException($"Repository '{id}' is not registered");
        var root = Path.GetFullPath(definition.RootPath);
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path escapes the repository root");
        if (!File.Exists(full)) throw new FileNotFoundException($"File not found: {relativePath}");
        var lines = await File.ReadAllLinesAsync(full, cancellationToken);
        var start = Math.Clamp(startLine <= 0 ? 1 : startLine, 1, Math.Max(1, lines.Length));
        var requestedEnd = endLine <= 0 ? start + 60 : endLine;
        var truncated = requestedEnd - start + 1 > 400;
        var end = Math.Clamp(Math.Min(requestedEnd, start + 399), start, lines.Length);
        var content = lines.Length == 0 ? string.Empty : string.Join('\n', lines[(start - 1)..end]);
        return new SourceSlice(definition.Id, relativePath.Replace('\\', '/'), start, end, lines.Length, content, truncated || requestedEnd > lines.Length);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values) await session.DisposeAsync();
        _sessions.Clear();
    }
}

public sealed class RepositorySession : IAsyncDisposable
{
    private static readonly string[] SourceExtensions =
        [".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"];

    private readonly RepositoryDefinition _definition;
    private readonly ModuleRegistry _registry;
    private readonly IRepositoryStore _store;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly object _watchLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);
    private int _watcherActive;
    private int _initialIndexStarted;
    private int _indexing;
    private RepositoryStatus? _lastStatus;
    private string? _lastIndexError;
    private string? _lastWatcherError;
    // Tracked so DisposeAsync can wait for any in-flight index before disposing _indexLock;
    // otherwise a background index task holding the semaphore hits ObjectDisposedException on Release().
    private volatile Task _lastIndexTask = Task.CompletedTask;

    public RepositorySession(RepositoryDefinition definition, ModuleRegistry registry, IRepositoryStore store)
    {
        _definition = definition;
        _registry = registry;
        _store = store;
    }

    public RepositoryDefinition Definition => _definition;
    public bool WatcherActive => Volatile.Read(ref _watcherActive) == 1;
    public bool IsIndexing => Volatile.Read(ref _indexing) == 1;

    /// <summary>Skips the initial full index when the per-repository database already has symbols.</summary>
    public async Task EnsureInitialIndexStartedAsync(bool force, CancellationToken cancellationToken = default)
    {
        if (force)
        {
            Volatile.Write(ref _initialIndexStarted, 1);
            QueueIndex(Array.Empty<string>());
            return;
        }
        if (Interlocked.Exchange(ref _initialIndexStarted, 1) != 0) return;
        var status = await _store.StatusAsync(_definition.Id, cancellationToken);
        _lastStatus = status;
        if (status.Symbols == 0) QueueIndex(Array.Empty<string>());
    }

    private void QueueIndex(IReadOnlyList<string> changedPaths)
    {
        Volatile.Write(ref _indexing, 1);
        _lastIndexTask = Task.Run(async () =>
        {
            try { await IndexAsync(changedPaths, _lifetime.Token); }
            catch (OperationCanceledException) { /* session disposed while queued/running */ }
            catch (Exception ex)
            {
                _lastIndexError = ex.Message;
                Volatile.Write(ref _indexing, 0);
            }
        });
    }

    public async Task IndexAsync(IReadOnlyList<string> changedPaths, CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _indexing, 1);
        await _indexLock.WaitAsync(cancellationToken);
        try
        {
            _lastIndexError = null;
            IReadOnlyList<IRepositoryLanguageModule> modules = _registry.Resolve(_definition);

            // Incremental fast path: modules that can patch just the changed documents do so;
            // the rest (or a null delta: new files, cold workspace) fall through to a full module run.
            if (changedPaths.Count > 0)
            {
                var pending = new List<IRepositoryLanguageModule>();
                var incrementalDiagnostics = new List<string>();
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var patchedFiles = 0;
                foreach (var module in modules)
                {
                    var relevant = changedPaths.Where(module.CanAnalyze).ToArray();
                    if (relevant.Length == 0) continue;
                    if (module is IIncrementalAnalyzer incremental)
                    {
                        var delta = await incremental.AnalyzeFilesAsync(new AnalysisRequest(_definition, relevant, cancellationToken));
                        if (delta is not null)
                        {
                            if (delta.FilePaths.Count > 0)
                            {
                                await _store.ReplaceFilesAsync(_definition.Id, module.Descriptor.Id, delta.FilePaths,
                                    delta.Symbols, delta.Relationships, Generation(), DateTimeOffset.UtcNow, cancellationToken);
                                patchedFiles += delta.FilePaths.Count;
                            }
                            incrementalDiagnostics.AddRange(delta.Diagnostics);
                            continue;
                        }
                    }
                    pending.Add(module);
                }
                if (patchedFiles > 0)
                    Console.WriteLine($"[map-repo] {_definition.Id}: incremental reindex of {patchedFiles} file(s) in {stopwatch.ElapsedMilliseconds} ms");
                if (pending.Count == 0)
                {
                    var stored = await _store.StatusAsync(_definition.Id, cancellationToken);
                    _lastStatus = stored with
                    {
                        WatcherActive = WatcherActive,
                        Diagnostics = incrementalDiagnostics.Concat(stored.Diagnostics).Distinct(StringComparer.Ordinal).Take(200).ToArray()
                    };
                    return;
                }
                modules = pending;
            }

            var tasks = modules.Select(async module =>
            {
                var relevant = changedPaths.Where(module.CanAnalyze).ToArray();
                if (changedPaths.Count > 0 && relevant.Length == 0) return (module.Descriptor.Id, Snapshot: (AnalysisSnapshot?)null, Failed: false);
                try { return (module.Descriptor.Id, Snapshot: (AnalysisSnapshot?)await module.AnalyzeAsync(new AnalysisRequest(_definition, relevant, cancellationToken)), Failed: false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return (module.Descriptor.Id, Snapshot: (AnalysisSnapshot?)new AnalysisSnapshot(_definition.Id, Generation(), [], [],
                        [$"Module {module.Descriptor.Id}: {ex.Message}"], DateTimeOffset.UtcNow), Failed: true);
                }
            });
            var results = await Task.WhenAll(tasks);
            // Only modules that analyzed successfully replace their rows; skipped or failed modules keep their stored data.
            var succeeded = results.Where(r => r.Snapshot is not null && !r.Failed).ToArray();
            var snapshots = succeeded.Select(r => r.Snapshot!).ToArray();
            var ranModuleIds = succeeded.Select(r => r.Id).ToArray();
            var diagnostics = results.Where(r => r.Snapshot is not null).SelectMany(r => r.Snapshot!.Diagnostics)
                .Distinct(StringComparer.Ordinal).Take(200).ToArray();
            if (ranModuleIds.Length > 0)
            {
                var symbols = snapshots.SelectMany(s => s.Symbols).GroupBy(s => s.Id).Select(g => g.First()).ToArray();
                var symbolIds = symbols.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
                var relationships = snapshots.SelectMany(s => s.Relationships)
                    .Where(e => symbolIds.Contains(e.SourceId) && symbolIds.Contains(e.TargetId))
                    .GroupBy(e => e.Id).Select(g => g.First()).ToArray();
                var generation = Generation();
                var indexedAt = DateTimeOffset.UtcNow;
                var fullRun = changedPaths.Count == 0 && ranModuleIds.Length == modules.Count;
                await _store.ReplaceAsync(new AnalysisSnapshot(_definition.Id, generation, symbols, relationships, diagnostics, indexedAt),
                    fullRun ? null : ranModuleIds, cancellationToken);
                var stored = await _store.StatusAsync(_definition.Id, cancellationToken);
                _lastStatus = stored with { WatcherActive = WatcherActive, Diagnostics = diagnostics };
            }
            else if (diagnostics.Length > 0)
            {
                _lastIndexError = diagnostics[0];
            }
        }
        finally
        {
            Volatile.Write(ref _indexing, 0);
            _indexLock.Release();
        }
    }

    public Task<RepositoryStatus> StatusAsync(CancellationToken cancellationToken = default) => StatusCoreAsync(cancellationToken);

    private async Task<RepositoryStatus> StatusCoreAsync(CancellationToken cancellationToken)
    {
        if (IsIndexing)
        {
            var cached = _lastStatus ?? new RepositoryStatus(_definition.Id, null, 0, 0, null, WatcherActive, true, []);
            return WithRuntimeDiagnostics(cached, "Indexing in progress");
        }

        var status = await _store.StatusAsync(_definition.Id, cancellationToken);
        _lastStatus = status;
        return WithRuntimeDiagnostics(status);
    }

    public void StartWatcher()
    {
        if (WatcherActive) return;
        var watcher = new FileSystemWatcher(_definition.RootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            Filter = "*.*",
            // Default 8 KB overflows silently on bulk operations (git checkout, npm install) inside
            // a watched repo, dropping events with no error — raise it well above the default.
            InternalBufferSize = 1 << 16,
            EnableRaisingEvents = true
        };
        watcher.Created += OnFileChanged;
        watcher.Changed += OnFileChanged;
        watcher.Renamed += OnFileRenamed;
        watcher.Deleted += OnFileChanged;
        watcher.Error += OnWatcherError;
        lock (_watchLock) { _watcher = watcher; _debounce = new Timer(_ => DrainChanges(), null, Timeout.Infinite, Timeout.Infinite); }
        Volatile.Write(ref _watcherActive, 1);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args) => Enqueue(args.FullPath);

    // A rename touches two paths: the old one must lose its stale symbols, the new one must be (re)indexed.
    private void OnFileRenamed(object sender, RenamedEventArgs args)
    {
        Enqueue(args.OldFullPath);
        Enqueue(args.FullPath);
    }

    private void Enqueue(string path)
    {
        if (!IsInteresting(path)) return;
        lock (_watchLock) { _changed.Add(path); _debounce?.Change(750, Timeout.Infinite); }
    }

    // A dropped/overflowed watcher (buffer overflow, watched directory recreated, etc.) means file
    // events may have been lost silently. Surface a diagnostic and force a full reindex rather than
    // trust a _changed set that might be missing entries.
    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        _lastWatcherError = args.GetException()?.Message ?? "unknown watcher error";
        lock (_watchLock) { _changed.Clear(); }
        QueueIndex(Array.Empty<string>());
    }

    private bool IsInteresting(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("packages", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("Data", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("dist", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("build", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("coverage", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("obj", StringComparison.OrdinalIgnoreCase))) return false;
        return SourceExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    private void DrainChanges()
    {
        string[] changes;
        lock (_watchLock) { changes = _changed.ToArray(); _changed.Clear(); }
        Volatile.Write(ref _indexing, 1);
        _lastIndexTask = Task.Run(async () =>
        {
            try { await IndexAsync(changes, _lifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _lastIndexError = ex.Message; Volatile.Write(ref _indexing, 0); }
        });
    }

    private RepositoryStatus WithRuntimeDiagnostics(RepositoryStatus status, string? runtimeDiagnostic = null)
    {
        var diagnostics = new List<string>();
        if (!string.IsNullOrWhiteSpace(runtimeDiagnostic)) diagnostics.Add(runtimeDiagnostic);
        if (!string.IsNullOrWhiteSpace(_lastIndexError)) diagnostics.Add($"Last index failed: {_lastIndexError}");
        if (!string.IsNullOrWhiteSpace(_lastWatcherError)) diagnostics.Add($"Watcher error, forced full reindex: {_lastWatcherError}");
        diagnostics.AddRange(status.Diagnostics);
        return status with { WatcherActive = WatcherActive, Indexing = IsIndexing, Diagnostics = diagnostics.Distinct(StringComparer.Ordinal).Take(200).ToArray() };
    }

    private static string Generation() => DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");

    public async ValueTask DisposeAsync()
    {
        lock (_watchLock)
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
            _debounce?.Dispose();
            _debounce = null;
        }
        Volatile.Write(ref _watcherActive, 0);

        // Ask any in-flight/queued index to cancel, then wait for it to actually finish before
        // disposing _indexLock — otherwise a background task still holding the semaphore throws
        // ObjectDisposedException on Release(), which previously got swallowed as _lastIndexError.
        await _lifetime.CancelAsync();
        try { await _lastIndexTask.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }

        _indexLock.Dispose();
        _lifetime.Dispose();
    }
}
