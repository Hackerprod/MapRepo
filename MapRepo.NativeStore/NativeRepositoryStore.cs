using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MapRepo.Core;
using MapRepo.NativeStore.Internal;
using MapRepo.NativeStore.Internal.Kernel;
using MapRepo.NativeStore.Projection;

namespace MapRepo.NativeStore;

/// <summary>
/// Embedded MapRepo-specific store: immutable file revisions, copy-on-write manifests,
/// dual-superblock recovery, compact graph/search packs, bounded caches, and memory-mapped reads.
/// </summary>
public sealed class NativeRepositoryStore : IRepositoryStore, ITransactionalRepositoryStore, IProjectedRepositoryStore, IAsyncDisposable
{
    private readonly NativeStoreOptions _options;
    private readonly string _rootDirectory;
    private readonly ConcurrentDictionary<string, HandleEntry> _handles = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _trimGate = new(1, 1);
    private int _disposed;

    public NativeRepositoryStore(string rootDirectory)
        : this(new NativeStoreOptions { RootDirectory = rootDirectory })
    {
    }

    public NativeRepositoryStore(NativeStoreOptions options)
    {
        _options = options.Validate();
        _rootDirectory = Path.GetFullPath(_options.RootDirectory);
        Directory.CreateDirectory(_rootDirectory);
    }

    public string RootDirectory => _rootDirectory;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_rootDirectory);
        return Task.CompletedTask;
    }

    public async Task ReplaceAsync(AnalysisSnapshot snapshot, IReadOnlyList<string>? moduleIds = null, CancellationToken cancellationToken = default)
    {
        await using var lease = await GetHandleLeaseAsync(snapshot.RepositoryId, cancellationToken).ConfigureAwait(false);
        await lease.Handle.ReplaceAsync(snapshot, moduleIds, cancellationToken).ConfigureAwait(false);
        await TrimBestEffortAsync().ConfigureAwait(false);
    }

    public async Task ReplaceFilesAsync(
        string repositoryId,
        string moduleId,
        IReadOnlyList<string> filePaths,
        IReadOnlyList<SymbolRecord> symbols,
        IReadOnlyList<RelationshipRecord> relationships,
        string generation,
        DateTimeOffset indexedAt,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await GetHandleLeaseAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        await lease.Handle.ReplaceFilesAsync(moduleId, filePaths, symbols, relationships, generation, indexedAt, cancellationToken)
            .ConfigureAwait(false);
        await TrimBestEffortAsync().ConfigureAwait(false);
    }

    public async Task ApplyMutationAsync(
        RepositoryMutation mutation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        await using var lease = await GetHandleLeaseAsync(mutation.RepositoryId, cancellationToken).ConfigureAwait(false);
        await lease.Handle.ApplyMutationAsync(mutation, cancellationToken).ConfigureAwait(false);
        await TrimBestEffortAsync().ConfigureAwait(false);
    }

    public async Task<SearchOutcome> SearchAsync(
        string repositoryId,
        string query,
        int limit,
        SearchFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        await using var handle = await GetHandleLeaseAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        using var snapshot = handle.Handle.AcquireSnapshot();
        return snapshot.Snapshot.SearchSymbols(query, limit, filter, cancellationToken);
    }

    public async Task<GraphResult> GraphAsync(
        string repositoryId,
        string symbolId,
        int depth,
        int limit,
        IReadOnlyList<string>? edgeKinds = null,
        CancellationToken cancellationToken = default)
    {
        await using var handle = await GetHandleLeaseAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        using var snapshot = handle.Handle.AcquireSnapshot();
        return snapshot.Snapshot.GetGraph(repositoryId, symbolId, depth, limit, edgeKinds, cancellationToken);
    }

    /// <summary>Metadata-only status path: it does not open or retain the heavy query snapshot.</summary>
    public async Task<RepositoryStatus> StatusAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_handles.TryGetValue(repositoryId, out var residentEntry) &&
            residentEntry.TryGetHandle(out var residentHandle) && residentHandle is not null && !residentHandle.IsDisposed)
        {
            try
            {
                using var residentSnapshot = residentHandle.AcquireSnapshot();
                return residentSnapshot.Snapshot.GetStatus(repositoryId);
            }
            catch (ObjectDisposedException)
            {
                // Concurrent eviction won the race. Fall through to the metadata-only path.
            }
        }

        var kernel = new CowStorageKernel(repositoryId, StoragePath(repositoryId), _options);
        var loaded = await kernel.LoadLatestMetadataAsync(
            cancellationToken,
            verifySnapshotContents: false,
            verifySegments: _options.VerifySnapshotPackChecksumsOnOpen).ConfigureAwait(false);
        var manifest = loaded.Manifest;
        if (manifest.Sequence == 0)
            return new RepositoryStatus(repositoryId, null, 0, 0, null, false, false, loaded.RecoveryNotes);
        var (diagnostics, summary) = RepositoryDiagnostics.Split(manifest.Diagnostics);
        if (loaded.RecoveryNotes.Count > 0)
            diagnostics = diagnostics.Concat(loaded.RecoveryNotes).Distinct(StringComparer.Ordinal).ToArray();
        return new RepositoryStatus(
            repositoryId,
            manifest.Generation,
            manifest.Snapshot?.SymbolCount ?? manifest.ActiveSegments.Values.Sum(static value => value.SymbolCount),
            manifest.Snapshot?.RelationshipCount ?? manifest.ActiveSegments.Values.Sum(static value => value.RelationshipCount),
            manifest.IndexedAt,
            false,
            false,
            diagnostics,
            summary);
    }

    public async Task<RepositoryOverview> OverviewAsync(
        string repositoryId,
        bool includeGenerated = false,
        CancellationToken cancellationToken = default)
    {
        await using var handle = await GetHandleLeaseAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        using var snapshot = handle.Handle.AcquireSnapshot();
        return snapshot.Snapshot.GetOverview(repositoryId, includeGenerated, cancellationToken);
    }

    public async Task<FileOutline> OutlineAsync(
        string repositoryId,
        string filePath,
        int maxSymbols = 500,
        CancellationToken cancellationToken = default)
    {
        await using var handle = await GetHandleLeaseAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        using var snapshot = handle.Handle.AcquireSnapshot();
        return snapshot.Snapshot.GetOutline(repositoryId, filePath, maxSymbols, cancellationToken);
    }

    public async Task<IReadOnlyList<FileEntry>> FilesAsync(
        string repositoryId,
        string? contains,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var handle = await GetHandleLeaseAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        using var snapshot = handle.Handle.AcquireSnapshot();
        return snapshot.Snapshot.GetFiles(contains, limit, cancellationToken);
    }

    public async Task<SymbolDetail?> SymbolAsync(
        string repositoryId,
        string symbolId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var handle = await GetHandleLeaseAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        using var snapshot = handle.Handle.AcquireSnapshot();
        return snapshot.Snapshot.GetSymbol(symbolId, limit, cancellationToken);
    }

    public async Task DeleteAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_handles.TryRemove(repositoryId, out var entry))
        {
            entry.BeginEviction();
            await entry.WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
            if (entry.TryGetCreatedTask(out var task))
            {
                try
                {
                    var handle = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    await handle.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException) { }
            }
        }
        await new CowStorageKernel(repositoryId, StoragePath(repositoryId), _options).DeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PurgePathAsync(string repositoryId, string pathPattern, CancellationToken cancellationToken = default)
    {
        await using var lease = await GetHandleLeaseAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        var removed = await lease.Handle.PurgePathAsync(pathPattern, cancellationToken).ConfigureAwait(false);
        await TrimBestEffortAsync().ConfigureAwait(false);
        return removed;
    }

    public string StoragePath(string repositoryId)
    {
        var slug = new string(repositoryId.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-').ToArray());
        if (slug.Length > 48) slug = slug[..48];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repositoryId))).ToLowerInvariant()[..10];
        return Path.Combine(_rootDirectory, $"{slug}__{hash}");
    }

    /// <summary>Full checksum and source-segment verification. Intended for release gates, not hot requests.</summary>
    public async Task<NativeStoreVerificationResult> VerifyAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var path = StoragePath(repositoryId);
        var hadCommittedMetadata = Directory.Exists(path) &&
            (File.Exists(Path.Combine(path, "superblock.a")) || File.Exists(Path.Combine(path, "superblock.b")));
        try
        {
            var kernel = new CowStorageKernel(repositoryId, path, _options);
            var loaded = await kernel.LoadLatestAsync(cancellationToken).ConfigureAwait(false);
            if (loaded.Manifest.Sequence == 0)
            {
                var validEmpty = !hadCommittedMetadata;
                return new NativeStoreVerificationResult(repositoryId, validEmpty, 0, null, 0, 0, 0,
                    validEmpty ? loaded.RecoveryNotes : loaded.RecoveryNotes.Concat(["Committed metadata exists, but no generation is recoverable."]).ToArray());
            }
            if (loaded.Manifest.Snapshot is null)
                throw new InvalidDataException("The active manifest has no compact snapshot pack.");
            var openedSnapshot = RepositorySnapshot.Open(
                loaded.Manifest,
                kernel.SnapshotPath(loaded.Manifest.Snapshot),
                _options,
                loaded.RecoveryNotes,
                verifyChecksums: true);
            try
            {
                using var snapshot = openedSnapshot.Acquire();
                var sourceSymbols = new Dictionary<string, SymbolRecord>(StringComparer.Ordinal);
                var sourceRelationships = new Dictionary<string, RelationshipRecord>(StringComparer.Ordinal);
                foreach (var segment in loaded.Segments.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var symbol in segment.Symbols)
                        if (!sourceSymbols.TryAdd(symbol.Id, symbol))
                            throw new InvalidDataException($"Duplicate symbol ID in source segments: {symbol.Id}");
                    foreach (var relationship in segment.Relationships)
                        if (!sourceRelationships.TryAdd(relationship.Id, relationship))
                            throw new InvalidDataException($"Duplicate relationship ID in source segments: {relationship.Id}");
                }

                var packedSymbols = new Dictionary<string, SymbolRecord>(sourceSymbols.Count, StringComparer.Ordinal);
                foreach (var symbol in snapshot.Snapshot.EnumerateSymbols(cancellationToken))
                    if (!packedSymbols.TryAdd(symbol.Id, symbol))
                        throw new InvalidDataException($"Duplicate symbol ID in compact pack: {symbol.Id}");
                var packedRelationships = new Dictionary<string, RelationshipRecord>(sourceRelationships.Count, StringComparer.Ordinal);
                foreach (var relationship in snapshot.Snapshot.EnumerateRelationships(cancellationToken))
                    if (!packedRelationships.TryAdd(relationship.Id, relationship))
                        throw new InvalidDataException($"Duplicate relationship ID in compact pack: {relationship.Id}");

                EnsureExactParity(sourceSymbols, packedSymbols, "symbol");
                EnsureExactParity(sourceRelationships, packedRelationships, "relationship");
                var expectedResolvedRelationships = sourceRelationships.Values.Count(relationship =>
                    sourceSymbols.ContainsKey(relationship.SourceId) && sourceSymbols.ContainsKey(relationship.TargetId));
                if (expectedResolvedRelationships != snapshot.Snapshot.ResolvedRelationshipCount)
                    throw new InvalidDataException($"Resolved-relationship count differs: source={expectedResolvedRelationships}, pack={snapshot.Snapshot.ResolvedRelationshipCount}.");

                return new NativeStoreVerificationResult(
                    repositoryId,
                    true,
                    loaded.Manifest.Sequence,
                    loaded.Manifest.Generation,
                    loaded.Manifest.ActiveSegments.Count,
                    packedSymbols.Count,
                    packedRelationships.Count,
                    loaded.RecoveryNotes.Concat(["Source segments and compact snapshot pack match exactly."]).ToArray(),
                    snapshot.Snapshot.ResolvedRelationshipCount);

                static void EnsureExactParity<T>(
                    IReadOnlyDictionary<string, T> expected,
                    IReadOnlyDictionary<string, T> actual,
                    string label)
                {
                    if (expected.Count != actual.Count)
                        throw new InvalidDataException($"{label} count differs: source={expected.Count}, pack={actual.Count}.");
                    foreach (var pair in expected)
                    {
                        if (!actual.TryGetValue(pair.Key, out var packed))
                            throw new InvalidDataException($"Compact pack is missing {label} '{pair.Key}'.");
                        if (!EqualityComparer<T>.Default.Equals(pair.Value, packed))
                            throw new InvalidDataException($"Compact-pack {label} '{pair.Key}' differs from its source segment.");
                    }
                }
            }
            finally
            {
                openedSnapshot.Retire();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new NativeStoreVerificationResult(repositoryId, false, 0, null, 0, 0, 0, [ex.Message]);
        }
    }

    public async Task<SymbolProjectionResult> ProjectSymbolsAsync(
        SymbolProjectionRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var handle = await GetHandleLeaseAsync(request.RepositoryId, cancellationToken).ConfigureAwait(false);
        using var snapshot = handle.Handle.AcquireSnapshot();
        return snapshot.Snapshot.ProjectSymbols(request, cancellationToken);
    }

    public async Task<FileProjectionResult> ProjectFilesAsync(
        string repositoryId,
        string? contains,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var handle = await GetHandleLeaseAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        using var snapshot = handle.Handle.AcquireSnapshot();
        return snapshot.Snapshot.ProjectFiles(contains, limit, cancellationToken);
    }

    public async Task<FileOutlineProjectionResult> ProjectOutlineAsync(
        string repositoryId,
        string filePath,
        int maxSymbols,
        bool compact,
        CancellationToken cancellationToken = default)
    {
        await using var handle = await GetHandleLeaseAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        using var snapshot = handle.Handle.AcquireSnapshot();
        return snapshot.Snapshot.ProjectOutline(repositoryId, filePath, maxSymbols, compact, cancellationToken);
    }

    public async Task<ProjectedSymbolDetailResult?> ProjectSymbolDetailAsync(
        string repositoryId,
        string symbolId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var handle = await GetHandleLeaseAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        using var snapshot = handle.Handle.AcquireSnapshot();
        return snapshot.Snapshot.ProjectSymbolDetail(symbolId, limit, cancellationToken);
    }

    public async Task<ProjectedGraphResult> ProjectGraphAsync(
        string repositoryId,
        string symbolId,
        int depth,
        int limit,
        IReadOnlyList<string>? edgeKinds = null,
        CancellationToken cancellationToken = default)
    {
        await using var handle = await GetHandleLeaseAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        using var snapshot = handle.Handle.AcquireSnapshot();
        return snapshot.Snapshot.ProjectGraph(repositoryId, symbolId, depth, limit, edgeKinds, cancellationToken);
    }

    public NativeStoreRuntimeStats GetRuntimeStats()
    {
        ThrowIfDisposed();
        var resident = 0;
        var active = 0;
        long estimatedManagedBytes = 0;
        long backingPackBytes = 0;
        long decodedStringCacheBytes = 0;
        var materializedRecords = 0;
        var memoryMapped = 0;
        var compactManaged = 0;
        foreach (var entry in _handles.Values)
        {
            if (!entry.TryGetHandle(out var handle) || handle is null || handle.IsDisposed) continue;
            try
            {
                using var lease = handle.AcquireSnapshot();
                resident++;
                active += entry.ActiveOperations;
                estimatedManagedBytes += lease.Snapshot.EstimatedManagedBytes;
                backingPackBytes += lease.Snapshot.BackingFileBytes;
                decodedStringCacheBytes += lease.Snapshot.DecodedStringCacheBytes;
                materializedRecords += lease.Snapshot.MaterializedRecordCacheEntries;
                if (lease.Snapshot.IsMemoryMapped) memoryMapped++; else compactManaged++;
            }
            catch (ObjectDisposedException)
            {
                // A concurrent LRU eviction won the race. Runtime statistics are explicitly best effort.
            }
        }

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return new NativeStoreRuntimeStats(
            resident,
            active,
            estimatedManagedBytes,
            backingPackBytes,
            decodedStringCacheBytes,
            materializedRecords,
            memoryMapped,
            compactManaged,
            GC.GetGCMemoryInfo().HeapSizeBytes,
            Environment.WorkingSet,
            process.PrivateMemorySize64);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var entries = _handles.ToArray();
        _handles.Clear();
        foreach (var pair in entries) pair.Value.BeginEviction();
        foreach (var pair in entries)
        {
            try
            {
                await pair.Value.WaitForIdleAsync(CancellationToken.None).ConfigureAwait(false);
                if (pair.Value.TryGetCreatedTask(out var task))
                    await (await task.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            }
            catch { }
        }
        try
        {
            await _trimGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            _trimGate.Release();
        }
        catch (ObjectDisposedException) { }
        _trimGate.Dispose();
    }

    private async Task<HandleLease> GetHandleLeaseAsync(string repositoryId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(repositoryId)) throw new ArgumentException("Repository ID is required.", nameof(repositoryId));
        while (true)
        {
            var entry = _handles.GetOrAdd(repositoryId, id => new HandleEntry(
                id,
                () => RepositoryHandle.OpenAsync(id, StoragePath(id), _options, CancellationToken.None)));
            if (!entry.TryAcquire())
            {
                TryRemoveEntry(repositoryId, entry);
                continue;
            }
            try
            {
                var handle = await entry.GetHandleAsync(cancellationToken).ConfigureAwait(false);
                ScheduleTrim();
                return new HandleLease(entry, handle);
            }
            catch
            {
                entry.Release();
                if (entry.IsFaulted) TryRemoveEntry(repositoryId, entry);
                throw;
            }
        }
    }

    private async Task TrimBestEffortAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 || !await _trimGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var now = DateTime.UtcNow.Ticks;
            while (true)
            {
                var residents = _handles.Values.Where(static entry => entry.TryGetHandle(out _)).ToArray();
                var totalBytes = residents.Sum(entry => entry.TryGetHandle(out var handle) ? handle!.EstimatedManagedBytes : 0);
                var overCount = residents.Length > _options.MaxResidentRepositories;
                var overBytes = totalBytes > _options.MaxResidentManagedBytes;
                var candidate = residents
                    .Where(entry => entry.ActiveOperations == 0)
                    .OrderBy(entry => entry.LastAccessTicks)
                    .FirstOrDefault(entry => overCount || overBytes ||
                        (_options.IdleRepositoryTimeout > TimeSpan.Zero && now - entry.LastAccessTicks >= _options.IdleRepositoryTimeout.Ticks));
                if (candidate is null || !candidate.BeginEviction()) break;
                TryRemoveEntry(candidate.RepositoryId, candidate);
                await candidate.WaitForIdleAsync(CancellationToken.None).ConfigureAwait(false);
                if (candidate.TryGetCreatedTask(out var task))
                {
                    try { await (await task.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
                    catch { }
                }
            }
        }
        finally { _trimGate.Release(); }
    }

    private bool TryRemoveEntry(string repositoryId, HandleEntry expected) =>
        ((ICollection<KeyValuePair<string, HandleEntry>>)_handles).Remove(
            new KeyValuePair<string, HandleEntry>(repositoryId, expected));

    private void ScheduleTrim() => _ = TrimSilentlyAsync();

    private async Task TrimSilentlyAsync()
    {
        try { await TrimBestEffortAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class HandleEntry
    {
        private readonly Lazy<Task<RepositoryHandle>> _handle;
        private readonly TaskCompletionSource<bool> _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _evicting;
        private long _lastAccessTicks = DateTime.UtcNow.Ticks;

        public HandleEntry(string repositoryId, Func<Task<RepositoryHandle>> factory)
        {
            RepositoryId = repositoryId;
            _handle = new Lazy<Task<RepositoryHandle>>(factory, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public string RepositoryId { get; }
        public int ActiveOperations => Volatile.Read(ref _active);
        public long LastAccessTicks => Volatile.Read(ref _lastAccessTicks);
        public bool IsFaulted => _handle.IsValueCreated && _handle.Value.IsFaulted;

        public bool TryAcquire()
        {
            while (true)
            {
                if (Volatile.Read(ref _evicting) != 0) return false;
                var current = Volatile.Read(ref _active);
                if (Interlocked.CompareExchange(ref _active, current + 1, current) != current) continue;
                if (Volatile.Read(ref _evicting) == 0)
                {
                    Volatile.Write(ref _lastAccessTicks, DateTime.UtcNow.Ticks);
                    return true;
                }
                Release();
                return false;
            }
        }

        public void Release()
        {
            Volatile.Write(ref _lastAccessTicks, DateTime.UtcNow.Ticks);
            if (Interlocked.Decrement(ref _active) == 0 && Volatile.Read(ref _evicting) != 0) _idle.TrySetResult(true);
        }

        public bool BeginEviction()
        {
            if (Interlocked.CompareExchange(ref _evicting, 1, 0) != 0) return false;
            if (Volatile.Read(ref _active) == 0) _idle.TrySetResult(true);
            return true;
        }

        public async Task<RepositoryHandle> GetHandleAsync(CancellationToken cancellationToken) =>
            await _handle.Value.WaitAsync(cancellationToken).ConfigureAwait(false);

        public Task WaitForIdleAsync(CancellationToken cancellationToken) => _idle.Task.WaitAsync(cancellationToken);

        public bool TryGetHandle(out RepositoryHandle? handle)
        {
            handle = null;
            if (!_handle.IsValueCreated || !_handle.Value.IsCompletedSuccessfully) return false;
            handle = _handle.Value.Result;
            return true;
        }

        public bool TryGetCreatedTask(out Task<RepositoryHandle> task)
        {
            if (_handle.IsValueCreated) { task = _handle.Value; return true; }
            task = null!;
            return false;
        }
    }

    private sealed class HandleLease(HandleEntry entry, RepositoryHandle handle) : IAsyncDisposable
    {
        private HandleEntry? _entry = entry;
        public RepositoryHandle Handle { get; } = handle;
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _entry, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
