using MapRepo.Core;

namespace MapRepo.NativeStore.Projection;

/// <summary>Storage-level views that avoid materializing fields the caller will discard.</summary>
public enum NativeProjectionKind
{
    /// <summary>ID, name, qualified name, kind, file and line; optimized for initial orientation.</summary>
    Orientation,

    /// <summary>The existing compact MCP symbol contract, including project/signature/end line.</summary>
    Compact,

    /// <summary>All symbol fields currently exposed by the MapRepo wire contract.</summary>
    Full,

    /// <summary>ID, name and kind only; optimized for graph node labels.</summary>
    GraphOnly
}

public sealed record ProjectionBudget(int MaxItems = 100, int MaxEstimatedTokens = 4_000);

public sealed record SymbolProjectionRequest(
    string RepositoryId,
    string Query,
    NativeProjectionKind Projection = NativeProjectionKind.Orientation,
    ProjectionBudget? Budget = null,
    SearchFilter? Filter = null);

public sealed record ProjectedSymbol(
    string Id,
    string Name,
    string? QualifiedName,
    string? Kind,
    string? Project,
    string? FilePath,
    string? Signature,
    int? StartLine,
    int? EndLine,
    string? Language,
    string? ModuleId,
    int? StartColumn,
    int? EndColumn,
    double Score,
    int EstimatedTokens);

public sealed record SymbolProjectionResult(
    IReadOnlyList<ProjectedSymbol> Items,
    int Returned,
    bool HasMore,
    int EstimatedTokens,
    NativeProjectionKind Projection);


public sealed record FileProjectionResult(
    IReadOnlyList<FileEntry> Items,
    bool HasMore);

public sealed record ProjectedOutlineSymbol(
    string? Id,
    string Name,
    string? QualifiedName,
    string Kind,
    string? Project,
    int StartLine,
    int? EndLine,
    string? Signature);

public sealed record FileOutlineProjectionResult(
    string RepositoryId,
    string FilePath,
    IReadOnlyList<ProjectedOutlineSymbol> Symbols,
    bool HasMore,
    bool Compact);

/// <summary>One logical graph edge with bounded occurrence evidence.</summary>
public sealed record ProjectedEdgeGroup(
    string SourceId,
    string TargetId,
    string Kind,
    string FilePath,
    IReadOnlyList<int> Lines,
    int OccurrenceCount);

public sealed record ProjectedSymbolDetailResult(
    ProjectedSymbol Symbol,
    IReadOnlyList<ProjectedEdgeGroup> Outgoing,
    bool OutgoingTruncated,
    IReadOnlyList<ProjectedEdgeGroup> Incoming,
    bool IncomingTruncated,
    IReadOnlyList<ProjectedSymbol> Neighbors,
    bool NeighborsTruncated);

public sealed record ProjectedGraphResult(
    string RepositoryId,
    string Generation,
    IReadOnlyList<ProjectedSymbol> Nodes,
    IReadOnlyList<ProjectedEdgeGroup> Edges,
    bool Truncated);

public interface IProjectedRepositoryStore
{
    Task<SymbolProjectionResult> ProjectSymbolsAsync(
        SymbolProjectionRequest request,
        CancellationToken cancellationToken = default);

    Task<FileProjectionResult> ProjectFilesAsync(
        string repositoryId,
        string? contains,
        int limit,
        CancellationToken cancellationToken = default);

    Task<FileOutlineProjectionResult> ProjectOutlineAsync(
        string repositoryId,
        string filePath,
        int maxSymbols,
        bool compact,
        CancellationToken cancellationToken = default);

    Task<ProjectedSymbolDetailResult?> ProjectSymbolDetailAsync(
        string repositoryId,
        string symbolId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<ProjectedGraphResult> ProjectGraphAsync(
        string repositoryId,
        string symbolId,
        int depth,
        int limit,
        IReadOnlyList<string>? edgeKinds = null,
        CancellationToken cancellationToken = default);
}
