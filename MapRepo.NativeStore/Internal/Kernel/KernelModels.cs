using System.Collections.Immutable;
using MapRepo.Core;

namespace MapRepo.NativeStore.Internal.Kernel;

internal readonly record struct FileModuleKey(string ModuleId, string FilePath) : IComparable<FileModuleKey>
{
    public static FileModuleKey Create(string moduleId, string filePath)
    {
        if (string.IsNullOrWhiteSpace(moduleId)) throw new ArgumentException("Module ID is required.", nameof(moduleId));
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path is required.", nameof(filePath));
        return new FileModuleKey(moduleId, NormalizePath(filePath));
    }

    public int CompareTo(FileModuleKey other)
    {
        var module = StringComparer.Ordinal.Compare(ModuleId, other.ModuleId);
        return module != 0 ? module : StringComparer.Ordinal.Compare(FilePath, other.FilePath);
    }

    public static string NormalizePath(string path) => path.Replace('\\', '/');
}

internal sealed record FileSegmentData(
    FileModuleKey Key,
    ImmutableArray<SymbolRecord> Symbols,
    ImmutableArray<RelationshipRecord> Relationships)
{
    public bool IsEmpty => Symbols.IsDefaultOrEmpty && Relationships.IsDefaultOrEmpty;
}

internal sealed record SegmentDescriptor(
    FileModuleKey Key,
    string FileName,
    long Sequence,
    uint Checksum,
    long Length,
    int SymbolCount,
    int RelationshipCount);

internal sealed record SnapshotDescriptor(
    string FileName,
    long Sequence,
    uint RootChecksum,
    long Length,
    int SymbolCount,
    int RelationshipCount,
    int ResolvedRelationshipCount,
    int FileCount);

internal sealed record StoreManifest(
    long Sequence,
    string RepositoryId,
    string Generation,
    DateTimeOffset IndexedAt,
    ImmutableArray<string> Diagnostics,
    ImmutableDictionary<FileModuleKey, SegmentDescriptor> ActiveSegments,
    SnapshotDescriptor? Snapshot)
{
    public static StoreManifest Empty(string repositoryId) => new(
        0,
        repositoryId,
        "unknown",
        DateTimeOffset.UnixEpoch,
        [],
        ImmutableDictionary<FileModuleKey, SegmentDescriptor>.Empty,
        null);
}

internal sealed record SuperblockPointer(
    long Sequence,
    string ManifestFileName,
    uint ManifestChecksum,
    long ManifestLength,
    DateTimeOffset CreatedAt);

internal sealed record KernelMetadataResult(
    StoreManifest Manifest,
    IReadOnlyList<string> RecoveryNotes);

internal sealed record KernelLoadResult(
    StoreManifest Manifest,
    ImmutableDictionary<FileModuleKey, FileSegmentData> Segments,
    IReadOnlyList<string> RecoveryNotes);

internal sealed record KernelCommitResult(
    StoreManifest Manifest,
    string? SnapshotPath);
