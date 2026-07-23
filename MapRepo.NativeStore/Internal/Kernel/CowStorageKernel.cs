using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MapRepo.NativeStore.Internal.Packing;

namespace MapRepo.NativeStore.Internal.Kernel;

internal sealed class CowStorageKernel
{
    private const string SuperblockA = "superblock.a";
    private const string SuperblockB = "superblock.b";
    private readonly string _repositoryId;
    private readonly string _directory;
    private readonly string _segmentsDirectory;
    private readonly string _snapshotsDirectory;
    private readonly string _manifestsDirectory;
    private readonly string _temporaryDirectory;
    private readonly string _writerLeasePath;
    private readonly NativeStoreOptions _options;

    public CowStorageKernel(string repositoryId, string directory, NativeStoreOptions options)
    {
        _repositoryId = repositoryId;
        _directory = directory;
        _segmentsDirectory = Path.Combine(directory, "segments");
        _snapshotsDirectory = Path.Combine(directory, "snapshots");
        _manifestsDirectory = Path.Combine(directory, "manifests");
        _temporaryDirectory = Path.Combine(directory, "tmp");
        _writerLeasePath = directory + ".writer.lock";
        _options = options;
    }

    public string DirectoryPath => _directory;
    public string TemporaryDirectory => _temporaryDirectory;
    public string SnapshotPath(SnapshotDescriptor descriptor) => Path.Combine(_snapshotsDirectory, descriptor.FileName);

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(_segmentsDirectory);
        Directory.CreateDirectory(_snapshotsDirectory);
        Directory.CreateDirectory(_manifestsDirectory);
        Directory.CreateDirectory(_temporaryDirectory);
        return Task.CompletedTask;
    }

    public async Task<KernelMetadataResult> LoadLatestMetadataAsync(
        CancellationToken cancellationToken,
        bool verifySnapshotContents = false,
        bool? verifySegments = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_directory))
            return new KernelMetadataResult(StoreManifest.Empty(_repositoryId), []);
        var notes = new List<string>();
        var pointers = await ReadPointersAsync(notes, cancellationToken).ConfigureAwait(false);
        var newest = pointers.Count == 0 ? 0 : pointers.Max(value => value.Pointer.Sequence);
        foreach (var candidate in pointers.OrderByDescending(value => value.Pointer.Sequence))
        {
            try
            {
                var manifest = await LoadManifestFromPointerAsync(
                    candidate.Pointer,
                    validateSnapshot: true,
                    verifySnapshotContents: verifySnapshotContents,
                    verifySegments: verifySegments ?? verifySnapshotContents,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (candidate.Pointer.Sequence != newest)
                    notes.Add($"Recovered repository from sequence {candidate.Pointer.Sequence} after rejecting a newer generation.");
                return new KernelMetadataResult(manifest, notes);
            }
            catch (Exception ex) when (IsRecoverableReadFailure(ex))
            {
                notes.Add($"Rejected sequence {candidate.Pointer.Sequence} from {candidate.Slot}: {ex.Message}");
            }
        }
        return new KernelMetadataResult(StoreManifest.Empty(_repositoryId), notes);
    }

    public async Task<KernelLoadResult> LoadLatestAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var notes = new List<string>();
        var pointers = await ReadPointersAsync(notes, cancellationToken).ConfigureAwait(false);
        var newest = pointers.Count == 0 ? 0 : pointers.Max(value => value.Pointer.Sequence);
        foreach (var candidate in pointers.OrderByDescending(value => value.Pointer.Sequence))
        {
            try
            {
                var manifest = await LoadManifestFromPointerAsync(
                    candidate.Pointer,
                    validateSnapshot: true,
                    verifySnapshotContents: false,
                    verifySegments: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var segments = await LoadSegmentsAsync(manifest, cancellationToken).ConfigureAwait(false);
                if (candidate.Pointer.Sequence != newest)
                    notes.Add($"Recovered repository from sequence {candidate.Pointer.Sequence} after rejecting a newer generation.");
                return new KernelLoadResult(manifest, segments, notes);
            }
            catch (Exception ex) when (IsRecoverableReadFailure(ex))
            {
                notes.Add($"Rejected sequence {candidate.Pointer.Sequence} from {candidate.Slot}: {ex.Message}");
            }
        }
        return new KernelLoadResult(
            StoreManifest.Empty(_repositoryId),
            ImmutableDictionary<FileModuleKey, FileSegmentData>.Empty,
            notes);
    }

    public async Task<KernelCommitResult> CommitAsync(
        StoreManifest expectedManifest,
        string generation,
        DateTimeOffset indexedAt,
        IReadOnlyList<string> diagnostics,
        IReadOnlyCollection<FileModuleKey> removals,
        IReadOnlyDictionary<FileModuleKey, FileSegmentData> upserts,
        SnapshotPackBuildResult snapshotPack,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await WriterLease.AcquireAsync(
            _writerLeasePath,
            _options.WriterLeaseTimeout,
            cancellationToken).ConfigureAwait(false);

        try
        {
            var diskState = await LoadLatestMetadataAsync(cancellationToken).ConfigureAwait(false);
            if (diskState.Manifest.Sequence != expectedManifest.Sequence)
                throw new ConcurrentStoreWriteException(expectedManifest.Sequence, diskState.Manifest.Sequence);

            var sequence = checked(expectedManifest.Sequence + 1);
            if (snapshotPack.Sequence != sequence)
                throw new InvalidOperationException("Prepared snapshot sequence differs from the next durable sequence.");
            var context = new StoreFaultContext(_repositoryId, sequence);
            var descriptors = expectedManifest.ActiveSegments.ToBuilder();
            foreach (var key in removals) descriptors.Remove(key);

            foreach (var pair in upserts.OrderBy(value => value.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = pair.Key;
                var segment = pair.Value;
                descriptors.Remove(key);
                if (segment.IsEmpty) continue;

                var (bytes, checksum) = SegmentCodec.Encode(segment, sequence);
                if (bytes.LongLength > _options.MaxSegmentBytes)
                    throw new IOException($"Segment for '{key.FilePath}' exceeds MaxSegmentBytes.");
                var fileName = SegmentFileName(sequence, key);
                var finalPath = Path.Combine(_segmentsDirectory, fileName);
                var temporaryPath = NewTemporaryPath("segment");
                var fileContext = context with { FilePath = key.FilePath };
                await AtomicFile.WriteNewAsync(
                    temporaryPath,
                    finalPath,
                    bytes,
                    _options,
                    () => Hit(StoreFaultPoint.AfterSegmentBytesWritten, fileContext),
                    () => Hit(StoreFaultPoint.AfterSegmentDurable, fileContext),
                    () => Hit(StoreFaultPoint.AfterSegmentPublished, fileContext),
                    cancellationToken).ConfigureAwait(false);

                descriptors[key] = new SegmentDescriptor(
                    key,
                    fileName,
                    sequence,
                    checksum,
                    bytes.LongLength,
                    segment.Symbols.Length,
                    segment.Relationships.Length);
            }

            var snapshotFileName = $"snapshot-{sequence:D20}-{Guid.NewGuid():N}.mrp";
            var snapshotPath = Path.Combine(_snapshotsDirectory, snapshotFileName);
            File.Move(snapshotPack.Path, snapshotPath, overwrite: false);
            Hit(StoreFaultPoint.AfterSnapshotPublished, context);
            var snapshotDescriptor = new SnapshotDescriptor(
                snapshotFileName,
                sequence,
                snapshotPack.RootChecksum,
                snapshotPack.Length,
                snapshotPack.SymbolCount,
                snapshotPack.RelationshipCount,
                snapshotPack.ResolvedRelationshipCount,
                snapshotPack.FileCount);

            var manifest = new StoreManifest(
                sequence,
                _repositoryId,
                generation,
                indexedAt,
                diagnostics.ToImmutableArray(),
                descriptors.ToImmutable(),
                snapshotDescriptor);
            var (manifestBytes, manifestChecksum) = ManifestCodec.Encode(manifest);
            var manifestFileName = $"manifest-{sequence:D20}-{Guid.NewGuid():N}.mrm";
            var manifestPath = Path.Combine(_manifestsDirectory, manifestFileName);
            await AtomicFile.WriteNewAsync(
                NewTemporaryPath("manifest"),
                manifestPath,
                manifestBytes,
                _options,
                () => Hit(StoreFaultPoint.AfterManifestBytesWritten, context),
                () => Hit(StoreFaultPoint.AfterManifestDurable, context),
                () => Hit(StoreFaultPoint.AfterManifestPublished, context),
                cancellationToken).ConfigureAwait(false);

            Hit(StoreFaultPoint.BeforeSuperblockPublish, context);
            var pointer = new SuperblockPointer(
                sequence,
                manifestFileName,
                manifestChecksum,
                manifestBytes.LongLength,
                DateTimeOffset.UtcNow);
            var superblockBytes = SuperblockCodec.Encode(pointer);
            var slotPath = Path.Combine(_directory, (sequence & 1) == 0 ? SuperblockA : SuperblockB);
            await AtomicFile.ReplaceAsync(
                NewTemporaryPath("superblock"),
                slotPath,
                superblockBytes,
                _options,
                () => Hit(StoreFaultPoint.AfterSuperblockBytesWritten, context),
                () => Hit(StoreFaultPoint.AfterSuperblockDurable, context),
                () => Hit(StoreFaultPoint.AfterSuperblockPublished, context),
                cancellationToken).ConfigureAwait(false);

            if (_options.CleanupObsoleteFiles)
            {
                try { await CleanupObsoleteAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
            }
            return new KernelCommitResult(manifest, snapshotPath);
        }
        catch
        {
            TryDelete(snapshotPack.Path);
            throw;
        }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_directory))
        {
            TryDelete(_writerLeasePath);
            return;
        }

        await using (var lease = await WriterLease.AcquireAsync(
            _writerLeasePath,
            _options.WriterLeaseTimeout,
            cancellationToken).ConfigureAwait(false))
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        TryDelete(_writerLeasePath);
    }

    private async Task<List<(string Slot, SuperblockPointer Pointer)>> ReadPointersAsync(
        List<string> notes,
        CancellationToken cancellationToken)
    {
        var pointers = new List<(string Slot, SuperblockPointer Pointer)>();
        foreach (var slot in new[] { SuperblockA, SuperblockB })
        {
            var path = Path.Combine(_directory, slot);
            if (!File.Exists(path)) continue;
            try
            {
                var bytes = await ReadBoundedAsync(path, 1024 * 1024, cancellationToken).ConfigureAwait(false);
                pointers.Add((slot, SuperblockCodec.Decode(bytes, _options)));
            }
            catch (Exception ex) when (IsRecoverableReadFailure(ex))
            {
                notes.Add($"Ignored invalid {slot}: {ex.Message}");
            }
        }
        return pointers;
    }

    private async Task<StoreManifest> LoadManifestFromPointerAsync(
        SuperblockPointer pointer,
        bool validateSnapshot,
        bool verifySnapshotContents,
        bool verifySegments,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(_manifestsDirectory, pointer.ManifestFileName);
        var manifestBytes = await ReadBoundedAsync(manifestPath, _options.MaxManifestBytes, cancellationToken).ConfigureAwait(false);
        if (manifestBytes.LongLength != pointer.ManifestLength) throw new InvalidDataException("Manifest length differs from superblock.");
        if (ManifestCodec.GetChecksum(manifestBytes) != pointer.ManifestChecksum) throw new InvalidDataException("Manifest checksum differs from superblock.");
        var manifest = ManifestCodec.Decode(manifestBytes, _options);
        if (manifest.Sequence != pointer.Sequence) throw new InvalidDataException("Manifest sequence differs from superblock.");
        if (!string.Equals(manifest.RepositoryId, _repositoryId, StringComparison.Ordinal))
            throw new InvalidDataException("Manifest belongs to another repository.");
        if (validateSnapshot && manifest.Snapshot is { } descriptor)
        {
            var path = SnapshotPath(descriptor);
            if (verifySnapshotContents)
            {
                using var mapped = MappedFile.OpenRead(path, _options.MaxSnapshotPackBytes, NativeMemoryMode.MemoryMapped);
                _ = PackHeaderCodec.Decode(mapped, _repositoryId, descriptor, _options, verifySections: true);
            }
            else
            {
                var (header, length) = await ReadPackHeaderAsync(path, cancellationToken).ConfigureAwait(false);
                _ = PackHeaderCodec.DecodeHeader(header, length, _repositoryId, descriptor, _options);
            }
        }

        // Segment validation is decoupled from the (heavier) full pack-content check: the status
        // fast path wants a corrupt per-file segment to trigger fallback without paying for a full
        // mmap decode of the consolidated query pack on every status call.
        if (verifySegments)
            await ValidateSegmentsAsync(manifest, cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    private async Task ValidateSegmentsAsync(StoreManifest manifest, CancellationToken cancellationToken)
    {
        foreach (var descriptor in manifest.ActiveSegments.Values.OrderBy(static value => value.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(_segmentsDirectory, descriptor.FileName);
            var bytes = await ReadBoundedAsync(path, _options.MaxSegmentBytes, cancellationToken).ConfigureAwait(false);
            _ = SegmentCodec.Decode(bytes, descriptor, _options);
        }
    }

    private async Task<ImmutableDictionary<FileModuleKey, FileSegmentData>> LoadSegmentsAsync(
        StoreManifest manifest,
        CancellationToken cancellationToken)
    {
        var segments = ImmutableDictionary.CreateBuilder<FileModuleKey, FileSegmentData>();
        foreach (var descriptor in manifest.ActiveSegments.Values.OrderBy(value => value.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(_segmentsDirectory, descriptor.FileName);
            var bytes = await ReadBoundedAsync(path, _options.MaxSegmentBytes, cancellationToken).ConfigureAwait(false);
            segments.Add(descriptor.Key, SegmentCodec.Decode(bytes, descriptor, _options));
        }
        return segments.ToImmutable();
    }

    private async Task CleanupObsoleteAsync(CancellationToken cancellationToken)
    {
        var keepManifests = new HashSet<string>(StringComparer.Ordinal);
        var keepSegments = new HashSet<string>(StringComparer.Ordinal);
        var keepSnapshots = new HashSet<string>(StringComparer.Ordinal);
        var verifiedEveryExistingSlot = true;
        foreach (var slot in new[] { SuperblockA, SuperblockB })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var superblockPath = Path.Combine(_directory, slot);
            if (!File.Exists(superblockPath)) continue;
            try
            {
                var pointer = SuperblockCodec.Decode(
                    await ReadBoundedAsync(superblockPath, 1024 * 1024, cancellationToken).ConfigureAwait(false),
                    _options);
                var manifestPath = Path.Combine(_manifestsDirectory, pointer.ManifestFileName);
                var bytes = await ReadBoundedAsync(manifestPath, _options.MaxManifestBytes, cancellationToken).ConfigureAwait(false);
                if (ManifestCodec.GetChecksum(bytes) != pointer.ManifestChecksum)
                {
                    verifiedEveryExistingSlot = false;
                    continue;
                }
                var manifest = ManifestCodec.Decode(bytes, _options);
                keepManifests.Add(pointer.ManifestFileName);
                foreach (var descriptor in manifest.ActiveSegments.Values) keepSegments.Add(descriptor.FileName);
                if (manifest.Snapshot is { } snapshot) keepSnapshots.Add(snapshot.FileName);
            }
            catch (Exception ex) when (IsRecoverableReadFailure(ex))
            {
                verifiedEveryExistingSlot = false;
            }
        }

        if (!verifiedEveryExistingSlot) return;
        DeleteUnreferenced(_manifestsDirectory, "manifest-*.mrm", keepManifests);
        DeleteUnreferenced(_segmentsDirectory, "seg-*.mrs", keepSegments);
        DeleteUnreferenced(_snapshotsDirectory, "snapshot-*.mrp", keepSnapshots);
        CleanupTemporaryFiles();
    }

    private static void DeleteUnreferenced(string directory, string pattern, HashSet<string> keep)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
        {
            if (keep.Contains(Path.GetFileName(path))) continue;
            TryDelete(path);
        }
    }

    private void CleanupTemporaryFiles()
    {
        if (!Directory.Exists(_temporaryDirectory)) return;
        foreach (var path in Directory.EnumerateFiles(_temporaryDirectory, "*.tmp", SearchOption.TopDirectoryOnly)) TryDelete(path);
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, long maximumLength, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 64 * 1024
        });
        var length = stream.Length;
        if (length < sizeof(uint) || length > maximumLength || length > int.MaxValue)
            throw new InvalidDataException($"File length {length} is outside the accepted range.");
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)length));
        await stream.ReadExactlyAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (stream.Length != length) throw new IOException("Committed store file changed while it was being read.");
        return bytes;
    }

    private async Task<(byte[] Header, long Length)> ReadPackHeaderAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.RandomAccess,
            BufferSize = PackLayout.HeaderSize
        });
        var length = stream.Length;
        if (length < PackLayout.HeaderSize || length > _options.MaxSnapshotPackBytes)
            throw new InvalidDataException($"Snapshot-pack length {length} is outside the accepted range.");
        var header = GC.AllocateUninitializedArray<byte>(PackLayout.HeaderSize);
        await stream.ReadExactlyAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (stream.Length != length) throw new IOException("Snapshot pack changed while its header was being read.");
        return (header, length);
    }

    private string NewTemporaryPath(string kind) => Path.Combine(_temporaryDirectory, $"{kind}-{Guid.NewGuid():N}.tmp");

    private static string SegmentFileName(long sequence, FileModuleKey key)
    {
        var bytes = Encoding.UTF8.GetBytes($"{key.ModuleId}\0{key.FilePath}");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16];
        return $"seg-{sequence:D20}-{hash}-{Guid.NewGuid():N}.mrs";
    }

    private void Hit(StoreFaultPoint point, StoreFaultContext context) => _options.FaultInjector?.Hit(point, context);

    private static bool IsRecoverableReadFailure(Exception ex) =>
        ex is IOException or InvalidDataException or EndOfStreamException or DecoderFallbackException or UnauthorizedAccessException;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed class WriterLease : IAsyncDisposable
    {
        private readonly FileStream _stream;

        private WriterLease(FileStream stream) => _stream = stream;

        public static async Task<WriterLease> AcquireAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var stream = new FileStream(path, new FileStreamOptions
                    {
                        Mode = FileMode.OpenOrCreate,
                        Access = FileAccess.ReadWrite,
                        Share = FileShare.None,
                        Options = FileOptions.None,
                        BufferSize = 4096
                    });
                    stream.SetLength(0);
                    var metadata = Encoding.UTF8.GetBytes($"pid={Environment.ProcessId};utc={DateTimeOffset.UtcNow:O}\n");
                    stream.Write(metadata);
                    stream.Flush();
                    return new WriterLease(stream);
                }
                catch (IOException ex)
                {
                    if (DateTime.UtcNow >= deadline)
                        throw new TimeoutException($"Could not acquire the repository writer lease within {timeout}.", ex);
                    await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public ValueTask DisposeAsync() => _stream.DisposeAsync();
    }
}
