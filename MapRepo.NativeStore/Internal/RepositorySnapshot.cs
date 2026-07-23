using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using MapRepo.Core;
using MapRepo.NativeStore.Internal.Caching;
using MapRepo.NativeStore.Internal.Kernel;
using MapRepo.NativeStore.Internal.Packing;
using MapRepo.NativeStore.Internal.Search;
using MapRepo.NativeStore.Projection;

namespace MapRepo.NativeStore.Internal;

internal sealed class RepositorySnapshot : IRepositoryRecordSource
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly NativeStoreOptions _options;
    private readonly IReadOnlyPackFile? _file;
    private readonly SnapshotPackHeader? _header;
    private readonly BoundedLruCache<int, string> _strings;
    private readonly BoundedLruCache<int, SymbolRecord> _symbols;
    private readonly BoundedLruCache<int, RelationshipRecord> _relationships;
    private readonly OverviewSeed? _overviewSeed;
    private readonly OverviewSeed? _overviewIncludingGeneratedSeed;
    private readonly object _overviewGate = new();
    private RepositoryOverview? _overview;
    private RepositoryOverview? _overviewIncludingGenerated;
    private int _references = 1;
    private int _retired;
    private int _disposed;

    private RepositorySnapshot(
        StoreManifest manifest,
        NativeStoreOptions options,
        IReadOnlyPackFile? file,
        SnapshotPackHeader? header,
        IReadOnlyList<string>? recoveryNotes)
    {
        Manifest = manifest;
        _options = options;
        _file = file;
        _header = header;
        RecoveryNotes = recoveryNotes?.ToImmutableArray() ?? [];
        _strings = new BoundedLruCache<int, string>(options.DecodedStringCacheBytes,
            static (_, value) => 24L + value.Length * sizeof(char));
        _symbols = new BoundedLruCache<int, SymbolRecord>(options.MaterializedRecordCacheEntries);
        _relationships = new BoundedLruCache<int, RelationshipRecord>(options.MaterializedRecordCacheEntries);

        if (header is null)
        {
            _overviewSeed = null;
            _overviewIncludingGeneratedSeed = null;
            return;
        }
        var overviewSection = header.Sections[PackSectionKind.Overview];
        if (overviewSection.Length > int.MaxValue)
            throw new InvalidDataException("Overview projection is too large.");
        var decodedOverview = OverviewSeedCodec.Decode(file!.Slice(overviewSection.Offset, checked((int)overviewSection.Length)));
        ValidateOverviewSeed(decodedOverview.Normal, header);
        ValidateOverviewSeed(decodedOverview.IncludingGenerated, header);
        _overviewSeed = decodedOverview.Normal;
        _overviewIncludingGeneratedSeed = decodedOverview.IncludingGenerated;
    }

    public StoreManifest Manifest { get; }
    public ImmutableArray<string> RecoveryNotes { get; }
    public int SymbolCount => _header?.SymbolCount ?? 0;
    public int RelationshipCount => _header?.RelationshipCount ?? 0;
    public int ResolvedRelationshipCount => _header?.ResolvedRelationshipCount ?? 0;
    public int FileCount => _header?.FileCount ?? 0;
    public bool IsMemoryMapped => _file?.IsMemoryMapped ?? false;
    public long BackingFileBytes => _file?.Length ?? 0;
    public long DecodedStringCacheBytes => _strings.CurrentWeight;
    public int MaterializedRecordCacheEntries => _symbols.Count + _relationships.Count;
    public long EstimatedManagedBytes =>
        64L * 1024 + _strings.CurrentWeight +
        (_symbols.Count + _relationships.Count) * 320L + (_file?.EstimatedManagedBytes ?? 0);

    public static RepositorySnapshot Empty(StoreManifest manifest, NativeStoreOptions options, IReadOnlyList<string>? recoveryNotes = null) =>
        new(manifest, options, null, null, recoveryNotes);

    public static RepositorySnapshot Open(
        StoreManifest manifest,
        string snapshotPath,
        NativeStoreOptions options,
        IReadOnlyList<string>? recoveryNotes = null,
        bool? verifyChecksums = null)
    {
        if (manifest.Snapshot is null) throw new InvalidOperationException("Manifest has no snapshot-pack descriptor.");
        IReadOnlyPackFile? mapped = null;
        try
        {
            mapped = MappedFile.OpenRead(snapshotPath, options.MaxSnapshotPackBytes, options.MemoryMode);
            var header = PackHeaderCodec.Decode(mapped, manifest.RepositoryId, manifest.Snapshot, options,
                verifyChecksums ?? options.VerifySnapshotPackChecksumsOnOpen);
            return new RepositorySnapshot(manifest, options, mapped, header, recoveryNotes);
        }
        catch
        {
            mapped?.Dispose();
            throw;
        }
    }

    public SnapshotLease Acquire()
    {
        while (true)
        {
            if (Volatile.Read(ref _retired) != 0 || Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(RepositorySnapshot));
            var current = Volatile.Read(ref _references);
            if (current <= 0) throw new ObjectDisposedException(nameof(RepositorySnapshot));
            if (Interlocked.CompareExchange(ref _references, current + 1, current) == current)
                return new SnapshotLease(this);
        }
    }

    public void Retire()
    {
        if (Interlocked.Exchange(ref _retired, 1) == 0) Release();
    }

    public SearchOutcome SearchSymbols(
        string query,
        int limit,
        SearchFilter? filter,
        CancellationToken cancellationToken)
    {
        var bounded = Math.Clamp(limit, 1, 200);
        var found = SearchCore(query, bounded + 1, filter, cancellationToken);
        var truncated = found.Count > bounded;
        if (truncated) found.RemoveRange(bounded, found.Count - bounded);
        var items = new SearchResult[found.Count];
        for (var index = 0; index < found.Count; index++)
        {
            var value = found[index];
            var symbol = MaterializeSymbol(value.Ordinal);
            items[index] = new SearchResult(symbol, value.Score,
                GetAdjacentRelationships(value.Ordinal, 24, null, cancellationToken));
        }
        return new SearchOutcome(items, truncated);
    }

    public SymbolProjectionResult ProjectSymbols(SymbolProjectionRequest request, CancellationToken cancellationToken)
    {
        var budget = request.Budget ?? new ProjectionBudget();
        var maxItems = Math.Clamp(budget.MaxItems, 1, 200);
        var tokenBudget = Math.Max(1, budget.MaxEstimatedTokens);
        var found = SearchCore(request.Query, maxItems + 1, request.Filter, cancellationToken);
        var projected = new List<ProjectedSymbol>(Math.Min(maxItems, found.Count));
        var estimatedTokens = 0;
        foreach (var value in found)
        {
            if (projected.Count >= maxItems) break;
            var row = ReadSymbolRow(value.Ordinal);
            var item = Project(row, value.Score, request.Projection);
            if (projected.Count > 0 && estimatedTokens + item.EstimatedTokens > tokenBudget) break;
            projected.Add(item);
            estimatedTokens += item.EstimatedTokens;
        }
        return new SymbolProjectionResult(projected, projected.Count,
            found.Count > projected.Count, estimatedTokens, request.Projection);
    }

    public GraphResult GetGraph(
        string repositoryId,
        string symbolId,
        int depth,
        int limit,
        IReadOnlyList<string>? edgeKinds,
        CancellationToken cancellationToken)
    {
        var traversal = TraverseGraph(symbolId, depth, limit, edgeKinds, cancellationToken);
        if (traversal.RootOrdinal < 0)
            return new GraphResult(repositoryId, Manifest.Generation, [], [], false);
        return new GraphResult(
            repositoryId,
            Manifest.Generation,
            traversal.NodeOrdinals.Select(MaterializeSymbol).ToArray(),
            traversal.EdgeOrdinals.Select(MaterializeRelationship).ToArray(),
            traversal.Truncated);
    }

    public ProjectedGraphResult ProjectGraph(
        string repositoryId,
        string symbolId,
        int depth,
        int limit,
        IReadOnlyList<string>? edgeKinds,
        CancellationToken cancellationToken)
    {
        var traversal = TraverseGraph(symbolId, depth, limit, edgeKinds, cancellationToken);
        if (traversal.RootOrdinal < 0)
            return new ProjectedGraphResult(repositoryId, Manifest.Generation, [], [], false);
        var nodes = traversal.NodeOrdinals
            .Select(ordinal => Project(ReadSymbolRow(ordinal), 0, NativeProjectionKind.GraphOnly))
            .ToArray();
        return new ProjectedGraphResult(
            repositoryId,
            Manifest.Generation,
            nodes,
            GroupEdgeOrdinals(traversal.EdgeOrdinals),
            traversal.Truncated);
    }

    public ProjectedSymbolDetailResult? ProjectSymbolDetail(
        string symbolId,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ordinal = FindSymbolOrdinal(symbolId);
        if (ordinal < 0) return null;
        var bounded = Math.Clamp(limit, 1, 400);
        var outgoingPage = GetDirectionalEdgePage(ordinal, outgoing: true, bounded, cancellationToken);
        var incomingPage = GetDirectionalEdgePage(ordinal, outgoing: false, bounded, cancellationToken);
        var neighbors = new HashSet<int>();
        foreach (var edgeOrdinal in outgoingPage.Items)
        {
            var edge = ReadRelationshipRow(edgeOrdinal);
            if (edge.TargetOrdinal >= 0 && edge.TargetOrdinal != ordinal) neighbors.Add(edge.TargetOrdinal);
        }
        foreach (var edgeOrdinal in incomingPage.Items)
        {
            var edge = ReadRelationshipRow(edgeOrdinal);
            if (edge.SourceOrdinal >= 0 && edge.SourceOrdinal != ordinal) neighbors.Add(edge.SourceOrdinal);
        }
        var neighborOrdinals = neighbors.Order().Take(200).ToArray();
        return new ProjectedSymbolDetailResult(
            Project(ReadSymbolRow(ordinal), 0, NativeProjectionKind.Compact),
            GroupEdgeOrdinals(outgoingPage.Items),
            outgoingPage.HasMore,
            GroupEdgeOrdinals(incomingPage.Items),
            incomingPage.HasMore,
            neighborOrdinals.Select(value => Project(ReadSymbolRow(value), 0, NativeProjectionKind.GraphOnly)).ToArray(),
            outgoingPage.HasMore || incomingPage.HasMore || neighbors.Count > neighborOrdinals.Length);
    }

    public RepositoryStatus GetStatus(string repositoryId)
    {
        if (Manifest.Sequence == 0)
            return new RepositoryStatus(repositoryId, null, 0, 0, null, false, false, RecoveryNotes);
        var (diagnostics, summary) = RepositoryDiagnostics.Split(Manifest.Diagnostics);
        if (!RecoveryNotes.IsDefaultOrEmpty) diagnostics = diagnostics.Concat(RecoveryNotes).ToArray();
        return new RepositoryStatus(repositoryId, Manifest.Generation, SymbolCount, RelationshipCount,
            Manifest.IndexedAt, false, false, diagnostics, summary);
    }

    public RepositoryOverview GetOverview(string repositoryId, bool includeGenerated, CancellationToken cancellationToken)
    {
        lock (_overviewGate)
        {
            var cached = includeGenerated ? _overviewIncludingGenerated : _overview;
            if (cached is not null) return cached;
            var created = BuildOverview(repositoryId, includeGenerated, cancellationToken);
            if (includeGenerated) _overviewIncludingGenerated = created; else _overview = created;
            return created;
        }
    }

    public FileOutline GetOutline(string repositoryId, string filePath, int maxSymbols, CancellationToken cancellationToken)
    {
        var projected = ProjectOutline(repositoryId, filePath, maxSymbols, compact: false, cancellationToken);
        var symbols = new SymbolRecord[projected.Symbols.Count];
        var normalized = FileModuleKey.NormalizePath(filePath);
        var fileOrdinal = FindFileOrdinal(normalized);
        if (fileOrdinal < 0)
            return new FileOutline(repositoryId, normalized, [], projected.HasMore);
        var row = ReadFileRow(fileOrdinal);
        var ordinals = IntSection(PackSectionKind.OutlineSymbols).Slice(row.OutlineStart, projected.Symbols.Count);
        for (var index = 0; index < symbols.Length; index++) symbols[index] = MaterializeSymbol(ordinals[index]);
        return new FileOutline(repositoryId, normalized, symbols, projected.HasMore);
    }

    public FileOutlineProjectionResult ProjectOutline(
        string repositoryId,
        string filePath,
        int maxSymbols,
        bool compact,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = FileModuleKey.NormalizePath(filePath);
        var fileOrdinal = FindFileOrdinal(normalized);
        if (fileOrdinal < 0)
            return new FileOutlineProjectionResult(repositoryId, normalized, [], false, compact);
        var row = ReadFileRow(fileOrdinal);
        var bounded = Math.Clamp(maxSymbols, 1, 500);
        var count = Math.Min(bounded, row.OutlineCount);
        var ordinals = IntSection(PackSectionKind.OutlineSymbols).Slice(row.OutlineStart, count);
        var result = new ProjectedOutlineSymbol[count];
        for (var index = 0; index < count; index++)
        {
            var symbol = ReadSymbolRow(ordinals[index]);
            var name = GetString(symbol.NameStringId);
            if (compact)
            {
                result[index] = new ProjectedOutlineSymbol(null, name, null, GetString(symbol.KindStringId), null,
                    symbol.StartLine, null, null);
                continue;
            }
            var qualified = GetString(symbol.QualifiedNameStringId);
            var signature = GetString(symbol.SignatureStringId);
            result[index] = new ProjectedOutlineSymbol(
                GetString(symbol.IdStringId),
                name,
                string.Equals(qualified, name, StringComparison.Ordinal) ? null : qualified,
                GetString(symbol.KindStringId),
                symbol.ProjectStringId < 0 ? null : GetString(symbol.ProjectStringId),
                symbol.StartLine,
                symbol.EndLine == symbol.StartLine ? null : symbol.EndLine,
                string.Equals(signature, name, StringComparison.Ordinal) ? null : signature);
        }
        return new FileOutlineProjectionResult(repositoryId, normalized, result, row.OutlineCount > bounded, compact);
    }

    public IReadOnlyList<FileEntry> GetFiles(string? contains, int limit, CancellationToken cancellationToken) =>
        ProjectFiles(contains, limit, cancellationToken).Items;

    public FileProjectionResult ProjectFiles(string? contains, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bounded = Math.Clamp(limit, 1, 2_000);
        var normalized = string.IsNullOrWhiteSpace(contains) ? null : CodeTokenizer.Normalize(contains);
        IEnumerable<int> candidates;
        if (normalized is { Length: >= 3 })
        {
            var values = IntersectTrigrams(normalized, fileIndex: true, cancellationToken);
            if (values.Length == 0) return new FileProjectionResult([], false);
            candidates = values;
        }
        else candidates = EnumerateVisibleFileOrdinals();

        var result = new List<FileEntry>(Math.Min(bounded + 1, FileCount));
        foreach (var ordinal in candidates)
        {
            var row = ReadFileRow(ordinal);
            if ((row.Flags & 1) == 0) continue;
            var path = GetString(row.PathStringId);
            if (normalized is not null && !path.Contains(contains!, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(new FileEntry(path, row.SymbolCount, GetString(row.LanguageStringId)));
            if (result.Count > bounded) break;
        }
        var hasMore = result.Count > bounded;
        if (hasMore) result.RemoveAt(result.Count - 1);
        return new FileProjectionResult(result, hasMore);
    }

    public SymbolDetail? GetSymbol(string symbolId, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ordinal = FindSymbolOrdinal(symbolId);
        if (ordinal < 0) return null;
        var bounded = Math.Clamp(limit, 1, 400);
        var outgoingPage = GetDirectionalEdgePage(ordinal, outgoing: true, bounded, cancellationToken);
        var incomingPage = GetDirectionalEdgePage(ordinal, outgoing: false, bounded, cancellationToken);
        var outgoing = outgoingPage.Items.Select(MaterializeRelationship).ToArray();
        var incoming = incomingPage.Items.Select(MaterializeRelationship).ToArray();
        var neighbors = new HashSet<int>();
        foreach (var edgeOrdinal in outgoingPage.Items)
        {
            var edge = ReadRelationshipRow(edgeOrdinal);
            if (edge.TargetOrdinal >= 0 && edge.TargetOrdinal != ordinal) neighbors.Add(edge.TargetOrdinal);
        }
        foreach (var edgeOrdinal in incomingPage.Items)
        {
            var edge = ReadRelationshipRow(edgeOrdinal);
            if (edge.SourceOrdinal >= 0 && edge.SourceOrdinal != ordinal) neighbors.Add(edge.SourceOrdinal);
        }
        var neighborTruncated = outgoingPage.HasMore || incomingPage.HasMore || neighbors.Count > 200;
        return new SymbolDetail(MaterializeSymbol(ordinal), outgoing, incoming,
            neighbors.Order().Take(200).Select(MaterializeSymbol).ToArray(),
            outgoingPage.HasMore,
            incomingPage.HasMore,
            neighborTruncated);
    }

    public IEnumerable<SymbolRecord> EnumerateSymbols(CancellationToken cancellationToken)
    {
        for (var ordinal = 0; ordinal < SymbolCount; ordinal++)
        {
            if ((ordinal & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            yield return MaterializeSymbol(ordinal);
        }
    }

    public IEnumerable<RelationshipRecord> EnumerateRelationships(CancellationToken cancellationToken)
    {
        for (var ordinal = 0; ordinal < RelationshipCount; ordinal++)
        {
            if ((ordinal & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            yield return MaterializeRelationship(ordinal);
        }
    }

    private List<SearchCandidate> SearchCore(
        string query,
        int fetchLimit,
        SearchFilter? filter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_header is null || fetchLimit <= 0 || string.IsNullOrWhiteSpace(query)) return [];
        var trimmed = query.Trim();
        var normalized = CodeTokenizer.Normalize(trimmed);
        var exactCandidates = new HashSet<int>();
        if (TryFindLexeme(normalized, out var exactLexeme))
        {
            foreach (var posting in EnumerateLexemePostings(exactLexeme))
                if ((posting.Fields & (LexemeField.Name | LexemeField.QualifiedName)) != 0) exactCandidates.Add(posting.SymbolOrdinal);
        }
        if (exactCandidates.Count > 0)
        {
            var exact = ScoreCandidates(exactCandidates, normalized, CodeTokenizer.Tokenize(trimmed), filter, exactOnly: true, cancellationToken);
            if (exact.Count > 0) return exact.Take(fetchLimit).ToList();
        }

        var queryTokens = CodeTokenizer.Tokenize(trimmed);
        if (queryTokens.Length == 0) queryTokens = [normalized];
        HashSet<int>? candidates = null;
        foreach (var token in queryTokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = new HashSet<int>();
            if (TryFindLexeme(token, out var term)) AddLexemePostings(group, term);
            AddPrefixPostings(group, token, cancellationToken);
            if (token.Length >= 3)
            {
                foreach (var value in IntersectTrigrams(token, fileIndex: false, cancellationToken)) group.Add(value);
            }
            if (group.Count == 0) return [];
            if (candidates is null) candidates = group;
            else candidates.IntersectWith(group);
            if (candidates.Count == 0) return [];
        }
        return ScoreCandidates(candidates ?? [], normalized, queryTokens, filter, exactOnly: false, cancellationToken)
            .Take(fetchLimit).ToList();
    }

    private List<SearchCandidate> ScoreCandidates(
        IEnumerable<int> ordinals,
        string normalizedQuery,
        ImmutableArray<string> queryTokens,
        SearchFilter? filter,
        bool exactOnly,
        CancellationToken cancellationToken)
    {
        var result = new List<SearchCandidate>();
        var inspected = 0;
        foreach (var ordinal in ordinals)
        {
            if ((inspected++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if ((uint)ordinal >= (uint)SymbolCount) continue;
            var row = ReadSymbolRow(ordinal);
            if (!MatchesFilter(row, filter)) continue;
            if (!exactOnly && !MatchesQuery(row, normalizedQuery, queryTokens)) continue;
            var score = exactOnly ? 100.0 : LexicalScore(row, normalizedQuery) + Bm25(row, ordinal, queryTokens);
            result.Add(new SearchCandidate(ordinal, score));
        }
        result.Sort((left, right) =>
        {
            var score = right.Score.CompareTo(left.Score);
            if (score != 0) return score;
            var leftRow = ReadSymbolRow(left.Ordinal);
            var rightRow = ReadSymbolRow(right.Ordinal);
            var length = GetString(leftRow.NameStringId).Length.CompareTo(GetString(rightRow.NameStringId).Length);
            if (length != 0) return length;
            var path = StringComparer.Ordinal.Compare(GetFilePath(leftRow.FileOrdinal), GetFilePath(rightRow.FileOrdinal));
            return path != 0 ? path : StringComparer.Ordinal.Compare(GetString(leftRow.IdStringId), GetString(rightRow.IdStringId));
        });
        return result;
    }

    private double Bm25(SymbolRow row, int symbolOrdinal, ImmutableArray<string> queryTokens)
    {
        if (_header is null || queryTokens.Length == 0 || SymbolCount == 0) return 0;
        const double k1 = 1.2;
        const double b = 0.75;
        var averageLength = Math.Max(1.0, _header.TotalDocumentLength / (double)SymbolCount);
        var score = 0.0;
        foreach (var token in queryTokens.Distinct(StringComparer.Ordinal))
        {
            if (!TryFindLexeme(token, out var lexeme) || !TryGetLexemeFrequency(lexeme, symbolOrdinal, out var frequency) || frequency == 0) continue;
            var documentFrequency = lexeme.DocumentFrequency;
            var idf = Math.Log(1.0 + (SymbolCount - documentFrequency + 0.5) / (documentFrequency + 0.5));
            var denominator = frequency + k1 * (1.0 - b + b * row.DocumentLength / averageLength);
            score += idf * frequency * (k1 + 1.0) / denominator;
        }
        return score * 5.0;
    }

    private bool MatchesFilter(SymbolRow row, SearchFilter? filter)
    {
        if (!string.IsNullOrWhiteSpace(filter?.Kind) &&
            !string.Equals(GetString(row.KindStringId), filter.Kind, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(filter?.PathContains) &&
            !GetFilePath(row.FileOrdinal).Contains(filter.PathContains, StringComparison.OrdinalIgnoreCase)) return false;
        return filter is not { IncludeTextual: false } || (row.Flags & 1) == 0;
    }

    private bool MatchesQuery(SymbolRow row, string normalizedQuery, ImmutableArray<string> queryTokens)
    {
        var name = GetString(row.NameStringId);
        var qualified = GetString(row.QualifiedNameStringId);
        var signature = GetString(row.SignatureStringId);
        var path = GetFilePath(row.FileOrdinal);
        if (Contains(name, normalizedQuery) || Contains(qualified, normalizedQuery) ||
            Contains(signature, normalizedQuery) || Contains(path, normalizedQuery)) return true;
        return queryTokens.All(token => Contains(name, token) || Contains(qualified, token) ||
            Contains(signature, token) || Contains(path, token));
    }

    private double LexicalScore(SymbolRow row, string query)
    {
        var name = GetString(row.NameStringId);
        var qualified = GetString(row.QualifiedNameStringId);
        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(qualified, query, StringComparison.OrdinalIgnoreCase)) return 100;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 70;
        if (Contains(name, query)) return 60;
        if (Contains(qualified, query)) return 50;
        if (Contains(GetString(row.SignatureStringId), query)) return 45;
        if (Contains(GetFilePath(row.FileOrdinal), query)) return 30;
        return 10;
    }

    private static bool Contains(string value, string normalizedNeedle) =>
        value.Contains(normalizedNeedle, StringComparison.OrdinalIgnoreCase);

    private void AddPrefixPostings(HashSet<int> destination, string prefix, CancellationToken cancellationToken)
    {
        if (prefix.Length < 2 || _header is null) return;
        var section = Section(PackSectionKind.Lexemes);
        var low = 0;
        var high = section.Count;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            var value = GetString(ReadLexemeRow(middle).StringId);
            if (StringComparer.Ordinal.Compare(value, prefix) < 0) low = middle + 1; else high = middle;
        }
        var inspected = 0;
        for (var index = low; index < section.Count && inspected < _options.MaxPrefixLexemesPerToken; index++, inspected++)
        {
            if ((inspected & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            var row = ReadLexemeRow(index);
            if (!GetString(row.StringId).StartsWith(prefix, StringComparison.Ordinal)) break;
            AddLexemePostings(destination, row);
        }
    }

    private int[] IntersectTrigrams(string normalized, bool fileIndex, CancellationToken cancellationToken)
    {
        var hashes = CodeTokenizer.TrigramHashes(normalized).Distinct().ToArray();
        if (hashes.Length == 0) return [];
        var rows = new List<TrigramRow>(hashes.Length);
        foreach (var hash in hashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryFindTrigram(hash, fileIndex, out var row)) return [];
            rows.Add(row);
        }
        rows.Sort(static (left, right) => left.PostingCount.CompareTo(right.PostingCount));
        var postingKind = fileIndex ? PackSectionKind.FileTrigramPostings : PackSectionKind.TrigramPostings;
        var candidates = IntSection(postingKind).Slice(rows[0].PostingStart, rows[0].PostingCount).ToArray();
        for (var index = 1; index < rows.Count && candidates.Length > 0; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var other = IntSection(postingKind).Slice(rows[index].PostingStart, rows[index].PostingCount);
            var merged = new int[Math.Min(candidates.Length, other.Length)];
            var left = 0;
            var right = 0;
            var count = 0;
            while (left < candidates.Length && right < other.Length)
            {
                if (candidates[left] == other[right]) { merged[count++] = candidates[left]; left++; right++; }
                else if (candidates[left] < other[right]) left++;
                else right++;
            }
            if (count != merged.Length) Array.Resize(ref merged, count);
            candidates = merged;
        }
        return candidates;
    }

    private bool TryFindLexeme(string value, out LexemeRow result)
    {
        if (_header is null) { result = default; return false; }
        var count = Section(PackSectionKind.Lexemes).Count;
        var low = 0;
        var high = count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            var row = ReadLexemeRow(middle);
            var comparison = StringComparer.Ordinal.Compare(GetString(row.StringId), value);
            if (comparison == 0) { result = row; return true; }
            if (comparison < 0) low = middle + 1; else high = middle - 1;
        }
        result = default;
        return false;
    }

    private bool TryFindTrigram(uint hash, bool fileIndex, out TrigramRow result)
    {
        var kind = fileIndex ? PackSectionKind.FileTrigrams : PackSectionKind.Trigrams;
        var count = Section(kind).Count;
        var low = 0;
        var high = count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            var row = ReadTrigramRow(kind, middle);
            if (row.Hash == hash) { result = row; return true; }
            if (row.Hash < hash) low = middle + 1; else high = middle - 1;
        }
        result = default;
        return false;
    }

    private void AddLexemePostings(HashSet<int> destination, LexemeRow row)
    {
        foreach (var posting in EnumerateLexemePostings(row)) destination.Add(posting.SymbolOrdinal);
    }

    private IEnumerable<LexemePosting> EnumerateLexemePostings(LexemeRow row)
    {
        for (var index = 0; index < row.PostingCount; index++) yield return ReadLexemePosting(row.PostingStart + index);
    }

    private bool TryGetLexemeFrequency(LexemeRow row, int symbolOrdinal, out ushort frequency)
    {
        var low = 0;
        var high = row.PostingCount - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            var posting = ReadLexemePosting(row.PostingStart + middle);
            if (posting.SymbolOrdinal == symbolOrdinal) { frequency = posting.Frequency; return true; }
            if (posting.SymbolOrdinal < symbolOrdinal) low = middle + 1; else high = middle - 1;
        }
        frequency = 0;
        return false;
    }

    private int FindSymbolOrdinal(string symbolId) => FindLookupOrdinal(PackSectionKind.SymbolLookup, symbolId, symbol: true);

    private int FindFileOrdinal(string normalizedPath)
    {
        if (_header is null) return -1;
        var low = 0;
        var high = FileCount - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            var comparison = StringComparer.Ordinal.Compare(
                GetString(ReadFileRow(middle).PathStringId),
                normalizedPath);
            if (comparison == 0) return middle;
            if (comparison < 0) low = middle + 1; else high = middle - 1;
        }
        return -1;
    }

    private IEnumerable<int> EnumerateVisibleFileOrdinals()
    {
        for (var ordinal = 0; ordinal < FileCount; ordinal++)
            if ((ReadFileRow(ordinal).Flags & 1) != 0) yield return ordinal;
    }

    private int FindLookupOrdinal(PackSectionKind kind, string id, bool symbol)
    {
        if (_header is null) return -1;
        var hash = StableHash.String64(id);
        var count = Section(kind).Count;
        var low = 0;
        var high = count;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (ReadLookupRow(kind, middle).Hash < hash) low = middle + 1; else high = middle;
        }
        for (var index = low; index < count; index++)
        {
            var row = ReadLookupRow(kind, index);
            if (row.Hash != hash) break;
            var stringId = symbol ? ReadSymbolRow(row.Ordinal).IdStringId : ReadRelationshipRow(row.Ordinal).IdStringId;
            if (string.Equals(GetString(stringId), id, StringComparison.Ordinal)) return row.Ordinal;
        }
        return -1;
    }

    private IReadOnlyList<RelationshipRecord> GetAdjacentRelationships(
        int symbolOrdinal,
        int limit,
        IReadOnlySet<string>? kinds,
        CancellationToken cancellationToken) =>
        GetAdjacentEdgeOrdinals(symbolOrdinal, limit, kinds, cancellationToken).Select(MaterializeRelationship).ToArray();

    private List<int> GetAdjacentEdgeOrdinals(
        int symbolOrdinal,
        int limit,
        IReadOnlySet<string>? kinds,
        CancellationToken cancellationToken)
    {
        var bounded = Math.Max(0, limit);
        var result = new List<int>(Math.Min(bounded, 64));
        if (bounded == 0) return result;
        var seen = new HashSet<int>();
        AddDirection(outgoing: true);
        if (result.Count < bounded) AddDirection(outgoing: false);
        return result;

        void AddDirection(bool outgoing)
        {
            var offsets = IntSection(outgoing ? PackSectionKind.OutgoingOffsets : PackSectionKind.IncomingOffsets);
            var edges = IntSection(outgoing ? PackSectionKind.OutgoingEdges : PackSectionKind.IncomingEdges);
            if ((uint)symbolOrdinal >= (uint)(offsets.Length - 1))
                throw new InvalidDataException($"Invalid symbol ordinal for graph adjacency: {symbolOrdinal}.");
            var start = offsets[symbolOrdinal];
            var count = offsets[symbolOrdinal + 1] - start;
            if (start < 0 || count < 0 || start > edges.Length - count)
                throw new InvalidDataException("Invalid graph adjacency bounds in snapshot pack.");
            for (var index = 0; index < count && result.Count < bounded; index++)
            {
                if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var edgeOrdinal = edges[start + index];
                if (!seen.Add(edgeOrdinal)) continue;
                if (kinds is not null &&
                    !kinds.Contains(GetString(ReadRelationshipRow(edgeOrdinal).KindStringId))) continue;
                result.Add(edgeOrdinal);
            }
        }
    }

    private (int[] Items, bool HasMore) GetDirectionalEdgePage(
        int symbolOrdinal,
        bool outgoing,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var offsets = IntSection(outgoing ? PackSectionKind.OutgoingOffsets : PackSectionKind.IncomingOffsets);
        var edges = IntSection(outgoing ? PackSectionKind.OutgoingEdges : PackSectionKind.IncomingEdges);
        if ((uint)symbolOrdinal >= (uint)(offsets.Length - 1))
            throw new InvalidDataException($"Invalid symbol ordinal for graph adjacency: {symbolOrdinal}.");
        var start = offsets[symbolOrdinal];
        var count = offsets[symbolOrdinal + 1] - start;
        if (start < 0 || count < 0 || start > edges.Length - count)
            throw new InvalidDataException("Invalid graph adjacency bounds in snapshot pack.");
        var bounded = Math.Max(0, limit);
        return (edges.Slice(start, Math.Min(count, bounded)).ToArray(), count > bounded);
    }

    private GraphOrdinalResult TraverseGraph(
        string symbolId,
        int depth,
        int limit,
        IReadOnlyList<string>? edgeKinds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = FindSymbolOrdinal(symbolId);
        if (root < 0) return new GraphOrdinalResult(-1, [], [], false);
        const int maxEdgesPerExpandedNode = 500;
        var boundedLimit = Math.Clamp(limit, 1, 2_000);
        var kinds = edgeKinds is { Count: > 0 } ? edgeKinds.ToHashSet(StringComparer.Ordinal) : null;
        var nodes = new HashSet<int> { root };
        var edgeOrdinals = new HashSet<int>();
        var frontier = new HashSet<int> { root };
        var orderedEdges = new List<int>();
        var truncated = false;
        for (var level = 0; level < Math.Clamp(depth, 0, 5) && frontier.Count > 0; level++)
        {
            var next = new HashSet<int>();
            foreach (var ordinal in frontier.Order())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var adjacent = GetAdjacentEdgeOrdinals(ordinal, maxEdgesPerExpandedNode + 1, kinds, cancellationToken);
                if (adjacent.Count > maxEdgesPerExpandedNode) truncated = true;
                foreach (var edgeOrdinal in adjacent.Take(maxEdgesPerExpandedNode))
                {
                    if (!edgeOrdinals.Add(edgeOrdinal)) continue;
                    var edge = ReadRelationshipRow(edgeOrdinal);
                    var needsSource = edge.SourceOrdinal >= 0 && !nodes.Contains(edge.SourceOrdinal);
                    var needsTarget = edge.TargetOrdinal >= 0 && !nodes.Contains(edge.TargetOrdinal) && edge.TargetOrdinal != edge.SourceOrdinal;
                    var required = (needsSource ? 1 : 0) + (needsTarget ? 1 : 0);
                    if (nodes.Count + required > boundedLimit)
                    {
                        edgeOrdinals.Remove(edgeOrdinal);
                        truncated = true;
                        continue;
                    }
                    orderedEdges.Add(edgeOrdinal);
                    if (needsSource && nodes.Add(edge.SourceOrdinal)) next.Add(edge.SourceOrdinal);
                    if (needsTarget && nodes.Add(edge.TargetOrdinal)) next.Add(edge.TargetOrdinal);
                }
            }
            frontier = next;
        }

        var orderedNodes = nodes
            .OrderBy(value => value == root ? 0 : 1)
            .ThenBy(value => GetString(ReadSymbolRow(value).QualifiedNameStringId), StringComparer.Ordinal)
            .ThenBy(value => GetString(ReadSymbolRow(value).IdStringId), StringComparer.Ordinal)
            .ToArray();
        return new GraphOrdinalResult(root, orderedNodes, orderedEdges.ToArray(), truncated);
    }

    private IReadOnlyList<ProjectedEdgeGroup> GroupEdgeOrdinals(IEnumerable<int> ordinals)
    {
        var groups = new Dictionary<EdgeGroupKey, EdgeGroupAccumulator>();
        foreach (var ordinal in ordinals)
        {
            var row = ReadRelationshipRow(ordinal);
            var key = new EdgeGroupKey(
                row.SourceIdStringId,
                row.TargetIdStringId,
                row.KindStringId,
                row.FileOrdinal);
            if (!groups.TryGetValue(key, out var accumulator))
            {
                accumulator = new EdgeGroupAccumulator();
                groups.Add(key, accumulator);
            }
            accumulator.Add(row.Line);
        }

        return groups
            .OrderBy(pair => GetString(pair.Key.SourceIdStringId), StringComparer.Ordinal)
            .ThenBy(pair => GetString(pair.Key.TargetIdStringId), StringComparer.Ordinal)
            .ThenBy(pair => GetString(pair.Key.KindStringId), StringComparer.Ordinal)
            .ThenBy(pair => GetFilePath(pair.Key.FileOrdinal), StringComparer.Ordinal)
            .Select(pair => new ProjectedEdgeGroup(
                GetString(pair.Key.SourceIdStringId),
                GetString(pair.Key.TargetIdStringId),
                GetString(pair.Key.KindStringId),
                GetFilePath(pair.Key.FileOrdinal),
                pair.Value.Lines,
                pair.Value.OccurrenceCount))
            .ToArray();
    }

    private RepositoryOverview BuildOverview(string repositoryId, bool includeGenerated, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var seed = includeGenerated ? _overviewIncludingGeneratedSeed : _overviewSeed;
        if (seed is null)
            return new RepositoryOverview(repositoryId, Manifest.Sequence == 0 ? null : Manifest.Generation,
                0, 0, [], [], [], [], [], []);
        return new RepositoryOverview(
            repositoryId,
            Manifest.Sequence == 0 ? null : Manifest.Generation,
            seed.Symbols,
            seed.Relationships,
            seed.Kinds.Select(value => new OverviewEntry(GetString(value.StringId), value.Count)).ToArray(),
            seed.Languages.Select(value => new OverviewEntry(GetString(value.StringId), value.Count)).ToArray(),
            seed.Projects.Select(value => new OverviewEntry(GetString(value.StringId), value.Count)).ToArray(),
            seed.EdgeKinds.Select(value => new OverviewEntry(GetString(value.StringId), value.Count)).ToArray(),
            seed.TopFiles.Select(value => new OverviewEntry(GetFilePath(value.FileOrdinal), value.Count)).ToArray(),
            seed.Hubs.Select(value => new HubSymbol(MaterializeSymbol(value.SymbolOrdinal), value.Degree)).ToArray());
    }

    private static void ValidateOverviewSeed(OverviewSeed seed, SnapshotPackHeader header)
    {
        if (seed.Symbols != header.SymbolCount || seed.Relationships != header.RelationshipCount)
            throw new InvalidDataException("Overview projection counts differ from the snapshot header.");
        foreach (var value in seed.Kinds.Concat(seed.Languages).Concat(seed.Projects).Concat(seed.EdgeKinds))
            if ((uint)value.StringId >= (uint)header.StringCount)
                throw new InvalidDataException("Overview projection contains an invalid string ID.");
        foreach (var value in seed.TopFiles)
            if ((uint)value.FileOrdinal >= (uint)header.FileCount)
                throw new InvalidDataException("Overview projection contains an invalid file ordinal.");
        foreach (var value in seed.Hubs)
            if ((uint)value.SymbolOrdinal >= (uint)header.SymbolCount)
                throw new InvalidDataException("Overview projection contains an invalid symbol ordinal.");
    }

    private ProjectedSymbol Project(SymbolRow row, double score, NativeProjectionKind projection)
    {
        var id = GetString(row.IdStringId);
        var name = GetString(row.NameStringId);
        var kind = GetString(row.KindStringId);
        string? qualified = null;
        string? project = null;
        string? filePath = null;
        string? signature = null;
        string? language = null;
        string? moduleId = null;
        int? startLine = null;
        int? endLine = null;
        int? startColumn = null;
        int? endColumn = null;

        if (projection is not NativeProjectionKind.GraphOnly)
        {
            var value = GetString(row.QualifiedNameStringId);
            qualified = string.Equals(value, name, StringComparison.Ordinal) ? null : value;
            filePath = GetFilePath(row.FileOrdinal);
            startLine = row.StartLine;
        }
        if (projection is NativeProjectionKind.Compact or NativeProjectionKind.Full)
        {
            project = row.ProjectStringId < 0 ? null : GetString(row.ProjectStringId);
            var value = GetString(row.SignatureStringId);
            signature = string.Equals(value, name, StringComparison.Ordinal) ? null : value;
            endLine = row.EndLine == row.StartLine ? null : row.EndLine;
        }
        if (projection is NativeProjectionKind.Full)
        {
            language = GetString(row.LanguageStringId);
            moduleId = GetString(row.ModuleStringId);
            startColumn = row.StartColumn;
            endColumn = row.EndColumn;
        }

        var characters = id.Length + name.Length + kind.Length + (qualified?.Length ?? 0) +
            (project?.Length ?? 0) + (filePath?.Length ?? 0) + (signature?.Length ?? 0) +
            (language?.Length ?? 0) + (moduleId?.Length ?? 0) + 72;
        return new ProjectedSymbol(
            id,
            name,
            qualified,
            kind,
            project,
            filePath,
            signature,
            startLine,
            endLine,
            language,
            moduleId,
            startColumn,
            endColumn,
            score,
            Math.Max(1, (characters + 3) / 4));
    }

    private SymbolRecord MaterializeSymbol(int ordinal) => _symbols.GetOrAdd(ordinal, value =>
    {
        var row = ReadSymbolRow(value);
        return new SymbolRecord(
            GetString(row.IdStringId),
            Manifest.RepositoryId,
            row.ProjectStringId < 0 ? null : GetString(row.ProjectStringId),
            GetFilePath(row.FileOrdinal),
            GetString(row.NameStringId),
            GetString(row.QualifiedNameStringId),
            GetString(row.KindStringId),
            row.StartLine,
            row.StartColumn,
            row.EndLine,
            row.EndColumn,
            GetString(row.SignatureStringId),
            GetString(row.LanguageStringId),
            GetString(row.ModuleStringId),
            row.StructuralIdentityStringId < 0 ? null : GetString(row.StructuralIdentityStringId));
    });

    private RelationshipRecord MaterializeRelationship(int ordinal) => _relationships.GetOrAdd(ordinal, value =>
    {
        var row = ReadRelationshipRow(value);
        return new RelationshipRecord(
            GetString(row.IdStringId),
            Manifest.RepositoryId,
            GetString(row.SourceIdStringId),
            GetString(row.TargetIdStringId),
            GetString(row.KindStringId),
            GetFilePath(row.FileOrdinal),
            row.Line,
            row.Column,
            GetString(row.ConfidenceStringId),
            GetString(row.LanguageStringId),
            GetString(row.ModuleStringId),
            row.StructuralIdentityStringId < 0 ? null : GetString(row.StructuralIdentityStringId));
    });

    private string GetFilePath(int fileOrdinal) => GetString(ReadFileRow(fileOrdinal).PathStringId);

    private string GetString(int id)
    {
        if (id < 0) throw new InvalidDataException("Negative non-null string ID in snapshot pack.");
        return _strings.GetOrAdd(id, DecodeString);
    }

    private string DecodeString(int id)
    {
        if (_header is null || _file is null || (uint)id >= (uint)_header.StringCount)
            throw new InvalidDataException($"Invalid snapshot-pack string ID: {id}.");
        var offsets = Section(PackSectionKind.StringOffsets);
        var start = BinaryPrimitives.ReadInt64LittleEndian(_file.Slice(offsets.Offset + (long)id * sizeof(long), sizeof(long)));
        var end = BinaryPrimitives.ReadInt64LittleEndian(_file.Slice(offsets.Offset + (long)(id + 1) * sizeof(long), sizeof(long)));
        var bytes = Section(PackSectionKind.StringBytes);
        if (start < 0 || end < start || end > bytes.Length || end - start > _options.MaxStringBytes)
            throw new InvalidDataException("Invalid string bounds in snapshot pack.");
        return StrictUtf8.GetString(_file.Slice(bytes.Offset + start, checked((int)(end - start))));
    }

    private PackSectionDescriptor Section(PackSectionKind kind) =>
        _header?.Sections[kind] ?? throw new InvalidOperationException("Empty snapshot has no mapped sections.");

    private ReadOnlySpan<int> IntSection(PackSectionKind kind)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Snapshot packs currently require a little-endian runtime.");
        var descriptor = Section(kind);
        if (_file is null || descriptor.Length > int.MaxValue) throw new InvalidDataException("Invalid mapped integer section.");
        return MemoryMarshal.Cast<byte, int>(_file.Slice(descriptor.Offset, checked((int)descriptor.Length)));
    }

    private FileRow ReadFileRow(int ordinal)
    {
        var source = Row(PackSectionKind.Files, ordinal, PackLayout.FileRowSize);
        return new FileRow(ReadInt(source, 0), ReadInt(source, 4), ReadInt(source, 8), ReadInt(source, 12),
            ReadInt(source, 16), ReadInt(source, 20));
    }

    private SymbolRow ReadSymbolRow(int ordinal)
    {
        var source = Row(PackSectionKind.Symbols, ordinal, PackLayout.SymbolRowSize);
        return new SymbolRow(ReadInt(source, 0), ReadInt(source, 4), ReadInt(source, 8), ReadInt(source, 12),
            ReadInt(source, 16), ReadInt(source, 20), ReadInt(source, 24), ReadInt(source, 28), ReadInt(source, 32),
            ReadInt(source, 36), ReadInt(source, 40), ReadInt(source, 44), ReadInt(source, 48), ReadInt(source, 52),
            ReadInt(source, 56), ReadInt(source, 60));
    }

    private RelationshipRow ReadRelationshipRow(int ordinal)
    {
        var source = Row(PackSectionKind.Relationships, ordinal, PackLayout.RelationshipRowSize);
        return new RelationshipRow(ReadInt(source, 0), ReadInt(source, 4), ReadInt(source, 8), ReadInt(source, 12),
            ReadInt(source, 16), ReadInt(source, 20), ReadInt(source, 24), ReadInt(source, 28), ReadInt(source, 32),
            ReadInt(source, 36), ReadInt(source, 40), ReadInt(source, 44), ReadInt(source, 48), ReadInt(source, 52));
    }

    private LookupRow ReadLookupRow(PackSectionKind kind, int ordinal)
    {
        var source = Row(kind, ordinal, PackLayout.LookupRowSize);
        return new LookupRow(BinaryPrimitives.ReadUInt64LittleEndian(source), ReadInt(source, 8));
    }

    private LexemeRow ReadLexemeRow(int ordinal)
    {
        var source = Row(PackSectionKind.Lexemes, ordinal, PackLayout.LexemeRowSize);
        return new LexemeRow(ReadInt(source, 0), ReadInt(source, 4), ReadInt(source, 8), ReadInt(source, 12));
    }

    private LexemePosting ReadLexemePosting(int ordinal)
    {
        var source = Row(PackSectionKind.LexemePostings, ordinal, PackLayout.LexemePostingSize);
        return new LexemePosting(ReadInt(source, 0), BinaryPrimitives.ReadUInt16LittleEndian(source[4..]),
            (LexemeField)BinaryPrimitives.ReadUInt16LittleEndian(source[6..]));
    }

    private TrigramRow ReadTrigramRow(PackSectionKind kind, int ordinal)
    {
        var source = Row(kind, ordinal, PackLayout.TrigramRowSize);
        return new TrigramRow(BinaryPrimitives.ReadUInt32LittleEndian(source), ReadInt(source, 4), ReadInt(source, 8));
    }

    private ReadOnlySpan<byte> Row(PackSectionKind kind, int ordinal, int rowSize)
    {
        var descriptor = Section(kind);
        if ((uint)ordinal >= (uint)descriptor.Count || descriptor.RecordSize != rowSize || _file is null)
            throw new InvalidDataException($"Invalid {kind} row ordinal: {ordinal}.");
        return _file.Slice(descriptor.Offset + (long)ordinal * rowSize, rowSize);
    }

    private static int ReadInt(ReadOnlySpan<byte> source, int offset) => BinaryPrimitives.ReadInt32LittleEndian(source[offset..]);

    private void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0) return;
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _strings.Clear();
        _symbols.Clear();
        _relationships.Clear();
        _file?.Dispose();
    }

    internal sealed class SnapshotLease : IDisposable
    {
        private RepositorySnapshot? _snapshot;
        internal SnapshotLease(RepositorySnapshot snapshot) => _snapshot = snapshot;
        public RepositorySnapshot Snapshot => _snapshot ?? throw new ObjectDisposedException(nameof(SnapshotLease));
        public void Dispose() => Interlocked.Exchange(ref _snapshot, null)?.Release();
    }

    private readonly record struct GraphOrdinalResult(
        int RootOrdinal,
        int[] NodeOrdinals,
        int[] EdgeOrdinals,
        bool Truncated);

    private readonly record struct EdgeGroupKey(
        int SourceIdStringId,
        int TargetIdStringId,
        int KindStringId,
        int FileOrdinal);

    private sealed class EdgeGroupAccumulator
    {
        private readonly SortedSet<int> _lines = [];
        public int OccurrenceCount { get; private set; }
        public IReadOnlyList<int> Lines => _lines.ToArray();

        public void Add(int line)
        {
            OccurrenceCount++;
            if (_lines.Contains(line)) return;
            _lines.Add(line);
            if (_lines.Count > 8) _lines.Remove(_lines.Max);
        }
    }

    private readonly record struct SearchCandidate(int Ordinal, double Score);
    private readonly record struct FileRow(int PathStringId, int LanguageStringId, int OutlineStart, int OutlineCount, int SymbolCount, int Flags);
    private readonly record struct SymbolRow(
        int IdStringId, int ProjectStringId, int FileOrdinal, int NameStringId, int QualifiedNameStringId,
        int KindStringId, int SignatureStringId, int LanguageStringId, int ModuleStringId,
        int StartLine, int StartColumn, int EndLine, int EndColumn, int DocumentLength, int Flags,
        int StructuralIdentityStringId);
    private readonly record struct RelationshipRow(
        int IdStringId, int SourceIdStringId, int TargetIdStringId, int SourceOrdinal, int TargetOrdinal,
        int KindStringId, int FileOrdinal, int Line, int Column, int ConfidenceStringId,
        int LanguageStringId, int ModuleStringId, int Flags, int StructuralIdentityStringId);
    private readonly record struct LookupRow(ulong Hash, int Ordinal);
    private readonly record struct LexemeRow(int StringId, int PostingStart, int PostingCount, int DocumentFrequency);
    private readonly record struct LexemePosting(int SymbolOrdinal, ushort Frequency, LexemeField Fields);
    private readonly record struct TrigramRow(uint Hash, int PostingStart, int PostingCount);
}
