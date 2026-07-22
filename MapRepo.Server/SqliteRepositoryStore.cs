using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MapRepo.Core;

namespace MapRepo.Server;

/// <summary>
/// One SQLite database per repository, stored in its own directory:
/// data-v4/&lt;slug&gt;__&lt;hash&gt;/index.db. Repositories never share storage.
/// </summary>
public sealed class SqliteRepositoryStore : IRepositoryStore
{
    private readonly string _root;
    // Overview aggregations are only worth recomputing when the index generation moves.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? Generation, RepositoryOverview Value)> _overviewCache = new(StringComparer.OrdinalIgnoreCase);
    // Marks a repository as having a populated symbols_fts5 at least once this process — the
    // backfill migration only ever matters for a database created before FTS existed.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _ftsBackfilled = new(StringComparer.OrdinalIgnoreCase);
    // list_files' `contains` filter is a leading-wildcard LIKE — can't use an index, so it's a full
    // table scan every time. OS page-cache warmth after that first scan isn't durable (build/dev
    // activity on the same machine evicts it under memory pressure), so cache the actual result
    // per (repo, generation, contains, limit) instead of hoping the disk stays warm.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? Generation, IReadOnlyList<FileEntry> Value)> _filesCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<SqliteRepositoryStore> _logger;

    public SqliteRepositoryStore(IHostEnvironment environment, ILogger<SqliteRepositoryStore> logger)
    {
        _root = Path.Combine(environment.ContentRootPath, "data-v4");
        Directory.CreateDirectory(_root);
        _logger = logger;
    }

    public string RootDirectory => _root;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task ReplaceAsync(AnalysisSnapshot snapshot, IReadOnlyList<string>? moduleIds = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(snapshot.RepositoryId, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (moduleIds is { Count: > 0 })
        {
            var marks = string.Join(',', moduleIds.Select((_, i) => $"$m{i}"));
            var parameters = moduleIds.Select((id, i) => ($"$m{i}", (object?)id)).ToArray();
            await ExecuteAsync(connection, transaction, $"DELETE FROM relationships WHERE module_id IN ({marks})", parameters, cancellationToken);
            await ExecuteAsync(connection, transaction, $"DELETE FROM symbols WHERE module_id IN ({marks})", parameters, cancellationToken);
        }
        else
        {
            await ExecuteAsync(connection, transaction, "DELETE FROM relationships", [], cancellationToken);
            await ExecuteAsync(connection, transaction, "DELETE FROM symbols", [], cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO symbols(id,repository_id,project,file_path,name,qualified_name,kind,start_line,start_column,end_line,end_column,signature,language,module_id)
                VALUES($id,$repo,$project,$file,$name,$qualified,$kind,$sl,$sc,$el,$ec,$signature,$language,$module)
                """;
            var parameters = Prepare(command, ["$id", "$repo", "$project", "$file", "$name", "$qualified", "$kind", "$sl", "$sc", "$el", "$ec", "$signature", "$language", "$module"]);
            foreach (var s in snapshot.Symbols)
            {
                Bind(parameters, s.Id, s.RepositoryId, s.Project, s.FilePath, s.Name, s.QualifiedName, s.Kind,
                    s.StartLine, s.StartColumn, s.EndLine, s.EndColumn, s.Signature, s.Language, s.ModuleId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO relationships(id,repository_id,source_id,target_id,kind,file_path,line,column_number,confidence,language,module_id)
                VALUES($id,$repo,$source,$target,$kind,$file,$line,$column,$confidence,$language,$module)
                """;
            var parameters = Prepare(command, ["$id", "$repo", "$source", "$target", "$kind", "$file", "$line", "$column", "$confidence", "$language", "$module"]);
            foreach (var e in snapshot.Relationships)
            {
                Bind(parameters, e.Id, e.RepositoryId, e.SourceId, e.TargetId, e.Kind, e.FilePath,
                    e.Line, e.Column, e.Confidence, e.Language, e.ModuleId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        if (moduleIds is { Count: > 0 })
        {
            var marks = string.Join(',', moduleIds.Select((_, i) => $"$n{i}"));
            var parameters = moduleIds.Select((id, i) => ($"$n{i}", (object?)id)).ToArray();
            await ExecuteAsync(connection, transaction, $"DELETE FROM symbols_fts5 WHERE module_id IN ({marks})", parameters, cancellationToken);
        }
        else await ExecuteAsync(connection, transaction, "DELETE FROM symbols_fts5", [], cancellationToken);
        await InsertFtsAsync(connection, transaction, snapshot.Symbols, cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO meta(repository_id,generation,indexed_at,diagnostics) VALUES($repo,$generation,$indexed,$diagnostics) ON CONFLICT(repository_id) DO UPDATE SET generation=excluded.generation,indexed_at=excluded.indexed_at,diagnostics=excluded.diagnostics";
            Add(command, ("$repo", snapshot.RepositoryId), ("$generation", snapshot.Generation),
                ("$indexed", snapshot.CreatedAt.ToString("O")), ("$diagnostics", string.Join("\n", snapshot.Diagnostics)));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReplaceFilesAsync(string repositoryId, string moduleId, IReadOnlyList<string> filePaths,
        IReadOnlyList<SymbolRecord> symbols, IReadOnlyList<RelationshipRecord> relationships,
        string generation, DateTimeOffset indexedAt, CancellationToken cancellationToken = default)
    {
        if (filePaths.Count == 0) return;
        await using var connection = await OpenAsync(repositoryId, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var fileMarks = string.Join(',', filePaths.Select((_, i) => $"$f{i}"));
        var fileParameters = filePaths.Select((path, i) => ($"$f{i}", (object?)path)).Append(("$module", (object?)moduleId)).ToArray();
        await ExecuteAsync(connection, transaction, $"DELETE FROM relationships WHERE module_id=$module AND file_path IN ({fileMarks})", fileParameters, cancellationToken);
        await ExecuteAsync(connection, transaction, $"DELETE FROM symbols WHERE module_id=$module AND file_path IN ({fileMarks})", fileParameters, cancellationToken);
        await ExecuteAsync(connection, transaction, $"DELETE FROM symbols_fts5 WHERE module_id=$module AND file_path IN ({fileMarks})", fileParameters, cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR REPLACE INTO symbols(id,repository_id,project,file_path,name,qualified_name,kind,start_line,start_column,end_line,end_column,signature,language,module_id)
                VALUES($id,$repo,$project,$file,$name,$qualified,$kind,$sl,$sc,$el,$ec,$signature,$language,$module)
                """;
            var parameters = Prepare(command, ["$id", "$repo", "$project", "$file", "$name", "$qualified", "$kind", "$sl", "$sc", "$el", "$ec", "$signature", "$language", "$module"]);
            foreach (var s in symbols)
            {
                Bind(parameters, s.Id, s.RepositoryId, s.Project, s.FilePath, s.Name, s.QualifiedName, s.Kind,
                    s.StartLine, s.StartColumn, s.EndLine, s.EndColumn, s.Signature, s.Language, s.ModuleId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        // Keep only edges whose endpoints exist — either inserted just now or already stored for other files.
        var known = symbols.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var unknown = relationships.SelectMany(e => new[] { e.SourceId, e.TargetId })
            .Where(id => !known.Contains(id)).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var chunk in unknown.Chunk(400))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var marks = new List<string>();
            for (var i = 0; i < chunk.Length; i++) { marks.Add($"$id{i}"); command.Parameters.AddWithValue($"$id{i}", chunk[i]); }
            command.CommandText = $"SELECT id FROM symbols WHERE id IN ({string.Join(',', marks)})";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) known.Add(reader.GetString(0));
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR REPLACE INTO relationships(id,repository_id,source_id,target_id,kind,file_path,line,column_number,confidence,language,module_id)
                VALUES($id,$repo,$source,$target,$kind,$file,$line,$column,$confidence,$language,$module)
                """;
            var parameters = Prepare(command, ["$id", "$repo", "$source", "$target", "$kind", "$file", "$line", "$column", "$confidence", "$language", "$module"]);
            foreach (var e in relationships.Where(e => known.Contains(e.SourceId) && known.Contains(e.TargetId)))
            {
                Bind(parameters, e.Id, e.RepositoryId, e.SourceId, e.TargetId, e.Kind, e.FilePath,
                    e.Line, e.Column, e.Confidence, e.Language, e.ModuleId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await InsertFtsAsync(connection, transaction, symbols, cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE meta SET generation=$generation, indexed_at=$indexed WHERE repository_id=$repo";
            Add(command, ("$repo", repositoryId), ("$generation", generation), ("$indexed", indexedAt.ToString("O")));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<SearchOutcome> SearchAsync(string repositoryId, string query, int limit, SearchFilter? filter = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(repositoryId, cancellationToken);
        var symbols = new List<(SymbolRecord Symbol, double Score)>();
        var boundedLimit = Math.Clamp(limit, 1, 200);
        // Fetch one extra row past the requested limit so truncation can be answered from a fact
        // (an (limit+1)th row actually exists) instead of a coincidence (returned count == limit,
        // which is also exactly what happens when limit matches the true total with nothing hidden).
        var fetchLimit = boundedLimit + 1;
        var extra = new StringBuilder();
        var extraQualified = new StringBuilder(); // same predicates with columns qualified as s.* for the FTS join
        var extraParameters = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(filter?.Kind))
        {
            extra.Append(" AND kind=$kind COLLATE NOCASE"); extraQualified.Append(" AND s.kind=$kind COLLATE NOCASE");
            extraParameters.Add(("$kind", filter!.Kind));
        }
        if (!string.IsNullOrWhiteSpace(filter?.PathContains))
        {
            extra.Append(" AND lower(file_path) LIKE '%'||lower($path)||'%' ESCAPE '\\'"); extraQualified.Append(" AND lower(s.file_path) LIKE '%'||lower($path)||'%' ESCAPE '\\'");
            extraParameters.Add(("$path", EscapeLikePattern(filter!.PathContains)));
        }
        if (filter is { IncludeTextual: false }) { extra.Append(" AND kind<>'textual-evidence'"); extraQualified.Append(" AND s.kind<>'textual-evidence'"); }

        await using (var exact = connection.CreateCommand())
        {
            exact.CommandText = $"""
                SELECT {SymbolColumns},100.0
                FROM symbols WHERE (name=$query COLLATE NOCASE OR qualified_name=$query COLLATE NOCASE){extra}
                ORDER BY length(name), file_path LIMIT $limit
                """;
            Add(exact, ("$query", query.Trim()), ("$limit", fetchLimit));
            Add(exact, extraParameters.ToArray());
            await using var reader = await exact.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) symbols.Add((ReadSymbol(reader), reader.GetDouble(14)));
        }
        if (symbols.Count == 0 && TryBuildFtsQuery(query) is { } ftsQuery)
        {
            // FTS5 prefix match: index-backed, scales to monorepos where LIKE '%x%' would scan the table.
            // The backfill (a BEGIN IMMEDIATE write transaction) is only ever needed once per database
            // — for one upgraded from a schema without the FTS table — so it runs at most once per
            // repository per process lifetime instead of on every search.
            if (_ftsBackfilled.TryAdd(repositoryId, true)) await BackfillFtsAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {SymbolColumnsQualified}, 55.0 - bm25(symbols_fts5, 0.0, 0.0, 10.0, 4.0, 1.0) AS score
                FROM symbols_fts5 JOIN symbols s ON s.id = symbols_fts5.symbol_id
                WHERE symbols_fts5 MATCH $match{extraQualified}
                ORDER BY bm25(symbols_fts5, 0.0, 0.0, 10.0, 4.0, 1.0), length(s.name) LIMIT $limit
                """;
            Add(command, ("$match", ftsQuery), ("$limit", fetchLimit));
            Add(command, extraParameters.ToArray());
            try
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) symbols.Add((ReadSymbol(reader), reader.GetDouble(14)));
            }
            catch (SqliteException) { /* malformed MATCH input: fall through to LIKE */ }
        }
        if (symbols.Count == 0)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {SymbolColumns},
                       CASE WHEN lower(name) LIKE lower($query)||'%' ESCAPE '\' THEN 60.0
                            WHEN lower(qualified_name) LIKE '%'||lower($query)||'%' ESCAPE '\' THEN 50.0 ELSE 10.0 END AS score
                FROM symbols WHERE (name LIKE '%'||$query||'%' ESCAPE '\' OR qualified_name LIKE '%'||$query||'%' ESCAPE '\' OR file_path LIKE '%'||$query||'%' ESCAPE '\'){extra}
                ORDER BY score DESC, length(name), file_path LIMIT $limit
                """;
            Add(command, ("$query", EscapeLikePattern(query)), ("$limit", fetchLimit));
            Add(command, extraParameters.ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) symbols.Add((ReadSymbol(reader), reader.GetDouble(14)));
        }
        var truncated = symbols.Count > boundedLimit;
        if (truncated) symbols.RemoveRange(boundedLimit, symbols.Count - boundedLimit);
        var relationshipsBySymbol = await RelationshipsForManyAsync(connection, symbols.Select(s => s.Symbol.Id), cancellationToken, 24);
        var items = symbols
            .Select(item => new SearchResult(item.Symbol, item.Score,
                relationshipsBySymbol.TryGetValue(item.Symbol.Id, out var relationships) ? relationships : []))
            .ToArray();
        return new SearchOutcome(items, truncated);
    }

    public async Task<GraphResult> GraphAsync(string repositoryId, string symbolId, int depth, int limit, IReadOnlyList<string>? edgeKinds = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(repositoryId, cancellationToken);
        var nodeIds = new HashSet<string>(StringComparer.Ordinal) { symbolId };
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new HashSet<string>(nodeIds);
        var edges = new List<RelationshipRecord>();
        for (var level = 0; level < Math.Clamp(depth, 0, 5) && frontier.Count > 0 && nodeIds.Count < limit; level++)
        {
            var next = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in frontier)
            {
                foreach (var edge in await RelationshipsForAsync(connection, id, cancellationToken, 500, edgeKinds))
                {
                    if (!edgeIds.Add(edge.Id)) continue;
                    edges.Add(edge);
                    if (nodeIds.Add(edge.SourceId)) next.Add(edge.SourceId);
                    if (nodeIds.Add(edge.TargetId)) next.Add(edge.TargetId);
                    if (nodeIds.Count >= limit) break;
                }
                if (nodeIds.Count >= limit) break;
            }
            frontier = next;
        }
        var nodes = await SymbolsByIdsAsync(connection, nodeIds, cancellationToken);
        var generation = await GenerationAsync(connection, repositoryId, cancellationToken);
        // Edges pointing outside the node budget are unusable by consumers — drop them.
        var resolved = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var bounded = edges.Where(e => resolved.Contains(e.SourceId) && resolved.Contains(e.TargetId)).ToArray();
        return new GraphResult(repositoryId, generation ?? "unknown", nodes, bounded, nodeIds.Count >= limit);
    }

    public async Task<RepositoryStatus> StatusAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(repositoryId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT generation,indexed_at,diagnostics,(SELECT count(*) FROM symbols),(SELECT count(*) FROM relationships) FROM meta WHERE repository_id=$repo";
        Add(command, ("$repo", repositoryId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new RepositoryStatus(repositoryId, null, 0, 0, null, false, false, []);
        var raw = reader.IsDBNull(2) ? [] : reader.GetString(2).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var (diagnostics, indexSummary) = SplitDiagnostics(raw);
        return new RepositoryStatus(repositoryId, reader.GetString(0), reader.GetInt32(3), reader.GetInt32(4),
            DateTimeOffset.TryParse(reader.GetString(1), out var date) ? date : null, false, false, diagnostics, indexSummary);
    }

    // A module's own informational summary line ("ts-semantic (full): N files, M symbols, K edges",
    // "tsconfig: <path>") is not a problem — it always fires, success or not — but it was stored in
    // the same flat diagnostics list as real MSBuild/analysis failures, so agents kept reading it as
    // a warning. Split by recognizable prefix instead of touching every module's analysis path.
    private static readonly string[] InformationalDiagnosticPrefixes = ["ts-semantic (full):", "ts-semantic (incremental):", "tsconfig:"];

    public static (IReadOnlyList<string> Diagnostics, IReadOnlyList<string>? IndexSummary) SplitDiagnostics(IReadOnlyList<string> raw)
    {
        var diagnostics = new List<string>();
        var summary = new List<string>();
        foreach (var line in raw)
            (InformationalDiagnosticPrefixes.Any(line.StartsWith) ? summary : diagnostics).Add(line);
        return (diagnostics, summary.Count > 0 ? summary : null);
    }

    // Path-pattern heuristic for tool-generated source (designer files, protobuf/gRPC stubs,
    // assembly metadata, obj/ build intermediates) that would otherwise dominate topFiles/hubs
    // with noise nobody hand-wrote. Best-effort by design: it can't see file content, only the path.
    private const string GeneratedFileFilterSql =
        " AND file_path NOT LIKE '%.designer.cs' AND file_path NOT LIKE '%.g.cs' AND file_path NOT LIKE '%.g.i.cs'" +
        " AND file_path NOT LIKE '%.pb.cs' AND file_path NOT LIKE '%AssemblyInfo.cs' AND file_path NOT LIKE '%/obj/%'" +
        " AND lower(file_path) NOT LIKE '%/generated/%'";

    public async Task<RepositoryOverview> OverviewAsync(string repositoryId, bool includeGenerated = false, CancellationToken cancellationToken = default)
    {
        var status = await StatusAsync(repositoryId, cancellationToken);
        var cacheKey = $"{repositoryId}|{includeGenerated}";
        if (_overviewCache.TryGetValue(cacheKey, out var cached) && cached.Generation == status.Generation && status.Generation is not null)
            return cached.Value;
        await using var connection = await OpenAsync(repositoryId, cancellationToken);
        var generatedFilter = includeGenerated ? "" : GeneratedFileFilterSql;

        async Task<IReadOnlyList<OverviewEntry>> GroupAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var entries = new List<OverviewEntry>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                entries.Add(new OverviewEntry(reader.IsDBNull(0) ? "(none)" : reader.GetString(0), reader.GetInt32(1)));
            return entries;
        }

        // kinds/languages/projects are cheap unfiltered GROUP BYs (as before includeGenerated
        // existed) — the reported noise was specifically topFiles/hubs being dominated by
        // generated code, not these aggregate counts, so only those two pay for the unindexed
        // NOT LIKE chain (a leading-wildcard LIKE can't use an index either way).
        var kinds = await GroupAsync("SELECT kind,count(*) FROM symbols GROUP BY kind ORDER BY count(*) DESC LIMIT 30");
        var languages = await GroupAsync("SELECT language,count(*) FROM symbols GROUP BY language ORDER BY count(*) DESC LIMIT 12");
        var projects = await GroupAsync("SELECT project,count(*) FROM symbols GROUP BY project ORDER BY count(*) DESC LIMIT 30");
        var edgeKinds = await GroupAsync("SELECT kind,count(*) FROM relationships GROUP BY kind ORDER BY count(*) DESC LIMIT 12");
        var topFiles = await GroupAsync($"SELECT file_path,count(*) FROM symbols WHERE kind<>'textual-evidence'{generatedFilter} GROUP BY file_path ORDER BY count(*) DESC LIMIT 20");

        var hubs = new List<HubSymbol>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT symbol_id, SUM(cnt) AS degree FROM (
                    SELECT source_id AS symbol_id, count(*) AS cnt FROM relationships GROUP BY source_id
                    UNION ALL
                    SELECT target_id, count(*) FROM relationships GROUP BY target_id
                ) GROUP BY symbol_id ORDER BY degree DESC LIMIT 80
                """;
            var degrees = new List<(string Id, int Degree)>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) degrees.Add((reader.GetString(0), reader.GetInt32(1)));
            var records = await SymbolsByIdsAsync(connection, degrees.Select(d => d.Id), cancellationToken);
            var byId = records.ToDictionary(r => r.Id, StringComparer.Ordinal);
            hubs.AddRange(degrees
                .Where(d => byId.ContainsKey(d.Id) && byId[d.Id].Kind != "textual-evidence"
                    && (includeGenerated || !IsGeneratedPath(byId[d.Id].FilePath)))
                .Take(20)
                .Select(d => new HubSymbol(byId[d.Id], d.Degree)));
        }

        var overview = new RepositoryOverview(repositoryId, status.Generation, status.Symbols, status.Relationships,
            kinds, languages, projects, edgeKinds, topFiles, hubs);
        _overviewCache[cacheKey] = (status.Generation, overview);
        return overview;
    }

    private static bool IsGeneratedPath(string filePath) =>
        filePath.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".pb.cs", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase) ||
        filePath.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
        filePath.Contains("/generated/", StringComparison.OrdinalIgnoreCase);

    public async Task<FileOutline> OutlineAsync(string repositoryId, string filePath, int maxSymbols = 500, CancellationToken cancellationToken = default)
    {
        var bounded = Math.Clamp(maxSymbols, 1, 500);
        // Ask for one extra row so truncated reflects an actual (bounded+1)th declaration found and
        // dropped, not just "returned count happened to equal the cap" (see search_symbols' identical
        // fix — a file with exactly `bounded` declarations must not be reported as truncated).
        await using var connection = await OpenAsync(repositoryId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SymbolColumns} FROM symbols WHERE file_path=$file AND kind<>'textual-evidence' ORDER BY start_line, start_column LIMIT $limit";
        Add(command, ("$file", filePath.Replace('\\', '/')), ("$limit", bounded + 1));
        var symbols = new List<SymbolRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) symbols.Add(ReadSymbol(reader));
        var truncated = symbols.Count > bounded;
        if (truncated) symbols.RemoveRange(bounded, symbols.Count - bounded);
        return new FileOutline(repositoryId, filePath.Replace('\\', '/'), symbols, truncated);
    }

    public async Task<IReadOnlyList<FileEntry>> FilesAsync(string repositoryId, string? contains, int limit, CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 2000);
        var status = await StatusAsync(repositoryId, cancellationToken);
        var cacheKey = $"{repositoryId}|{contains ?? ""}|{boundedLimit}";
        if (_filesCache.TryGetValue(cacheKey, out var cached) && cached.Generation == status.Generation && status.Generation is not null)
            return cached.Value;
        await using var connection = await OpenAsync(repositoryId, cancellationToken);
        await using var command = connection.CreateCommand();
        var where = string.IsNullOrWhiteSpace(contains) ? string.Empty : " AND lower(file_path) LIKE '%'||lower($contains)||'%' ESCAPE '\\'";
        command.CommandText = $"SELECT file_path, count(*), max(language) FROM symbols WHERE kind<>'textual-evidence'{where} GROUP BY file_path ORDER BY file_path LIMIT $limit";
        if (!string.IsNullOrWhiteSpace(contains)) Add(command, ("$contains", EscapeLikePattern(contains)));
        Add(command, ("$limit", boundedLimit));
        var files = new List<FileEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) files.Add(new FileEntry(reader.GetString(0), reader.GetInt32(1), reader.GetString(2)));
        _filesCache[cacheKey] = (status.Generation, files);
        return files;
    }

    public async Task<SymbolDetail?> SymbolAsync(string repositoryId, string symbolId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(repositoryId, cancellationToken);
        var symbol = (await SymbolsByIdsAsync(connection, [symbolId], cancellationToken)).FirstOrDefault();
        if (symbol is null) return null;
        var bounded = Math.Clamp(limit, 1, 400);
        var outgoing = new List<RelationshipRecord>();
        var incoming = new List<RelationshipRecord>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {EdgeColumns} FROM relationships WHERE source_id=$id LIMIT $limit";
            Add(command, ("$id", symbolId), ("$limit", bounded));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) outgoing.Add(ReadEdge(reader));
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {EdgeColumns} FROM relationships WHERE target_id=$id LIMIT $limit";
            Add(command, ("$id", symbolId), ("$limit", bounded));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) incoming.Add(ReadEdge(reader));
        }
        var neighborIds = outgoing.Select(e => e.TargetId).Concat(incoming.Select(e => e.SourceId))
            .Where(id => id != symbolId).Distinct(StringComparer.Ordinal).Take(200);
        var neighbors = await SymbolsByIdsAsync(connection, neighborIds, cancellationToken);
        return new SymbolDetail(symbol, outgoing, incoming, neighbors);
    }

    public Task DeleteAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        _overviewCache.TryRemove(repositoryId, out _);
        _dbPathByRepo.TryRemove(repositoryId, out _);
        _ftsBackfilled.TryRemove(repositoryId, out _);
        SqliteConnection.ClearAllPools();
        var directory = DirectoryFor(repositoryId);
        if (Directory.Exists(directory)) DeleteBestEffort(directory);
        return Task.CompletedTask;
    }

    /// <summary>Deletes as much of a directory tree as the OS currently allows. A leftover WAL or
    /// journal file held open by an antivirus scan or a not-yet-released handle must not turn
    /// "unregister this repository" into a 500 — the repo is already gone from the catalog by the
    /// time this runs; any file it can't remove now is harmless orphaned disk usage.</summary>
    private void DeleteBestEffort(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _logger.LogWarning(ex, "Could not delete {File} while removing a repository's data; it will be left behind", file); }
        }
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _logger.LogWarning(ex, "Could not fully remove {Directory}; some files are still in use", directory); }
    }

    public async Task<int> PurgePathAsync(string repositoryId, string pathPattern, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(repositoryId, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var escaped = EscapeLikePattern(pathPattern);

        int removed;
        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT count(*) FROM symbols WHERE lower(file_path) LIKE '%'||lower($pattern)||'%' ESCAPE '\\'";
            Add(count, ("$pattern", escaped));
            removed = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
        }

        if (removed > 0)
        {
            // Edges recorded at a matching path, or pointing at a symbol that is about to be
            // removed, must go too — otherwise they'd dangle (target/source no longer resolvable).
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    DELETE FROM relationships WHERE lower(file_path) LIKE '%'||lower($pattern)||'%' ESCAPE '\'
                        OR source_id IN (SELECT id FROM symbols WHERE lower(file_path) LIKE '%'||lower($pattern)||'%' ESCAPE '\')
                        OR target_id IN (SELECT id FROM symbols WHERE lower(file_path) LIKE '%'||lower($pattern)||'%' ESCAPE '\')
                    """;
                Add(command, ("$pattern", escaped));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM symbols_fts5 WHERE lower(file_path) LIKE '%'||lower($pattern)||'%' ESCAPE '\\'";
                Add(command, ("$pattern", escaped));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM symbols WHERE lower(file_path) LIKE '%'||lower($pattern)||'%' ESCAPE '\\'";
                Add(command, ("$pattern", escaped));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE meta SET generation=$generation, indexed_at=$indexed WHERE repository_id=$repo";
                Add(command, ("$repo", repositoryId), ("$generation", DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff")), ("$indexed", DateTimeOffset.UtcNow.ToString("O")));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
        _overviewCache.TryRemove(repositoryId, out _);
        return removed;
    }

    /// <summary>Turns free text into a safe FTS5 prefix query ("Execute Frame" → "\"Execute\"* \"Frame\"*"); null when no usable tokens.</summary>
    private static string? TryBuildFtsQuery(string query)
    {
        var tokens = query.Split([' ', '\t', '.', ':', '/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => new string(t.Where(char.IsLetterOrDigit).ToArray()))
            .Where(t => t.Length >= 2)
            .Take(6)
            .ToArray();
        return tokens.Length == 0 ? null : string.Join(' ', tokens.Select(t => $"\"{t}\"*"));
    }

    /// <summary>Escapes a LIKE pattern's own wildcard characters so user input like "get_%" or "a\b" is matched
    /// literally instead of as a wildcard. Pair with `ESCAPE '\'` in the SQL text.</summary>
    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private const string SymbolColumns = "id,repository_id,project,file_path,name,qualified_name,kind,start_line,start_column,end_line,end_column,signature,language,module_id";
    private const string SymbolColumnsQualified = "s.id,s.repository_id,s.project,s.file_path,s.name,s.qualified_name,s.kind,s.start_line,s.start_column,s.end_line,s.end_column,s.signature,s.language,s.module_id";
    private const string EdgeColumns = "id,repository_id,source_id,target_id,kind,file_path,line,column_number,confidence,language,module_id";

    private async Task<IReadOnlyList<RelationshipRecord>> RelationshipsForAsync(SqliteConnection connection, string id, CancellationToken cancellationToken, int limit, IReadOnlyList<string>? edgeKinds = null)
    {
        await using var command = connection.CreateCommand();
        var kindFilter = string.Empty;
        if (edgeKinds is { Count: > 0 })
        {
            var marks = edgeKinds.Select((_, i) => $"$k{i}").ToArray();
            kindFilter = $" AND kind IN ({string.Join(',', marks)})";
            for (var i = 0; i < edgeKinds.Count; i++) command.Parameters.AddWithValue(marks[i], edgeKinds[i]);
        }
        command.CommandText = $"""
            SELECT {EdgeColumns} FROM relationships WHERE source_id=$id{kindFilter}
            UNION
            SELECT {EdgeColumns} FROM relationships WHERE target_id=$id{kindFilter}
            LIMIT $limit
            """;
        Add(command, ("$id", id), ("$limit", Math.Clamp(limit, 1, 500)));
        var result = new List<RelationshipRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadEdge(reader));
        return result;
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<RelationshipRecord>>> RelationshipsForManyAsync(
        SqliteConnection connection, IEnumerable<string> symbolIds, CancellationToken cancellationToken, int perSymbolLimit)
    {
        var ids = symbolIds.Distinct(StringComparer.Ordinal).Take(200).ToArray();
        var result = ids.ToDictionary(id => id, _ => new List<RelationshipRecord>(), StringComparer.Ordinal);
        if (ids.Length == 0) return result.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<RelationshipRecord>)kvp.Value, StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        var marks = new List<string>();
        for (var i = 0; i < ids.Length; i++)
        {
            var parameter = $"$id{i}";
            marks.Add(parameter);
            command.Parameters.AddWithValue(parameter, ids[i]);
        }
        var inList = string.Join(',', marks);
        command.CommandText = $"""
            SELECT {EdgeColumns} FROM relationships WHERE source_id IN ({inList})
            UNION ALL
            SELECT {EdgeColumns} FROM relationships WHERE target_id IN ({inList})
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(ids.Length * perSymbolLimit * 2, 1, 10_000));

        var seenBySymbol = ids.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var edge = ReadEdge(reader);
            AddFor(edge.SourceId, edge);
            AddFor(edge.TargetId, edge);
        }

        return result.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<RelationshipRecord>)kvp.Value, StringComparer.Ordinal);

        void AddFor(string symbolId, RelationshipRecord edge)
        {
            if (!result.TryGetValue(symbolId, out var relationships)) return;
            if (relationships.Count >= perSymbolLimit) return;
            if (seenBySymbol[symbolId].Add(edge.Id)) relationships.Add(edge);
        }
    }

    private static async Task<IReadOnlyList<SymbolRecord>> SymbolsByIdsAsync(SqliteConnection connection, IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        var values = ids.Distinct().Take(500).ToArray();
        if (values.Length == 0) return [];
        await using var command = connection.CreateCommand();
        var marks = new List<string>();
        for (var i = 0; i < values.Length; i++) { marks.Add($"$id{i}"); command.Parameters.AddWithValue($"$id{i}", values[i]); }
        command.CommandText = $"SELECT {SymbolColumns} FROM symbols WHERE id IN ({string.Join(',', marks)})";
        var result = new List<SymbolRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadSymbol(reader));
        return result;
    }

    private static async Task<string?> GenerationAsync(SqliteConnection connection, string repo, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT generation FROM meta WHERE repository_id=$repo"; Add(command, ("$repo", repo));
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public string StoragePath(string repositoryId) => DirectoryFor(repositoryId);

    private string DirectoryFor(string repositoryId)
    {
        var slug = new string(repositoryId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());
        if (slug.Length > 48) slug = slug[..48];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repositoryId))).ToLowerInvariant()[..10];
        return Path.Combine(_root, $"{slug}__{hash}");
    }

    // Schema/migration DDL must run exactly once per database per process, serialized per file:
    // repeating DROP/CREATE VIRTUAL on every connection contends with concurrent WAL writers,
    // and concurrent recovery attempts would hold each other's WAL hostage.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _schemaLocks = new(StringComparer.OrdinalIgnoreCase);

    // Once a repository's database file is chosen and its schema verified, reuse it for the process lifetime.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _dbPathByRepo = new(StringComparer.OrdinalIgnoreCase);

    private async Task<SqliteConnection> OpenAsync(string repositoryId, CancellationToken cancellationToken)
    {
        if (_dbPathByRepo.TryGetValue(repositoryId, out var readyPath))
        {
            // Schema already verified for this file: pool the native handle so the common case
            // (search/graph/status) pays connection-open + WAL header parsing once, not per call.
            try
            {
                var ready = new SqliteConnection($"Data Source={readyPath};Cache=Private;Pooling=True;Default Timeout=60;Mode=ReadWriteCreate");
                await ready.OpenAsync(cancellationToken);
                return ready;
            }
            catch (SqliteException)
            {
                // A pooled native handle can end up poisoned by a transient disk error (I/O error,
                // dropped network volume, antivirus lock): once that happens, every future open on
                // the same connection string fails identically forever, since nothing ever clears
                // the pool or the cached path. Drop both and fall through to the slow path below,
                // which reopens the file directly and self-heals a genuinely damaged database.
                _dbPathByRepo.TryRemove(repositoryId, out _);
                SqliteConnection.ClearAllPools();
            }
        }

        var directory = DirectoryFor(repositoryId);
        Directory.CreateDirectory(directory);
        var gate = _schemaLocks.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_dbPathByRepo.TryGetValue(repositoryId, out readyPath))
            {
                try
                {
                    var ready = new SqliteConnection($"Data Source={readyPath};Cache=Private;Pooling=True;Default Timeout=60;Mode=ReadWriteCreate");
                    await ready.OpenAsync(cancellationToken);
                    return ready;
                }
                catch (SqliteException)
                {
                    _dbPathByRepo.TryRemove(repositoryId, out _);
                    SqliteConnection.ClearAllPools();
                }
            }
            // Try index.db, then numbered fallbacks. A damaged candidate (broken FTS shadow tables,
            // orphaned WAL held by another process) is deleted when possible and skipped otherwise:
            // the index is derived data and is rebuilt by the next indexing pass.
            SqliteException? last = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var dbPath = Path.Combine(directory, attempt == 0 ? "index.db" : $"index-{attempt}.db");
                SqliteConnection? connection = null;
                try
                {
                    connection = new SqliteConnection($"Data Source={dbPath};Cache=Private;Pooling=False;Default Timeout=60;Mode=ReadWriteCreate");
                    await connection.OpenAsync(cancellationToken);
                    await EnsureSchemaAsync(connection, cancellationToken);
                    _dbPathByRepo[repositoryId] = dbPath;
                    return connection;
                }
                catch (SqliteException ex)
                {
                    last = ex;
                    if (connection is not null) await connection.DisposeAsync();
                    SqliteConnection.ClearAllPools();
                    try
                    {
                        foreach (var file in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                            if (File.Exists(file)) File.Delete(file);
                    }
                    catch (Exception io) when (io is IOException or UnauthorizedAccessException)
                    {
                        // File held elsewhere: leave it behind and move on to the next candidate name.
                    }
                }
            }
            throw last!;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS meta(repository_id TEXT PRIMARY KEY,generation TEXT NOT NULL,indexed_at TEXT NOT NULL,diagnostics TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS symbols(id TEXT PRIMARY KEY,repository_id TEXT NOT NULL,project TEXT,file_path TEXT NOT NULL,name TEXT NOT NULL,qualified_name TEXT NOT NULL,kind TEXT NOT NULL,start_line INTEGER NOT NULL,start_column INTEGER NOT NULL,end_line INTEGER NOT NULL,end_column INTEGER NOT NULL,signature TEXT NOT NULL,language TEXT NOT NULL,module_id TEXT NOT NULL);
            DROP INDEX IF EXISTS idx_symbols_name;
            CREATE INDEX idx_symbols_name ON symbols(name COLLATE NOCASE);
            DROP INDEX IF EXISTS idx_symbols_qualified_name;
            CREATE INDEX idx_symbols_qualified_name ON symbols(qualified_name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_path);
            CREATE INDEX IF NOT EXISTS idx_symbols_kind ON symbols(kind);
            CREATE TABLE IF NOT EXISTS relationships(id TEXT PRIMARY KEY,repository_id TEXT NOT NULL,source_id TEXT NOT NULL,target_id TEXT NOT NULL,kind TEXT NOT NULL,file_path TEXT NOT NULL,line INTEGER NOT NULL,column_number INTEGER NOT NULL,confidence TEXT NOT NULL,language TEXT NOT NULL,module_id TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_edges_source ON relationships(source_id);
            CREATE INDEX IF NOT EXISTS idx_edges_target ON relationships(target_id);
            DROP TRIGGER IF EXISTS symbols_fts_ai;
            DROP TRIGGER IF EXISTS symbols_fts_ad;
            DROP TRIGGER IF EXISTS symbols_fts_au;
            DROP TABLE IF EXISTS symbols_fts;
            CREATE VIRTUAL TABLE IF NOT EXISTS symbols_fts5 USING fts5(symbol_id UNINDEXED, module_id UNINDEXED, name, qualified_name, file_path);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Rebuilds the standalone FTS table when empty (upgraded databases). Runs under an immediate
    /// transaction so concurrent connections cannot double-fill it.</summary>
    private static async Task BackfillFtsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            BEGIN IMMEDIATE;
            INSERT INTO symbols_fts5(symbol_id, module_id, name, qualified_name, file_path)
            SELECT id, module_id, name, qualified_name, file_path FROM symbols
            WHERE (SELECT count(*) FROM symbols_fts5) = 0;
            COMMIT;
            """;
        try { await command.ExecuteNonQueryAsync(cancellationToken); }
        catch (SqliteException) { /* another writer holds the lock; it will backfill */ }
    }

    private static async Task InsertFtsAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<SymbolRecord> symbols, CancellationToken cancellationToken)
    {
        if (symbols.Count == 0) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO symbols_fts5(symbol_id, module_id, name, qualified_name, file_path) VALUES($id,$module,$name,$qualified,$file)";
        var parameters = Prepare(command, ["$id", "$module", "$name", "$qualified", "$file"]);
        foreach (var s in symbols)
        {
            Bind(parameters, s.Id, s.ModuleId, s.Name, s.QualifiedName, s.FilePath);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static List<SqliteParameter> Prepare(SqliteCommand command, string[] names)
    {
        var parameters = new List<SqliteParameter>();
        foreach (var name in names)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            command.Parameters.Add(parameter);
            parameters.Add(parameter);
        }
        return parameters;
    }

    private static void Bind(List<SqliteParameter> parameters, params object?[] values)
    {
        for (var i = 0; i < parameters.Count; i++) parameters[i].Value = values[i] ?? DBNull.Value;
    }

    private static void Add(SqliteCommand command, params (string Name, object? Value)[] values)
    { foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value); }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, (string Name, object? Value)[] values, CancellationToken token)
    { await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; Add(command, values); await command.ExecuteNonQueryAsync(token); }

    private static SymbolRecord ReadSymbol(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetString(11), reader.GetString(12), reader.GetString(13));
    private static RelationshipRecord ReadEdge(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetString(8), reader.GetString(9), reader.GetString(10));
}
