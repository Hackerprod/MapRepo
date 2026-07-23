using System.Collections.Immutable;
using MapRepo.Core;
using MapRepo.NativeStore.Internal.Kernel;
using MapRepo.NativeStore.Internal.Packing;

namespace MapRepo.NativeStore.Internal;

internal sealed class RepositoryHandle : IAsyncDisposable
{
    private readonly string _repositoryId;
    private readonly NativeStoreOptions _options;
    private readonly CowStorageKernel _kernel;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private RepositorySnapshot _snapshot;
    private int _disposed;

    private RepositoryHandle(
        string repositoryId,
        NativeStoreOptions options,
        CowStorageKernel kernel,
        RepositorySnapshot snapshot)
    {
        _repositoryId = repositoryId;
        _options = options;
        _kernel = kernel;
        _snapshot = snapshot;
    }

    public string StoragePath => _kernel.DirectoryPath;
    public long EstimatedManagedBytes => Volatile.Read(ref _snapshot).EstimatedManagedBytes;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public static async Task<RepositoryHandle> OpenAsync(
        string repositoryId,
        string directory,
        NativeStoreOptions options,
        CancellationToken cancellationToken)
    {
        var kernel = new CowStorageKernel(repositoryId, directory, options);
        var metadata = await kernel.LoadLatestMetadataAsync(
            cancellationToken,
            verifySnapshotContents: options.VerifySnapshotPackChecksumsOnOpen).ConfigureAwait(false);
        if (metadata.Manifest.Sequence == 0)
        {
            return new RepositoryHandle(repositoryId, options, kernel,
                RepositorySnapshot.Empty(metadata.Manifest, options, metadata.RecoveryNotes));
        }

        if (metadata.Manifest.Snapshot is { } snapshotDescriptor)
        {
            var snapshot = RepositorySnapshot.Open(
                metadata.Manifest,
                kernel.SnapshotPath(snapshotDescriptor),
                options,
                metadata.RecoveryNotes,
                verifyChecksums: false);
            return new RepositoryHandle(repositoryId, options, kernel, snapshot);
        }

        // Format-1 stores are upgraded transactionally. Source segments remain authoritative;
        // the next generation adds the compact immutable query pack and a v2 manifest.
        var legacy = await kernel.LoadLatestAsync(cancellationToken).ConfigureAwait(false);
        var source = new SegmentRecordSource(legacy.Segments);
        var pack = await SnapshotPackBuilder.BuildTemporaryAsync(
            repositoryId,
            checked(legacy.Manifest.Sequence + 1),
            legacy.Manifest.IndexedAt,
            source,
            [],
            ImmutableDictionary<FileModuleKey, FileSegmentData>.Empty,
            kernel.TemporaryDirectory,
            options,
            cancellationToken).ConfigureAwait(false);

        KernelCommitResult upgraded;
        try
        {
            upgraded = await kernel.CommitAsync(
                legacy.Manifest,
                legacy.Manifest.Generation,
                legacy.Manifest.IndexedAt,
                legacy.Manifest.Diagnostics,
                [],
                ImmutableDictionary<FileModuleKey, FileSegmentData>.Empty,
                pack,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ConcurrentStoreWriteException)
        {
            var latest = await kernel.LoadLatestMetadataAsync(
                cancellationToken,
                verifySnapshotContents: options.VerifySnapshotPackChecksumsOnOpen).ConfigureAwait(false);
            if (latest.Manifest.Snapshot is null) throw;
            var current = RepositorySnapshot.Open(
                latest.Manifest,
                kernel.SnapshotPath(latest.Manifest.Snapshot),
                options,
                latest.RecoveryNotes,
                verifyChecksums: false);
            return new RepositoryHandle(repositoryId, options, kernel, current);
        }

        var descriptor = upgraded.Manifest.Snapshot
            ?? throw new InvalidDataException("Upgraded manifest has no snapshot pack.");
        var opened = RepositorySnapshot.Open(
            upgraded.Manifest,
            kernel.SnapshotPath(descriptor),
            options,
            legacy.RecoveryNotes,
            verifyChecksums: false);
        return new RepositoryHandle(repositoryId, options, kernel, opened);
    }

    public RepositorySnapshot.SnapshotLease AcquireSnapshot()
    {
        while (true)
        {
            ThrowIfDisposed();
            var current = Volatile.Read(ref _snapshot);
            try
            {
                return current.Acquire();
            }
            catch (ObjectDisposedException) when (!ReferenceEquals(current, Volatile.Read(ref _snapshot)))
            {
                // A writer published the next immutable snapshot between the read and Acquire().
            }
        }
    }

    public Task ReplaceAsync(
        AnalysisSnapshot analysis,
        IReadOnlyList<string>? moduleIds,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(analysis.RepositoryId, _repositoryId, StringComparison.Ordinal))
            throw new ArgumentException("Analysis snapshot belongs to another repository.", nameof(analysis));

        var selectedModules = moduleIds is { Count: > 0 }
            ? moduleIds.Distinct(StringComparer.Ordinal).ToArray()
            : analysis.Symbols.Select(static value => value.ModuleId)
                .Concat(analysis.Relationships.Select(static value => value.ModuleId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

        var replacements = selectedModules.Select(moduleId => new ModuleSnapshotDelta(
            moduleId,
            analysis.Symbols.Where(value => string.Equals(value.ModuleId, moduleId, StringComparison.Ordinal)).ToArray(),
            analysis.Relationships.Where(value => string.Equals(value.ModuleId, moduleId, StringComparison.Ordinal)).ToArray()))
            .ToArray();

        return ApplyMutationAsync(new RepositoryMutation(
            analysis.RepositoryId,
            analysis.Generation,
            analysis.CreatedAt,
            analysis.Diagnostics,
            ReplaceAll: moduleIds is not { Count: > 0 },
            PreserveExistingDiagnostics: moduleIds is { Count: > 0 },
            ModuleReplacements: replacements,
            FileDeltas: []), cancellationToken);
    }

    public Task ReplaceFilesAsync(
        string moduleId,
        IReadOnlyList<string> filePaths,
        IReadOnlyList<SymbolRecord> symbols,
        IReadOnlyList<RelationshipRecord> relationships,
        string generation,
        DateTimeOffset indexedAt,
        CancellationToken cancellationToken) =>
        ApplyMutationAsync(new RepositoryMutation(
            _repositoryId,
            generation,
            indexedAt,
            [],
            ReplaceAll: false,
            PreserveExistingDiagnostics: true,
            ModuleReplacements: [],
            FileDeltas: [new FileModuleDelta(moduleId, filePaths, symbols, relationships)]), cancellationToken);

    public async Task ApplyMutationAsync(RepositoryMutation mutation, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateMutation(mutation);

        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var lease = AcquireSnapshot();
            var current = lease.Snapshot;
            var removals = new HashSet<FileModuleKey>();
            if (mutation.ReplaceAll)
            {
                removals.UnionWith(current.Manifest.ActiveSegments.Keys);
            }

            var replacementModules = new HashSet<string>(StringComparer.Ordinal);
            var symbols = new List<SymbolRecord>();
            var relationships = new List<RelationshipRecord>();
            foreach (var replacement in mutation.ModuleReplacements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!replacementModules.Add(replacement.ModuleId))
                    throw new InvalidDataException($"Module '{replacement.ModuleId}' appears more than once in one mutation.");
                if (!mutation.ReplaceAll)
                {
                    removals.UnionWith(current.Manifest.ActiveSegments.Keys.Where(
                        key => string.Equals(key.ModuleId, replacement.ModuleId, StringComparison.Ordinal)));
                }
                AddModuleRecords(replacement.ModuleId, replacement.Symbols, replacement.Relationships, symbols, relationships);
            }

            var fileKeys = new HashSet<FileModuleKey>();
            foreach (var delta in mutation.FileDeltas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (replacementModules.Contains(delta.ModuleId))
                    throw new InvalidDataException($"Module '{delta.ModuleId}' cannot be fully replaced and file-patched in the same mutation.");
                if (string.IsNullOrWhiteSpace(delta.ModuleId))
                    throw new InvalidDataException("An incremental delta has no module ID.");

                var normalizedPaths = delta.FilePaths
                    .Select(FileModuleKey.NormalizePath)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var allowed = normalizedPaths.ToHashSet(StringComparer.Ordinal);
                foreach (var path in normalizedPaths)
                {
                    var key = FileModuleKey.Create(delta.ModuleId, path);
                    if (!fileKeys.Add(key))
                        throw new InvalidDataException($"File '{path}' for module '{delta.ModuleId}' appears more than once in one mutation.");
                    removals.Add(key);
                }

                AddFileDeltaRecords(
                    delta.ModuleId,
                    allowed,
                    delta.Symbols,
                    delta.Relationships,
                    symbols,
                    relationships);
            }

            var upserts = BuildSegments(symbols, relationships);
            var diagnostics = EffectiveDiagnostics(current.Manifest.Diagnostics, mutation);
            if (removals.Count == 0 && upserts.Count == 0 &&
                diagnostics.SequenceEqual(current.Manifest.Diagnostics, StringComparer.Ordinal))
            {
                return;
            }

            await CommitAndPublishAsync(
                current,
                mutation.Generation,
                mutation.IndexedAt,
                diagnostics,
                removals,
                upserts,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<int> PurgePathAsync(string pathPattern, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(pathPattern)) return 0;
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var lease = AcquireSnapshot();
            var current = lease.Snapshot;
            var removals = current.Manifest.ActiveSegments.Keys
                .Where(key => key.FilePath.Contains(pathPattern, StringComparison.OrdinalIgnoreCase))
                .ToHashSet();
            if (removals.Count == 0) return 0;
            var removed = removals.Sum(key =>
                current.Manifest.ActiveSegments.TryGetValue(key, out var descriptor)
                    ? descriptor.SymbolCount
                    : 0);
            var now = DateTimeOffset.UtcNow;
            await CommitAndPublishAsync(
                current,
                now.ToString("yyyyMMddHHmmssfff"),
                now,
                current.Manifest.Diagnostics,
                removals,
                ImmutableDictionary<FileModuleKey, FileSegmentData>.Empty,
                cancellationToken).ConfigureAwait(false);
            return removed;
        }
        finally
        {
            _writer.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _writer.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            Volatile.Read(ref _snapshot).Retire();
        }
        finally
        {
            _writer.Release();
            _writer.Dispose();
        }
    }

    private async Task CommitAndPublishAsync(
        RepositorySnapshot current,
        string generation,
        DateTimeOffset indexedAt,
        IReadOnlyList<string> diagnostics,
        IReadOnlyCollection<FileModuleKey> removals,
        IReadOnlyDictionary<FileModuleKey, FileSegmentData> upserts,
        CancellationToken cancellationToken)
    {
        var pack = await SnapshotPackBuilder.BuildTemporaryAsync(
            _repositoryId,
            checked(current.Manifest.Sequence + 1),
            indexedAt,
            current,
            removals,
            upserts,
            _kernel.TemporaryDirectory,
            _options,
            cancellationToken).ConfigureAwait(false);

        KernelCommitResult result;
        try
        {
            result = await _kernel.CommitAsync(
                current.Manifest,
                generation,
                indexedAt,
                diagnostics,
                removals,
                upserts,
                pack,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await ReloadDurableSnapshotAsync().ConfigureAwait(false);
            throw;
        }

        var context = new StoreFaultContext(_repositoryId, result.Manifest.Sequence);
        try
        {
            _options.FaultInjector?.Hit(StoreFaultPoint.BeforeInMemorySnapshotPublish, context);
            var descriptor = result.Manifest.Snapshot
                ?? throw new InvalidDataException("Committed manifest has no snapshot pack.");
            var next = RepositorySnapshot.Open(
                result.Manifest,
                _kernel.SnapshotPath(descriptor),
                _options,
                verifyChecksums: false);
            Publish(next);
            _options.FaultInjector?.Hit(StoreFaultPoint.AfterInMemorySnapshotPublish, context);
        }
        catch
        {
            await ReloadDurableSnapshotAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReloadDurableSnapshotAsync()
    {
        try
        {
            var loaded = await _kernel.LoadLatestMetadataAsync(
                CancellationToken.None,
                verifySnapshotContents: _options.VerifySnapshotPackChecksumsOnOpen).ConfigureAwait(false);
            RepositorySnapshot next;
            if (loaded.Manifest.Sequence == 0)
            {
                next = RepositorySnapshot.Empty(loaded.Manifest, _options, loaded.RecoveryNotes);
            }
            else if (loaded.Manifest.Snapshot is { } descriptor)
            {
                next = RepositorySnapshot.Open(
                    loaded.Manifest,
                    _kernel.SnapshotPath(descriptor),
                    _options,
                    loaded.RecoveryNotes,
                    verifyChecksums: false);
            }
            else
            {
                return;
            }

            if (loaded.Manifest.Sequence >= Volatile.Read(ref _snapshot).Manifest.Sequence) Publish(next);
            else next.Retire();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Preserve the original commit exception; a later process reopen still performs full recovery.
        }
    }

    private void Publish(RepositorySnapshot next)
    {
        var previous = Interlocked.Exchange(ref _snapshot, next);
        if (!ReferenceEquals(previous, next)) previous.Retire();
    }

    private ImmutableDictionary<FileModuleKey, FileSegmentData> BuildSegments(
        IEnumerable<SymbolRecord> symbols,
        IEnumerable<RelationshipRecord> relationships)
    {
        var groups = new Dictionary<FileModuleKey, (List<SymbolRecord> Symbols, List<RelationshipRecord> Relationships)>();
        foreach (var raw in symbols)
        {
            ValidateRecord(raw.RepositoryId, raw.ModuleId, raw.FilePath, raw.Id, "symbol");
            var symbol = raw with { FilePath = FileModuleKey.NormalizePath(raw.FilePath) };
            var key = FileModuleKey.Create(symbol.ModuleId, symbol.FilePath);
            if (!groups.TryGetValue(key, out var group)) group = ([], []);
            group.Symbols.Add(symbol);
            groups[key] = group;
        }

        foreach (var raw in relationships)
        {
            ValidateRecord(raw.RepositoryId, raw.ModuleId, raw.FilePath, raw.Id, "relationship");
            if (string.IsNullOrWhiteSpace(raw.SourceId) || string.IsNullOrWhiteSpace(raw.TargetId))
                throw new InvalidDataException($"Relationship '{raw.Id}' has an empty source or target ID.");
            var relationship = raw with { FilePath = FileModuleKey.NormalizePath(raw.FilePath) };
            var key = FileModuleKey.Create(relationship.ModuleId, relationship.FilePath);
            if (!groups.TryGetValue(key, out var group)) group = ([], []);
            group.Relationships.Add(relationship);
            groups[key] = group;
        }

        var result = ImmutableDictionary.CreateBuilder<FileModuleKey, FileSegmentData>();
        foreach (var pair in groups.OrderBy(static value => value.Key))
        {
            var uniqueSymbols = Deduplicate(pair.Value.Symbols, static value => value.Id, "symbol", pair.Key.FilePath);
            var uniqueRelationships = Deduplicate(pair.Value.Relationships, static value => value.Id, "relationship", pair.Key.FilePath);
            if ((long)uniqueSymbols.Length + uniqueRelationships.Length > _options.MaxRecordsPerSegment)
            {
                throw new InvalidDataException(
                    $"The segment for '{pair.Key.FilePath}' exceeds MaxRecordsPerSegment ({_options.MaxRecordsPerSegment:N0}).");
            }
            result[pair.Key] = new FileSegmentData(pair.Key, uniqueSymbols, uniqueRelationships);
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<T> Deduplicate<T>(
        IEnumerable<T> values,
        Func<T, string> idSelector,
        string recordKind,
        string filePath)
        where T : notnull
    {
        var result = ImmutableArray.CreateBuilder<T>();
        foreach (var group in values.GroupBy(idSelector, StringComparer.Ordinal).OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            var first = group.First();
            if (group.Skip(1).Any(value => !EqualityComparer<T>.Default.Equals(first, value)))
                throw new ConflictingRecordIdentityException(recordKind, group.Key, filePath);
            result.Add(first);
        }
        return result.ToImmutable();
    }

    private static void AddFileDeltaRecords(
        string moduleId,
        IReadOnlySet<string> allowedPaths,
        IReadOnlyList<SymbolRecord> sourceSymbols,
        IReadOnlyList<RelationshipRecord> sourceRelationships,
        List<SymbolRecord> destinationSymbols,
        List<RelationshipRecord> destinationRelationships)
    {
        foreach (var value in sourceSymbols)
        {
            var normalizedPath = FileModuleKey.NormalizePath(value.FilePath);
            if (!string.Equals(value.ModuleId, moduleId, StringComparison.Ordinal) || !allowedPaths.Contains(normalizedPath))
            {
                throw new InvalidDataException(
                    $"Symbol '{value.Id}' is outside the declared incremental delta for module '{moduleId}'.");
            }
            destinationSymbols.Add(value);
        }

        foreach (var value in sourceRelationships)
        {
            var normalizedPath = FileModuleKey.NormalizePath(value.FilePath);
            if (!string.Equals(value.ModuleId, moduleId, StringComparison.Ordinal) || !allowedPaths.Contains(normalizedPath))
            {
                throw new InvalidDataException(
                    $"Relationship '{value.Id}' is outside the declared incremental delta for module '{moduleId}'.");
            }
            destinationRelationships.Add(value);
        }
    }

    private static void AddModuleRecords(
        string moduleId,
        IReadOnlyList<SymbolRecord> sourceSymbols,
        IReadOnlyList<RelationshipRecord> sourceRelationships,
        List<SymbolRecord> destinationSymbols,
        List<RelationshipRecord> destinationRelationships)
    {
        if (string.IsNullOrWhiteSpace(moduleId))
            throw new InvalidDataException("A module replacement has no module ID.");
        foreach (var value in sourceSymbols)
        {
            if (!string.Equals(value.ModuleId, moduleId, StringComparison.Ordinal))
                throw new InvalidDataException($"Symbol '{value.Id}' does not belong to replacement module '{moduleId}'.");
            destinationSymbols.Add(value);
        }
        foreach (var value in sourceRelationships)
        {
            if (!string.Equals(value.ModuleId, moduleId, StringComparison.Ordinal))
                throw new InvalidDataException($"Relationship '{value.Id}' does not belong to replacement module '{moduleId}'.");
            destinationRelationships.Add(value);
        }
    }

    private static IReadOnlyList<string> EffectiveDiagnostics(
        IReadOnlyList<string> current,
        RepositoryMutation mutation)
    {
        IEnumerable<string> values = mutation.PreserveExistingDiagnostics
            ? current.Concat(mutation.Diagnostics)
            : mutation.Diagnostics;
        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(200)
            .ToArray();
    }

    private void ValidateMutation(RepositoryMutation mutation)
    {
        if (!string.Equals(mutation.RepositoryId, _repositoryId, StringComparison.Ordinal))
            throw new ArgumentException("Mutation belongs to another repository.", nameof(mutation));
        if (string.IsNullOrWhiteSpace(mutation.Generation))
            throw new ArgumentException("Mutation generation is required.", nameof(mutation));
        if (mutation.ReplaceAll && mutation.PreserveExistingDiagnostics)
            throw new ArgumentException("A full replacement cannot preserve diagnostics from the discarded generation.", nameof(mutation));
    }

    private void ValidateRecord(string repositoryId, string moduleId, string filePath, string id, string kind)
    {
        if (!string.Equals(repositoryId, _repositoryId, StringComparison.Ordinal))
            throw new InvalidDataException($"A {kind} belongs to another repository.");
        if (string.IsNullOrWhiteSpace(moduleId) || string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(id))
            throw new InvalidDataException($"A {kind} has an empty module, path, or ID.");
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(RepositoryHandle));
    }
}
