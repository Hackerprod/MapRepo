namespace MapRepo.NativeStore;

/// <summary>Representation used by opened repository snapshots.</summary>
public enum NativeMemoryMode
{
    /// <summary>Read the compact binary pack into one managed byte array. Useful for diagnostics and comparison.</summary>
    CompactManaged = 0,

    /// <summary>Keep the immutable binary pack on disk and expose only demanded pages through a read-only memory map.</summary>
    MemoryMapped = 1
}

/// <summary>Configuration for the embedded MapRepo-native store.</summary>
public sealed class NativeStoreOptions
{
    /// <summary>Root directory containing one isolated store directory per repository.</summary>
    public required string RootDirectory { get; init; }

    /// <summary>Representation used after opening or publishing a repository generation.</summary>
    public NativeMemoryMode MemoryMode { get; init; } = NativeMemoryMode.MemoryMapped;

    /// <summary>Flush segment, manifest, superblock, and snapshot-pack bytes through the OS before publication.</summary>
    public bool FlushToDisk { get; init; } = true;

    /// <summary>Ask the OS to bypass part of its write cache for commit files.</summary>
    public bool WriteThrough { get; init; } = true;

    /// <summary>Maximum time a second process waits for the single-writer lease.</summary>
    public TimeSpan WriterLeaseTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Maximum accepted immutable source-segment size.</summary>
    public long MaxSegmentBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Maximum accepted manifest size.</summary>
    public long MaxManifestBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Maximum accepted compact snapshot-pack size.</summary>
    public long MaxSnapshotPackBytes { get; init; } = 2_000_000_000L;

    /// <summary>Maximum accepted UTF-8 string size in persistent metadata formats.</summary>
    public int MaxStringBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>Maximum symbols plus relationships accepted in one immutable file segment.</summary>
    public int MaxRecordsPerSegment { get; init; } = 2_000_000;

    /// <summary>Maximum symbols plus relationships accepted in one consolidated query snapshot.</summary>
    public int MaxRecordsPerSnapshot { get; init; } = 20_000_000;

    /// <summary>Maximum lexicon rows expanded for one prefix token before candidate verification.</summary>
    public int MaxPrefixLexemesPerToken { get; init; } = 4_096;

    /// <summary>Reject duplicate symbol and relationship IDs before commit.</summary>
    public bool StrictIdentityValidation { get; init; } = true;

    /// <summary>Delete files not referenced by either recoverable superblock after a successful commit.</summary>
    public bool CleanupObsoleteFiles { get; init; } = true;

    /// <summary>Verify every pack-section checksum when opening. Enable for tests or explicit integrity scans.</summary>
    public bool VerifySnapshotPackChecksumsOnOpen { get; init; } = false;

    /// <summary>Maximum number of heavy repository handles retained at once.</summary>
    public int MaxResidentRepositories { get; init; } = 2;

    /// <summary>Best-effort managed-memory budget used by opportunistic repository-handle eviction.</summary>
    public long MaxResidentManagedBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Idle duration after which an unused repository handle becomes an eviction candidate.</summary>
    public TimeSpan IdleRepositoryTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Maximum decoded UTF-8 string bytes retained by each opened snapshot.</summary>
    public long DecodedStringCacheBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>Maximum materialized SymbolRecord/RelationshipRecord entries retained by each opened snapshot.</summary>
    public int MaterializedRecordCacheEntries { get; init; } = 2_048;

    /// <summary>Optional deterministic crash/fault injector used by the hard-force suite.</summary>
    public IStoreFaultInjector? FaultInjector { get; init; }

    internal NativeStoreOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(RootDirectory))
            throw new ArgumentException("RootDirectory is required.", nameof(RootDirectory));
        if (WriterLeaseTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(WriterLeaseTimeout));
        if (MaxSegmentBytes is < 1024 * 1024 or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(MaxSegmentBytes), "Use a value from 1 MiB through Int32.MaxValue bytes.");
        if (MaxManifestBytes is < 64 * 1024 or > 512L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxManifestBytes));
        if (MaxSnapshotPackBytes < MaxSegmentBytes || MaxSnapshotPackBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(MaxSnapshotPackBytes), "The contiguous pack reader currently supports files through Int32.MaxValue bytes.");
        if (MaxStringBytes is < 1024 or > 128 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxStringBytes));
        if (MaxRecordsPerSegment is < 1_000 or > 20_000_000)
            throw new ArgumentOutOfRangeException(nameof(MaxRecordsPerSegment));
        if (MaxRecordsPerSnapshot < MaxRecordsPerSegment || MaxRecordsPerSnapshot > 100_000_000)
            throw new ArgumentOutOfRangeException(nameof(MaxRecordsPerSnapshot));
        if (MaxPrefixLexemesPerToken is < 16 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(MaxPrefixLexemesPerToken));
        if (MaxResidentRepositories is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(MaxResidentRepositories));
        if (MaxResidentManagedBytes < 32L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxResidentManagedBytes));
        if (IdleRepositoryTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(IdleRepositoryTimeout));
        if (DecodedStringCacheBytes < 0 || DecodedStringCacheBytes > 2L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(DecodedStringCacheBytes));
        if (MaterializedRecordCacheEntries is < 0 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(MaterializedRecordCacheEntries));
        return this;
    }
}
