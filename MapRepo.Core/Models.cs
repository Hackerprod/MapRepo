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
    IReadOnlyList<string>? ExcludedPaths = null);

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
    IReadOnlyList<SymbolRecord> Symbols);

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
    Task<IReadOnlyList<SearchResult>> SearchAsync(string repositoryId, string query, int limit, SearchFilter? filter = null, CancellationToken cancellationToken = default);
    Task<GraphResult> GraphAsync(string repositoryId, string symbolId, int depth, int limit, IReadOnlyList<string>? edgeKinds = null, CancellationToken cancellationToken = default);
    Task<RepositoryStatus> StatusAsync(string repositoryId, CancellationToken cancellationToken = default);
    Task<RepositoryOverview> OverviewAsync(string repositoryId, CancellationToken cancellationToken = default);
    Task<FileOutline> OutlineAsync(string repositoryId, string filePath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileEntry>> FilesAsync(string repositoryId, string? contains, int limit, CancellationToken cancellationToken = default);
    Task<SymbolDetail?> SymbolAsync(string repositoryId, string symbolId, int limit, CancellationToken cancellationToken = default);
    Task DeleteAsync(string repositoryId, CancellationToken cancellationToken = default);
    /// <summary>Removes every symbol/relationship whose file path contains <paramref name="pathPattern"/>
    /// (case-insensitive), across all modules, without re-running any analyzer. Returns the number
    /// of symbols removed. Used to retroactively apply a new exclude pattern to an already-indexed
    /// repository — cheap and immediate, unlike a full reindex.</summary>
    Task<int> PurgePathAsync(string repositoryId, string pathPattern, CancellationToken cancellationToken = default);
}

public sealed record RepositoryStatus(
    string RepositoryId,
    string? Generation,
    int Symbols,
    int Relationships,
    DateTimeOffset? LastIndexedAt,
    bool WatcherActive,
    bool Indexing,
    IReadOnlyList<string> Diagnostics);

public sealed record RepositorySummary(
    RepositoryDefinition Definition,
    RepositoryStatus Status);
