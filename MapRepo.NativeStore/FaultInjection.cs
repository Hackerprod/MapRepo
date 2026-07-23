namespace MapRepo.NativeStore;

/// <summary>Durability boundaries exposed for deterministic process-crash testing.</summary>
public enum StoreFaultPoint
{
    AfterSegmentBytesWritten,
    AfterSegmentDurable,
    AfterSegmentPublished,
    AfterSnapshotBytesWritten,
    AfterSnapshotDurable,
    AfterSnapshotPublished,
    AfterManifestBytesWritten,
    AfterManifestDurable,
    AfterManifestPublished,
    BeforeSuperblockPublish,
    AfterSuperblockBytesWritten,
    AfterSuperblockDurable,
    AfterSuperblockPublished,
    BeforeInMemorySnapshotPublish,
    AfterInMemorySnapshotPublish
}

public readonly record struct StoreFaultContext(
    string RepositoryId,
    long Sequence,
    string? FilePath = null);

public interface IStoreFaultInjector
{
    void Hit(StoreFaultPoint point, StoreFaultContext context);
}

public sealed class DelegateStoreFaultInjector(Action<StoreFaultPoint, StoreFaultContext> callback)
    : IStoreFaultInjector
{
    public void Hit(StoreFaultPoint point, StoreFaultContext context) => callback(point, context);
}
