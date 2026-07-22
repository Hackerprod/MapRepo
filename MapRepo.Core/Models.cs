namespace MapRepo.Core;

public sealed record RepositoryDefinition(
    string Id,
    string RootPath,
    string? SolutionPath = null,
    IReadOnlyList<string>? EnabledModules = null,
    bool IncludeTextualEvidence = false,
    string? TsEngine = null,
    /// <summary>Extra path substrings (case-insensitive) to skip during indexing and watching, on
    /// top of the built-in PathExclusions list — e.g. a project-specific build-verification
    /// scratch folder like "verify-build" that isn't one of the universal names.</summary>
    IReadOnlyList<string>? ExcludedPaths = null,
    /// <summary>When a .sln pulls in a project outside RootPath (a sibling repo referenced via
    /// ProjectReference), symbols/edges from that project are dropped by default so one repository's
    /// index never leaks another's files. Set true only when that cross-repo visibility is wanted.</summary>
    bool AllowExternalSymbols = false);

public sealed record ModuleDescriptor(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Languages,
    string Version,
    bool SupportsSemanticAnalysis);

public sealed record AnalysisRequest(
    RepositoryDefinition Repository,
    IReadOnlyList<string> ChangedPaths,
    CancellationToken CancellationToken);

public sealed record SymbolRecord(
    string Id,
    string RepositoryId,
    string? Project,
    string FilePath,
    string Name,
    string QualifiedName,
    string Kind,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    string Signature,
    string Language,
    string ModuleId);

public sealed record RelationshipRecord(
    string Id,
    string RepositoryId,
    string SourceId,
    string TargetId,
    string Kind,
    string FilePath,
    int Line,
    int Column,
    string Confidence,
    string Language,
    string ModuleId);

public sealed record AnalysisSnapshot(
    string RepositoryId,
    string Generation,
    IReadOnlyList<SymbolRecord> Symbols,
    IReadOnlyList<RelationshipRecord> Relationships,
    IReadOnlyList<string> Diagnostics,
    DateTimeOffset CreatedAt);

public sealed record SearchResult(
    SymbolRecord Symbol,
    double Score,
    IReadOnlyList<RelationshipRecord> Relationships);

/// <summary>Truncated reflects whether a match beyond the returned page was actually found (one
/// extra row was fetched and discarded), not just "did the result count happen to equal limit" —
/// with limit=2 and exactly 2 real matches, the latter heuristic would wrongly claim truncation.</summary>
public sealed record SearchOutcome(
    IReadOnlyList<SearchResult> Items,
    bool Truncated);

public sealed record GraphResult(
    string RepositoryId,
    string Generation,
    IReadOnlyList<SymbolRecord> Nodes,
    IReadOnlyList<RelationshipRecord> Edges,
    bool Truncated);

public sealed record OverviewEntry(string Key, int Count);

public sealed record HubSymbol(SymbolRecord Symbol, int Degree);

public sealed record RepositoryOverview(
    string RepositoryId,
    string? Generation,
    int Symbols,
    int Relationships,
    IReadOnlyList<OverviewEntry> Kinds,
    IReadOnlyList<OverviewEntry> Languages,
    IReadOnlyList<OverviewEntry> Projects,
    IReadOnlyList<OverviewEntry> EdgeKinds,
    IReadOnlyList<OverviewEntry> TopFiles,
    IReadOnlyList<HubSymbol> Hubs);

public sealed record FileEntry(string FilePath, int Symbols, string Language);

public sealed record FileOutline(
    string RepositoryId,
    string FilePath,
    IReadOnlyList<SymbolRecord> Symbols,
    bool Truncated = false);

public sealed record SymbolDetail(
    SymbolRecord Symbol,
    IReadOnlyList<RelationshipRecord> Outgoing,
    IReadOnlyList<RelationshipRecord> Incoming,
    IReadOnlyList<SymbolRecord> Neighbors);

public sealed record SourceSlice(
    string RepositoryId,
    string FilePath,
    int StartLine,
    int EndLine,
    int TotalLines,
    string Content,
    bool Truncated);

public sealed record SearchFilter(
    string? Kind = null,
    string? PathContains = null,
    bool IncludeTextual = true);

public interface IRepositoryLanguageModule
{
    ModuleDescriptor Descriptor { get; }
    bool CanAnalyze(string filePath);
    Task<AnalysisSnapshot> AnalyzeAsync(AnalysisRequest request);
}

/// <summary>Result of analyzing only the changed files: rows for exactly those files, nothing else.</summary>
public sealed record FileAnalysisDelta(
    IReadOnlyList<string> FilePaths,
    IReadOnlyList<SymbolRecord> Symbols,
    IReadOnlyList<RelationshipRecord> Relationships,
    IReadOnlyList<string> Diagnostics);

/// <summary>Optional module capability: reanalyze only the changed documents against cached state.
/// Returning null means the module cannot do it for this change set (new files, no cached workspace) and a full run is required.</summary>
public interface IIncrementalAnalyzer
{
    Task<FileAnalysisDelta?> AnalyzeFilesAsync(AnalysisRequest request);
}

/// <summary>Optional module capability: release per-repository cached state (workspaces, caches).</summary>
public interface IRepositoryLifecycle
{
    void ReleaseRepository(string repositoryId);
}

public interface IRepositoryStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    /// <summary>Replaces stored records. When <paramref name="moduleIds"/> is provided only those modules' rows are replaced; other modules' data is preserved.</summary>
    Task ReplaceAsync(AnalysisSnapshot snapshot, IReadOnlyList<string>? moduleIds = null, CancellationToken cancellationToken = default);
    /// <summary>Replaces only the rows of the given files for one module — the incremental path.</summary>
    Task ReplaceFilesAsync(string repositoryId, string moduleId, IReadOnlyList<string> filePaths,
        IReadOnlyList<SymbolRecord> symbols, IReadOnlyList<RelationshipRecord> relationships,
        string generation, DateTimeOffset indexedAt, CancellationToken cancellationToken = default);
    Task<SearchOutcome> SearchAsync(string repositoryId, string query, int limit, SearchFilter? filter = null, CancellationToken cancellationToken = default);
    Task<GraphResult> GraphAsync(string repositoryId, string symbolId, int depth, int limit, IReadOnlyList<string>? edgeKinds = null, CancellationToken cancellationToken = default);
    Task<RepositoryStatus> StatusAsync(string repositoryId, CancellationToken cancellationToken = default);
    Task<RepositoryOverview> OverviewAsync(string repositoryId, bool includeGenerated = false, CancellationToken cancellationToken = default);
    Task<FileOutline> OutlineAsync(string repositoryId, string filePath, int maxSymbols = 500, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileEntry>> FilesAsync(string repositoryId, string? contains, int limit, CancellationToken cancellationToken = default);
    Task<SymbolDetail?> SymbolAsync(string repositoryId, string symbolId, int limit, CancellationToken cancellationToken = default);
    Task DeleteAsync(string repositoryId, CancellationToken cancellationToken = default);
    /// <summary>Removes every symbol/relationship whose file path contains <paramref name="pathPattern"/>
    /// (case-insensitive), across all modules, without re-running any analyzer. Returns the number
    /// of symbols removed. Used to retroactively apply a new exclude pattern to an already-indexed
    /// repository — cheap and immediate, unlike a full reindex.</summary>
    Task<int> PurgePathAsync(string repositoryId, string pathPattern, CancellationToken cancellationToken = default);
    /// <summary>The on-disk directory holding this repository's index files — for diagnostics, so
    /// a disk I/O error names the actual file to go look at instead of just "disk I/O error".</summary>
    string StoragePath(string repositoryId);
}

public sealed record RepositoryStatus(
    string RepositoryId,
    string? Generation,
    int Symbols,
    int Relationships,
    DateTimeOffset? LastIndexedAt,
    bool WatcherActive,
    bool Indexing,
    /// <summary>Actual problems: MSBuild/analysis failures, config issues — things an agent should
    /// treat as warnings. Purely informational index stats (file/symbol/edge counts) belong in
    /// <see cref="IndexSummary"/> instead, so this list isn't misread as "something is wrong".</summary>
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string>? IndexSummary = null);

public sealed record RepositorySummary(
    RepositoryDefinition Definition,
    RepositoryStatus Status);
