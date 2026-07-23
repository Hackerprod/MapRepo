namespace MapRepo.NativeStore;

/// <summary>Best-effort process-local residency metrics for diagnostics and capacity planning.</summary>
public sealed record NativeStoreRuntimeStats(
    int ResidentRepositories,
    int ActiveOperations,
    long EstimatedManagedBytes,
    long BackingPackBytes,
    long DecodedStringCacheBytes,
    int MaterializedRecordCacheEntries,
    int MemoryMappedRepositories,
    int CompactManagedRepositories,
    long ManagedHeapBytes,
    long ProcessWorkingSetBytes,
    long ProcessPrivateMemoryBytes);
