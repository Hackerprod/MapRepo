namespace MapRepo.NativeStore.Internal.Packing;

[Flags]
internal enum LexemeField : ushort
{
    None = 0,
    Name = 1,
    QualifiedName = 2,
    Token = 4
}

internal enum PackSectionKind
{
    StringOffsets = 1,
    StringBytes = 2,
    Files = 3,
    Symbols = 4,
    Relationships = 5,
    SymbolLookup = 6,
    RelationshipLookup = 7,
    Lexemes = 8,
    LexemePostings = 9,
    Trigrams = 10,
    TrigramPostings = 11,
    FileTrigrams = 12,
    FileTrigramPostings = 13,
    OutlineSymbols = 14,
    OutgoingOffsets = 15,
    OutgoingEdges = 16,
    IncomingOffsets = 17,
    IncomingEdges = 18,
    Overview = 19
}

internal readonly record struct PackSectionDescriptor(
    PackSectionKind Kind,
    long Offset,
    long Length,
    int Count,
    int RecordSize,
    uint Checksum);

internal sealed record SnapshotPackHeader(
    long Sequence,
    long Length,
    uint RootChecksum,
    int SymbolCount,
    int RelationshipCount,
    int ResolvedRelationshipCount,
    int FileCount,
    int StringCount,
    long TotalDocumentLength,
    DateTimeOffset IndexedAt,
    IReadOnlyDictionary<PackSectionKind, PackSectionDescriptor> Sections);

internal sealed record SnapshotPackBuildResult(
    string Path,
    long Sequence,
    uint RootChecksum,
    long Length,
    int SymbolCount,
    int RelationshipCount,
    int ResolvedRelationshipCount,
    int FileCount);

internal static class PackLayout
{
    public const int HeaderSize = 4096;
    public const int DirectoryEntrySize = 40;
    public const int FileRowSize = 32;
    public const int SymbolRowSize = 64;
    public const int RelationshipRowSize = 56;
    public const int LookupRowSize = 16;
    public const int LexemeRowSize = 16;
    public const int LexemePostingSize = 8;
    public const int TrigramRowSize = 16;
}

internal interface IRepositoryRecordSource
{
    IEnumerable<MapRepo.Core.SymbolRecord> EnumerateSymbols(CancellationToken cancellationToken);
    IEnumerable<MapRepo.Core.RelationshipRecord> EnumerateRelationships(CancellationToken cancellationToken);
}

internal sealed class SegmentRecordSource : IRepositoryRecordSource
{
    private readonly IReadOnlyDictionary<MapRepo.NativeStore.Internal.Kernel.FileModuleKey, MapRepo.NativeStore.Internal.Kernel.FileSegmentData> _segments;

    public SegmentRecordSource(
        IReadOnlyDictionary<MapRepo.NativeStore.Internal.Kernel.FileModuleKey, MapRepo.NativeStore.Internal.Kernel.FileSegmentData> segments) =>
        _segments = segments;

    public IEnumerable<MapRepo.Core.SymbolRecord> EnumerateSymbols(CancellationToken cancellationToken)
    {
        foreach (var pair in _segments.OrderBy(value => value.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var value in pair.Value.Symbols) yield return value;
        }
    }

    public IEnumerable<MapRepo.Core.RelationshipRecord> EnumerateRelationships(CancellationToken cancellationToken)
    {
        foreach (var pair in _segments.OrderBy(value => value.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var value in pair.Value.Relationships) yield return value;
        }
    }
}
