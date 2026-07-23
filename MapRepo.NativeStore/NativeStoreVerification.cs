namespace MapRepo.NativeStore;

/// <summary>Result of a full checksum, manifest, segment, identity, search-index, and graph-index verification pass.</summary>
public sealed record NativeStoreVerificationResult(
    string RepositoryId,
    bool IsValid,
    long Sequence,
    string? Generation,
    int ActiveSegments,
    int Symbols,
    int Relationships,
    IReadOnlyList<string> Notes,
    int ResolvedRelationships = 0);
