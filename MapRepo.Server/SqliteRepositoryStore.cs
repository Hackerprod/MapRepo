using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
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

    public SqliteRepositoryStore(IHostEnvironment environment)
    {
        _root = Path.Combine(environment.ContentRootPath, "data-v4");
        Directory.CreateDirectory(_root);
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

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE meta SET generation=$generation, indexed_at=$indexed WHERE repository_id=$repo";
            Add(command, ("$repo", repositoryId), ("$generation", generation), ("$indexed", indexedAt.ToString("O")));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string repositoryId, string query, int limit, SearchFilter? filter = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(repositoryId, cancellationToken);
        var symbols = new List<(SymbolRecord Symbol, double Score)>();
        var boundedLimit = Math.Clamp(limit, 1, 200);
        var extra = new StringBuilder();
        var extraParameters = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(filter?.Kind)) { extra.Append(" AND kind=$kind COLLATE NOCASE"); extraParameters.Add(("$kind", filter!.Kind)); }
        if (!string.IsNullOrWhiteSpace(filter?.PathContains)) { extra.Append(" AND lower(file_path) LIKE '%'||lower($path)||'%'"); extraParameters.Add(("$path", filter!.PathContains)); }
        if (filter is { IncludeTextual: false }) extra.Append(" AND kind<>'textual-evidence'");

        await using (var exact = connection.CreateCommand())
        {
            exact.CommandText = $"""
                SELECT {SymbolColumns},100.0
                FROM symbols WHERE (name=$query COLLATE NOCASE OR qualified_name=$query COLLATE NOCASE){extra}
                ORDER BY length(name), file_path LIMIT $limit
                """;
            Add(exact, ("$query", query.Trim()), ("$limit", boundedLimit));
            Add(exact, extraParameters.ToArray());
            await using var reader = await exact.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) symbols.Add((ReadSymbol(reader), reader.GetDouble(14)));
        }
        if (symbols.Count == 0)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {SymbolColumns},
                       CASE WHEN lower(name) LIKE lower($query)||'%' THEN 60.0
                            WHEN lower(qualified_name) LIKE '%'||lower($query)||'%' THEN 50.0 ELSE 10.0 END AS score
                FROM symbols WHERE (name LIKE '%'||$query||'%' OR qualified_name LIKE '%'||$query||'%' OR file_path LIKE '%'||$query||'%'){extra}
                ORDER BY score DESC, length(name), file_path LIMIT $limit
                """;
            Add(command, ("$query", query), ("$limit", boundedLimit));
            Add(command, extraParameters.ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) symbols.Add((ReadSymbol(reader), reader.GetDouble(14)));
        }
        var relationshipsBySymbol = await RelationshipsForManyAsync(connection, symbols.Select(s => s.Symbol.Id), cancellationToken, 24);
        return symbols
            .Select(item => new SearchResult(item.Symbol, item.Score,
                relationshipsBySymbol.TryGetValue(item.Symbol.Id, out var relationships) ? relationships : []))
            .ToArray();
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
        var diagnostics = reader.IsDBNull(2) ? [] : reader.GetString(2).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return new RepositoryStatus(repositoryId, reader.GetString(0), reader.GetInt32(3), reader.GetInt32(4),
            DateTimeOffset.TryParse(reader.GetString(1), out var date) ? date : null, false, false, diagnostics);
    }

    public async Task<RepositoryOverview> OverviewAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        var status = await StatusAsync(repositoryId, cancellationToken);
        if (_overviewCache.TryGetValue(repositoryId, out var cached) && cached.Generation == status.Generation && status.Generation is not null)
            return cached.Value;
        await using var connection = await OpenAsync(repositoryId, cancellationToken);

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

        var kinds = await GroupAsync("SELECT kind,count(*) FROM symbols GROUP BY kind ORDER BY count(*) DESC LIMIT 30");
        var languages = await GroupAsync("SELECT language,count(*) FROM symbols GROUP BY language ORDER BY count(*) DESC LIMIT 12");
        var projects = await GroupAsync("SELECT project,count(*) FROM symbols GROUP BY project ORDER BY count(*) DESC LIMIT 30");
        var edgeKinds = await GroupAsync("SELECT kind,count(*) FROM relationships GROUP BY kind ORDER BY count(*) DESC LIMIT 12");
        var topFiles = await GroupAsync("SELECT file_path,count(*) FROM symbols WHERE kind<>'textual-evidence' GROUP BY file_path ORDER BY count(*) DESC LIMIT 20");

        var hubs = new List<HubSymbol>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT symbol_id, SUM(cnt) AS degree FROM (
                    SELECT source_id AS symbol_id, count(*) AS cnt FROM relationships GROUP BY source_id
                    UNION ALL
                    SELECT target_id, count(*) FROM relationships GROUP BY target_id
                ) GROUP BY symbol_id ORDER BY degree DESC LIMIT 40
                """;
            var degrees = new List<(string Id, int Degree)>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) degrees.Add((reader.GetString(0), reader.GetInt32(1)));
            var records = await SymbolsByIdsAsync(connection, degrees.Select(d => d.Id), cancellationToken);
            var byId = records.ToDictionary(r => r.Id, StringComparer.Ordinal);
            hubs.AddRange(degrees
                .Where(d => byId.ContainsKey(d.Id) && byId[d.Id].Kind != "textual-evidence")
                .Take(20)
                .Select(d => new HubSymbol(byId[d.Id], d.Degree)));
        }

        var overview = new RepositoryOverview(repositoryId, status.Generation, status.Symbols, status.Relationships,
            kinds, languages, projects, edgeKinds, topFiles, hubs);
        _overviewCache[repositoryId] = (status.Generation, overview);
        return overview;
    }

    public async Task<FileOutline> OutlineAsync(string repositoryId, string filePath, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(repositoryId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SymbolColumns} FROM symbols WHERE file_path=$file AND kind<>'textual-evidence' ORDER BY start_line, start_column LIMIT 500";
        Add(command, ("$file", filePath.Replace('\\', '/')));
        var symbols = new List<SymbolRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) symbols.Add(ReadSymbol(reader));
        return new FileOutline(repositoryId, filePath.Replace('\\', '/'), symbols);
    }

    public async Task<IReadOnlyList<FileEntry>> FilesAsync(string repositoryId, string? contains, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(repositoryId, cancellationToken);
        await using var command = connection.CreateCommand();
        var where = string.IsNullOrWhiteSpace(contains) ? string.Empty : " AND lower(file_path) LIKE '%'||lower($contains)||'%'";
        command.CommandText = $"SELECT file_path, count(*), max(language) FROM symbols WHERE kind<>'textual-evidence'{where} GROUP BY file_path ORDER BY file_path LIMIT $limit";
        if (!string.IsNullOrWhiteSpace(contains)) Add(command, ("$contains", contains));
        Add(command, ("$limit", Math.Clamp(limit, 1, 2000)));
        var files = new List<FileEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) files.Add(new FileEntry(reader.GetString(0), reader.GetInt32(1), reader.GetString(2)));
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
        SqliteConnection.ClearAllPools();
        var directory = DirectoryFor(repositoryId);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    private const string SymbolColumns = "id,repository_id,project,file_path,name,qualified_name,kind,start_line,start_column,end_line,end_column,signature,language,module_id";
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

    private string DirectoryFor(string repositoryId)
    {
        var slug = new string(repositoryId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());
        if (slug.Length > 48) slug = slug[..48];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repositoryId))).ToLowerInvariant()[..10];
        return Path.Combine(_root, $"{slug}__{hash}");
    }

    private async Task<SqliteConnection> OpenAsync(string repositoryId, CancellationToken cancellationToken)
    {
        var directory = DirectoryFor(repositoryId);
        Directory.CreateDirectory(directory);
        var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "index.db")};Cache=Private;Pooling=False;Default Timeout=60;Mode=ReadWriteCreate");
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        return connection;
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS meta(repository_id TEXT PRIMARY KEY,generation TEXT NOT NULL,indexed_at TEXT NOT NULL,diagnostics TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS symbols(id TEXT PRIMARY KEY,repository_id TEXT NOT NULL,project TEXT,file_path TEXT NOT NULL,name TEXT NOT NULL,qualified_name TEXT NOT NULL,kind TEXT NOT NULL,start_line INTEGER NOT NULL,start_column INTEGER NOT NULL,end_line INTEGER NOT NULL,end_column INTEGER NOT NULL,signature TEXT NOT NULL,language TEXT NOT NULL,module_id TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_symbols_name ON symbols(name);
            CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_path);
            CREATE INDEX IF NOT EXISTS idx_symbols_kind ON symbols(kind);
            CREATE TABLE IF NOT EXISTS relationships(id TEXT PRIMARY KEY,repository_id TEXT NOT NULL,source_id TEXT NOT NULL,target_id TEXT NOT NULL,kind TEXT NOT NULL,file_path TEXT NOT NULL,line INTEGER NOT NULL,column_number INTEGER NOT NULL,confidence TEXT NOT NULL,language TEXT NOT NULL,module_id TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_edges_source ON relationships(source_id);
            CREATE INDEX IF NOT EXISTS idx_edges_target ON relationships(target_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
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
