using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using MapRepo.Core;
using MapRepo.NativeStore.Internal.Kernel;
using MapRepo.NativeStore.Internal.Search;

namespace MapRepo.NativeStore.Internal.Packing;

internal static class SnapshotPackBuilder
{
    public static Task<SnapshotPackBuildResult> BuildTemporaryAsync(
        string repositoryId,
        long sequence,
        DateTimeOffset indexedAt,
        IRepositoryRecordSource? source,
        IReadOnlyCollection<FileModuleKey> removals,
        IReadOnlyDictionary<FileModuleKey, FileSegmentData> upserts,
        string temporaryDirectory,
        NativeStoreOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        Directory.CreateDirectory(temporaryDirectory);
        return Task.Run(() => Build(
            repositoryId,
            sequence,
            indexedAt,
            source,
            removals,
            upserts,
            temporaryDirectory,
            options,
            cancellationToken), cancellationToken);
    }

    private static SnapshotPackBuildResult Build(
        string repositoryId,
        long sequence,
        DateTimeOffset indexedAt,
        IRepositoryRecordSource? source,
        IReadOnlyCollection<FileModuleKey> removals,
        IReadOnlyDictionary<FileModuleKey, FileSegmentData> upserts,
        string temporaryDirectory,
        NativeStoreOptions options,
        CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(temporaryDirectory, $"snapshot-{sequence:D20}-{Guid.NewGuid():N}.tmp");
        try
        {
            var replacements = removals.Concat(upserts.Keys).ToHashSet();
            var symbolMap = new Dictionary<string, (SymbolRecord Record, FileModuleKey Owner)>(StringComparer.Ordinal);
            var relationshipMap = new Dictionary<string, (RelationshipRecord Record, FileModuleKey Owner)>(StringComparer.Ordinal);

            if (source is not null)
            {
                foreach (var raw in source.EnumerateSymbols(cancellationToken))
                {
                    var symbol = Normalize(raw);
                    var owner = FileModuleKey.Create(symbol.ModuleId, symbol.FilePath);
                    if (!replacements.Contains(owner)) AddSymbol(symbol, owner, preferNew: false);
                }
                foreach (var raw in source.EnumerateRelationships(cancellationToken))
                {
                    var relationship = Normalize(raw);
                    var owner = FileModuleKey.Create(relationship.ModuleId, relationship.FilePath);
                    if (!replacements.Contains(owner)) AddRelationship(relationship, owner, preferNew: false);
                }
            }

            foreach (var pair in upserts.OrderBy(static pair => pair.Key))
            {
                ValidateSegment(pair.Value, repositoryId, pair.Key);
                foreach (var raw in pair.Value.Symbols) AddSymbol(Normalize(raw), pair.Key, preferNew: true);
                foreach (var raw in pair.Value.Relationships) AddRelationship(Normalize(raw), pair.Key, preferNew: true);
            }

            var symbols = symbolMap.Values.Select(static value => value.Record)
                .OrderBy(static value => value.Id, StringComparer.Ordinal).ToArray();
            var relationships = relationshipMap.Values.Select(static value => value.Record)
                .OrderBy(static value => value.Id, StringComparer.Ordinal).ToArray();
            if ((long)symbols.Length + relationships.Length > options.MaxRecordsPerSnapshot)
                throw new InvalidDataException($"Snapshot exceeds MaxRecordsPerSnapshot ({options.MaxRecordsPerSnapshot:N0}).");

            var symbolOrdinalById = new Dictionary<string, int>(symbols.Length, StringComparer.Ordinal);
            for (var index = 0; index < symbols.Length; index++) symbolOrdinalById.Add(symbols[index].Id, index);

            var paths = symbols.Select(static value => value.FilePath)
                .Concat(relationships.Select(static value => value.FilePath))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var fileOrdinalByPath = new Dictionary<string, int>(paths.Length, StringComparer.Ordinal);
            for (var index = 0; index < paths.Length; index++) fileOrdinalByPath.Add(paths[index], index);

            var strings = new StringTableBuilder(options.MaxStringBytes);
            var symbolRows = new SymbolRow[symbols.Length];
            var lookupRows = new LookupRow[symbols.Length];
            var lexemeOccurrences = new List<LexemeOccurrence>(InitialCapacity(symbols.Length, 12));
            var trigramOccurrences = new List<UIntPair>(InitialCapacity(symbols.Length, 20));
            var outlineBuilders = new List<int>[paths.Length];
            var visibleSymbolCounts = new int[paths.Length];
            var languagesByFile = new SortedSet<string>[paths.Length];

            for (var ordinal = 0; ordinal < symbols.Length; ordinal++)
            {
                if ((ordinal & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var symbol = symbols[ordinal];
                var fileOrdinal = fileOrdinalByPath[symbol.FilePath];
                var terms = CodeTokenizer.WeightedTerms(symbol, out var documentLength);
                var lexemes = new Dictionary<int, LexemeAccumulator>();
                AddLexeme(CodeTokenizer.Normalize(symbol.Name), LexemeField.Name, 0);
                AddLexeme(CodeTokenizer.Normalize(symbol.QualifiedName), LexemeField.QualifiedName, 0);
                foreach (var term in terms) AddLexeme(term.Key, LexemeField.Token, term.Value);
                foreach (var pair in lexemes)
                    lexemeOccurrences.Add(new LexemeOccurrence(pair.Key, ordinal, pair.Value.Frequency, pair.Value.Fields));
                foreach (var hash in CodeTokenizer.TrigramHashes(symbol)) trigramOccurrences.Add(new UIntPair(hash, ordinal));

                if (!string.Equals(symbol.Kind, "textual-evidence", StringComparison.Ordinal))
                {
                    (outlineBuilders[fileOrdinal] ??= []).Add(ordinal);
                    visibleSymbolCounts[fileOrdinal]++;
                    (languagesByFile[fileOrdinal] ??= new SortedSet<string>(StringComparer.Ordinal)).Add(symbol.Language);
                }

                symbolRows[ordinal] = new SymbolRow(
                    strings.GetOrAdd(symbol.Id),
                    strings.GetOrAddNullable(symbol.Project),
                    fileOrdinal,
                    strings.GetOrAdd(symbol.Name),
                    strings.GetOrAdd(symbol.QualifiedName),
                    strings.GetOrAdd(symbol.Kind),
                    strings.GetOrAdd(symbol.Signature),
                    strings.GetOrAdd(symbol.Language),
                    strings.GetOrAdd(symbol.ModuleId),
                    symbol.StartLine,
                    symbol.StartColumn,
                    symbol.EndLine,
                    symbol.EndColumn,
                    documentLength,
                    string.Equals(symbol.Kind, "textual-evidence", StringComparison.Ordinal) ? 1 : 0,
                    strings.GetOrAddNullable(symbol.StructuralIdentity));
                lookupRows[ordinal] = new LookupRow(StableHash.String64(symbol.Id), ordinal);

                void AddLexeme(string value, LexemeField fields, ushort frequency)
                {
                    if (string.IsNullOrEmpty(value)) return;
                    var id = strings.GetOrAdd(value);
                    if (lexemes.TryGetValue(id, out var existing))
                        lexemes[id] = new LexemeAccumulator(
                            (ushort)Math.Min(ushort.MaxValue, existing.Frequency + frequency),
                            existing.Fields | fields);
                    else lexemes.Add(id, new LexemeAccumulator(frequency, fields));
                }
            }

            var fileRows = new FileRow[paths.Length];
            var outlineSymbols = new List<int>(symbols.Length);
            var fileTrigramOccurrences = new List<UIntPair>(InitialCapacity(paths.Length, 8));
            for (var fileOrdinal = 0; fileOrdinal < paths.Length; fileOrdinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outline = outlineBuilders[fileOrdinal] ?? [];
                outline.Sort((left, right) => CompareOutline(symbols[left], symbols[right]));
                var outlineStart = outlineSymbols.Count;
                outlineSymbols.AddRange(outline);
                var language = languagesByFile[fileOrdinal] is { Count: > 0 } languages
                    ? languages.Max ?? string.Empty
                    : string.Empty;
                var visible = visibleSymbolCounts[fileOrdinal] > 0;
                fileRows[fileOrdinal] = new FileRow(
                    strings.GetOrAdd(paths[fileOrdinal]),
                    strings.GetOrAdd(language),
                    outlineStart,
                    outline.Count,
                    visibleSymbolCounts[fileOrdinal],
                    visible ? 1 : 0);
                if (visible)
                {
                    foreach (var hash in CodeTokenizer.TrigramHashes(CodeTokenizer.Normalize(paths[fileOrdinal])).Distinct())
                        fileTrigramOccurrences.Add(new UIntPair(hash, fileOrdinal));
                }
            }

            var relationshipRows = new RelationshipRow[relationships.Length];
            var relationshipLookupRows = new LookupRow[relationships.Length];
            var outgoingCounts = new int[symbols.Length];
            var incomingCounts = new int[symbols.Length];
            var resolvedRelationships = 0;
            for (var ordinal = 0; ordinal < relationships.Length; ordinal++)
            {
                if ((ordinal & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var relationship = relationships[ordinal];
                var sourceOrdinal = symbolOrdinalById.GetValueOrDefault(relationship.SourceId, -1);
                var targetOrdinal = symbolOrdinalById.GetValueOrDefault(relationship.TargetId, -1);
                if (sourceOrdinal >= 0 && targetOrdinal >= 0)
                {
                    outgoingCounts[sourceOrdinal]++;
                    incomingCounts[targetOrdinal]++;
                    resolvedRelationships++;
                }
                relationshipRows[ordinal] = new RelationshipRow(
                    strings.GetOrAdd(relationship.Id),
                    strings.GetOrAdd(relationship.SourceId),
                    strings.GetOrAdd(relationship.TargetId),
                    sourceOrdinal,
                    targetOrdinal,
                    strings.GetOrAdd(relationship.Kind),
                    fileOrdinalByPath[relationship.FilePath],
                    relationship.Line,
                    relationship.Column,
                    strings.GetOrAdd(relationship.Confidence),
                    strings.GetOrAdd(relationship.Language),
                    strings.GetOrAdd(relationship.ModuleId),
                    sourceOrdinal >= 0 && targetOrdinal >= 0 ? 1 : 0,
                    strings.GetOrAddNullable(relationship.StructuralIdentity));
                relationshipLookupRows[ordinal] = new LookupRow(StableHash.String64(relationship.Id), ordinal);
            }

            var outgoingOffsets = PrefixSum(outgoingCounts);
            var incomingOffsets = PrefixSum(incomingCounts);
            var outgoingEdges = new int[resolvedRelationships];
            var incomingEdges = new int[resolvedRelationships];
            var outgoingCursor = outgoingOffsets[..^1].ToArray();
            var incomingCursor = incomingOffsets[..^1].ToArray();
            for (var ordinal = 0; ordinal < relationshipRows.Length; ordinal++)
            {
                var row = relationshipRows[ordinal];
                if (row.SourceOrdinal < 0 || row.TargetOrdinal < 0) continue;
                outgoingEdges[outgoingCursor[row.SourceOrdinal]++] = ordinal;
                incomingEdges[incomingCursor[row.TargetOrdinal]++] = ordinal;
            }

            Array.Sort(lookupRows, LookupRowComparer.Instance);
            Array.Sort(relationshipLookupRows, LookupRowComparer.Instance);
            var (lexemeRows, lexemePostings) = BuildLexemes(lexemeOccurrences, strings, cancellationToken);
            var (trigramRows, trigramPostings) = BuildTrigrams(trigramOccurrences, cancellationToken);
            var (fileTrigramRows, fileTrigramPostings) = BuildTrigrams(fileTrigramOccurrences, cancellationToken);
            var overviewBytes = BuildOverviewProjection(
                symbols,
                relationships,
                paths,
                fileRows,
                outgoingCounts,
                incomingCounts,
                strings);

            WritePack(
                temporaryPath,
                repositoryId,
                sequence,
                indexedAt,
                options,
                strings,
                overviewBytes,
                fileRows,
                symbolRows,
                relationshipRows,
                lookupRows,
                relationshipLookupRows,
                lexemeRows,
                lexemePostings,
                trigramRows,
                trigramPostings,
                fileTrigramRows,
                fileTrigramPostings,
                outlineSymbols.ToArray(),
                outgoingOffsets,
                outgoingEdges,
                incomingOffsets,
                incomingEdges,
                resolvedRelationships,
                symbolRows.Sum(static row => (long)row.DocumentLength),
                cancellationToken,
                out var length,
                out var rootChecksum);

            return new SnapshotPackBuildResult(temporaryPath, sequence, rootChecksum, length,
                symbols.Length, relationships.Length, resolvedRelationships, paths.Length);

            void AddSymbol(SymbolRecord symbol, FileModuleKey owner, bool preferNew)
            {
                ValidateRepository(symbol.RepositoryId, repositoryId, "symbol");
                if (symbolMap.TryGetValue(symbol.Id, out var existing))
                {
                    if (existing.Owner != owner && options.StrictIdentityValidation)
                        throw new DuplicateSymbolIdentityException(symbol.Id, existing.Owner.FilePath, owner.FilePath);
                    if (!EqualityComparer<SymbolRecord>.Default.Equals(existing.Record, symbol) && existing.Owner == owner)
                        throw new ConflictingRecordIdentityException("symbol", symbol.Id, owner.FilePath);
                    if (!preferNew) return;
                }
                symbolMap[symbol.Id] = (symbol, owner);
            }

            void AddRelationship(RelationshipRecord relationship, FileModuleKey owner, bool preferNew)
            {
                ValidateRepository(relationship.RepositoryId, repositoryId, "relationship");
                if (relationshipMap.TryGetValue(relationship.Id, out var existing))
                {
                    if (existing.Owner != owner && options.StrictIdentityValidation)
                        throw new DuplicateRelationshipIdentityException(relationship.Id, existing.Owner.FilePath, owner.FilePath);
                    if (!EqualityComparer<RelationshipRecord>.Default.Equals(existing.Record, relationship) && existing.Owner == owner)
                        throw new ConflictingRecordIdentityException("relationship", relationship.Id, owner.FilePath);
                    if (!preferNew) return;
                }
                relationshipMap[relationship.Id] = (relationship, owner);
            }
        }
        catch
        {
            try { File.Delete(temporaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static byte[] BuildOverviewProjection(
        SymbolRecord[] symbols,
        RelationshipRecord[] relationships,
        string[] paths,
        FileRow[] files,
        int[] outgoingCounts,
        int[] incomingCounts,
        StringTableBuilder strings)
    {
        var kinds = Count(symbols.Select(static value => value.Kind));
        var languages = Count(symbols.Select(static value => value.Language));
        var projects = Count(symbols.Select(static value => value.Project ?? "(none)"));
        var edgeKinds = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < relationships.Length; ordinal++)
            Increment(edgeKinds, relationships[ordinal].Kind);

        var degrees = new int[symbols.Length];
        for (var ordinal = 0; ordinal < symbols.Length; ordinal++)
            degrees[ordinal] = checked(outgoingCounts[ordinal] + incomingCounts[ordinal]);

        return OverviewSeedCodec.Encode(Create(includeGenerated: false), Create(includeGenerated: true));

        OverviewSeed Create(bool includeGenerated)
        {
            var topFiles = Enumerable.Range(0, files.Length)
                .Where(ordinal => (files[ordinal].Flags & 1) != 0)
                .Where(ordinal => includeGenerated || !IsGeneratedPath(paths[ordinal]))
                .Select(ordinal => new OverviewFileSeed(ordinal, files[ordinal].SymbolCount))
                .OrderByDescending(static value => value.Count)
                .ThenBy(value => paths[value.FileOrdinal], StringComparer.Ordinal)
                .Take(20)
                .ToImmutableArray();
            var hubs = Enumerable.Range(0, symbols.Length)
                .Where(ordinal => degrees[ordinal] > 0)
                .Where(ordinal => !string.Equals(symbols[ordinal].Kind, "textual-evidence", StringComparison.Ordinal))
                .Where(ordinal => includeGenerated || !IsGeneratedPath(symbols[ordinal].FilePath))
                .OrderByDescending(ordinal => degrees[ordinal])
                .ThenBy(ordinal => ordinal)
                .Take(20)
                .Select(ordinal => new OverviewHubSeed(ordinal, degrees[ordinal]))
                .ToImmutableArray();
            return new OverviewSeed(
                symbols.Length,
                relationships.Length,
                Top(kinds, 30),
                Top(languages, 12),
                Top(projects, 30),
                Top(edgeKinds, 12),
                topFiles,
                hubs);
        }

        ImmutableArray<OverviewCountSeed> Top(Dictionary<string, int> source, int limit) => source
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Take(limit)
            .Select(pair => new OverviewCountSeed(strings.GetOrAdd(pair.Key), pair.Value))
            .ToImmutableArray();

        static Dictionary<string, int> Count(IEnumerable<string> values)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var value in values) Increment(result, value);
            return result;
        }

        static void Increment(Dictionary<string, int> target, string key) =>
            target[key] = target.GetValueOrDefault(key) + 1;
    }

    private static bool IsGeneratedPath(string filePath) =>
        filePath.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".pb.cs", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase) ||
        filePath.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
        filePath.Contains("/generated/", StringComparison.OrdinalIgnoreCase);

    private static void WritePack(
        string path,
        string repositoryId,
        long sequence,
        DateTimeOffset indexedAt,
        NativeStoreOptions options,
        StringTableBuilder strings,
        byte[] overviewBytes,
        FileRow[] files,
        SymbolRow[] symbols,
        RelationshipRow[] relationships,
        LookupRow[] symbolLookup,
        LookupRow[] relationshipLookup,
        LexemeRow[] lexemes,
        LexemePosting[] lexemePostings,
        TrigramRow[] trigrams,
        int[] trigramPostings,
        TrigramRow[] fileTrigrams,
        int[] fileTrigramPostings,
        int[] outlineSymbols,
        int[] outgoingOffsets,
        int[] outgoingEdges,
        int[] incomingOffsets,
        int[] incomingEdges,
        int resolvedRelationships,
        long totalDocumentLength,
        CancellationToken cancellationToken,
        out long length,
        out uint rootChecksum)
    {
        var fileOptions = FileOptions.SequentialScan;
        if (options.WriteThrough) fileOptions |= FileOptions.WriteThrough;
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read,
            Options = fileOptions,
            BufferSize = 128 * 1024
        });
        stream.SetLength(PackLayout.HeaderSize);
        stream.Position = PackLayout.HeaderSize;
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var sections = new List<PackSectionDescriptor>();
        var stringOffsets = strings.BuildUtf8Offsets(out var stringByteLength);

        AddSection(PackSectionKind.StringOffsets, stringOffsets.Length, sizeof(long), () =>
        {
            foreach (var value in stringOffsets) writer.Write(value);
        });
        AddSection(PackSectionKind.StringBytes, stringByteLength, sizeof(byte), () =>
            WriteUtf8Strings(stream, writer, strings.Values, cancellationToken));
        AddSection(PackSectionKind.Overview, overviewBytes.Length, sizeof(byte), () => writer.Write(overviewBytes));
        AddSection(PackSectionKind.Files, files.Length, PackLayout.FileRowSize, () =>
        {
            foreach (var row in files) row.Write(writer);
        });
        AddSection(PackSectionKind.Symbols, symbols.Length, PackLayout.SymbolRowSize, () =>
        {
            foreach (var row in symbols) row.Write(writer);
        });
        AddSection(PackSectionKind.Relationships, relationships.Length, PackLayout.RelationshipRowSize, () =>
        {
            foreach (var row in relationships) row.Write(writer);
        });
        AddSection(PackSectionKind.SymbolLookup, symbolLookup.Length, PackLayout.LookupRowSize, () =>
        {
            foreach (var row in symbolLookup) row.Write(writer);
        });
        AddSection(PackSectionKind.RelationshipLookup, relationshipLookup.Length, PackLayout.LookupRowSize, () =>
        {
            foreach (var row in relationshipLookup) row.Write(writer);
        });
        AddSection(PackSectionKind.Lexemes, lexemes.Length, PackLayout.LexemeRowSize, () =>
        {
            foreach (var row in lexemes) row.Write(writer);
        });
        AddSection(PackSectionKind.LexemePostings, lexemePostings.Length, PackLayout.LexemePostingSize, () =>
        {
            foreach (var row in lexemePostings) row.Write(writer);
        });
        AddSection(PackSectionKind.Trigrams, trigrams.Length, PackLayout.TrigramRowSize, () =>
        {
            foreach (var row in trigrams) row.Write(writer);
        });
        AddIntSection(PackSectionKind.TrigramPostings, trigramPostings);
        AddSection(PackSectionKind.FileTrigrams, fileTrigrams.Length, PackLayout.TrigramRowSize, () =>
        {
            foreach (var row in fileTrigrams) row.Write(writer);
        });
        AddIntSection(PackSectionKind.FileTrigramPostings, fileTrigramPostings);
        AddIntSection(PackSectionKind.OutlineSymbols, outlineSymbols);
        AddIntSection(PackSectionKind.OutgoingOffsets, outgoingOffsets);
        AddIntSection(PackSectionKind.OutgoingEdges, outgoingEdges);
        AddIntSection(PackSectionKind.IncomingOffsets, incomingOffsets);
        AddIntSection(PackSectionKind.IncomingEdges, incomingEdges);

        writer.Flush();
        stream.Flush();
        length = stream.Length;
        if (length > options.MaxSnapshotPackBytes)
            throw new IOException($"Snapshot pack exceeds MaxSnapshotPackBytes: {length:N0} bytes.");

        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            for (var index = 0; index < sections.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var descriptor = sections[index];
                var state = Crc32C.Begin();
                stream.Position = descriptor.Offset;
                var remaining = descriptor.Length;
                while (remaining > 0)
                {
                    var requested = (int)Math.Min(buffer.Length, remaining);
                    var read = stream.Read(buffer, 0, requested);
                    if (read == 0) throw new EndOfStreamException("Snapshot pack changed while computing section checksums.");
                    state.Append(buffer.AsSpan(0, read));
                    remaining -= read;
                }
                sections[index] = descriptor with { Checksum = state.Finish() };
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }

        var (header, root) = PackHeaderCodec.Encode(repositoryId, sequence, length, symbols.Length,
            relationships.Length, resolvedRelationships, files.Length, strings.Count, totalDocumentLength, indexedAt, sections);
        stream.Position = 0;
        stream.Write(header);
        writer.Flush();
        options.FaultInjector?.Hit(StoreFaultPoint.AfterSnapshotBytesWritten, new StoreFaultContext(repositoryId, sequence));
        stream.Flush();
        if (options.FlushToDisk) stream.Flush(flushToDisk: true);
        options.FaultInjector?.Hit(StoreFaultPoint.AfterSnapshotDurable, new StoreFaultContext(repositoryId, sequence));
        rootChecksum = root;

        void AddIntSection(PackSectionKind kind, int[] values) =>
            AddSection(kind, values.Length, sizeof(int), () =>
            {
                foreach (var value in values) writer.Write(value);
            });

        void AddSection(PackSectionKind kind, int count, int recordSize, Action write)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Align(stream, sizeof(long));
            var offset = stream.Position;
            write();
            writer.Flush();
            var sectionLength = stream.Position - offset;
            var expected = (long)count * recordSize;
            if (sectionLength != expected)
                throw new InvalidDataException($"Writer produced {sectionLength} bytes for {kind}; expected {expected}.");
            sections.Add(new PackSectionDescriptor(kind, offset, sectionLength, count, recordSize, 0));
        }
    }

    private static (LexemeRow[] Rows, LexemePosting[] Postings) BuildLexemes(
        List<LexemeOccurrence> occurrences,
        StringTableBuilder strings,
        CancellationToken cancellationToken)
    {
        occurrences.Sort((left, right) =>
        {
            var text = StringComparer.Ordinal.Compare(strings[left.LexemeStringId], strings[right.LexemeStringId]);
            return text != 0 ? text : left.SymbolOrdinal.CompareTo(right.SymbolOrdinal);
        });
        var rows = new List<LexemeRow>();
        var postings = new List<LexemePosting>(occurrences.Count);
        var index = 0;
        while (index < occurrences.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lexemeId = occurrences[index].LexemeStringId;
            var start = postings.Count;
            while (index < occurrences.Count && occurrences[index].LexemeStringId == lexemeId)
            {
                var symbol = occurrences[index].SymbolOrdinal;
                ushort frequency = 0;
                var fields = LexemeField.None;
                while (index < occurrences.Count && occurrences[index].LexemeStringId == lexemeId && occurrences[index].SymbolOrdinal == symbol)
                {
                    frequency = (ushort)Math.Min(ushort.MaxValue, frequency + occurrences[index].Frequency);
                    fields |= occurrences[index].Fields;
                    index++;
                }
                postings.Add(new LexemePosting(symbol, frequency, fields));
            }
            rows.Add(new LexemeRow(lexemeId, start, postings.Count - start, postings.Count - start));
        }
        return (rows.ToArray(), postings.ToArray());
    }

    private static (TrigramRow[] Rows, int[] Postings) BuildTrigrams(
        List<UIntPair> occurrences,
        CancellationToken cancellationToken)
    {
        occurrences.Sort(UIntPairComparer.Instance);
        var rows = new List<TrigramRow>();
        var postings = new List<int>(occurrences.Count);
        var index = 0;
        while (index < occurrences.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = occurrences[index].Key;
            var start = postings.Count;
            var previous = -1;
            while (index < occurrences.Count && occurrences[index].Key == hash)
            {
                var value = occurrences[index++].Value;
                if (value == previous) continue;
                postings.Add(value);
                previous = value;
            }
            rows.Add(new TrigramRow(hash, start, postings.Count - start));
        }
        return (rows.ToArray(), postings.ToArray());
    }

    private static int[] PrefixSum(int[] counts)
    {
        var offsets = new int[counts.Length + 1];
        var total = 0;
        for (var index = 0; index < counts.Length; index++)
        {
            offsets[index] = total;
            total = checked(total + counts[index]);
        }
        offsets[^1] = total;
        return offsets;
    }

    private static int InitialCapacity(int count, int factor) =>
        checked((int)Math.Min(1_000_000L, Math.Max(16L, (long)count * factor)));

    private static void WriteUtf8Strings(
        Stream stream,
        BinaryWriter writer,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        writer.Flush();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var encoder = Encoding.UTF8.GetEncoder();
            foreach (var value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (value.Length == 0) continue;
                encoder.Reset();
                var remaining = value.AsSpan();
                while (!remaining.IsEmpty)
                {
                    encoder.Convert(
                        remaining,
                        buffer,
                        flush: true,
                        out var charsUsed,
                        out var bytesUsed,
                        out var completed);
                    if (charsUsed == 0 && bytesUsed == 0)
                        throw new InvalidDataException("UTF-8 encoder made no progress while writing the string table.");
                    stream.Write(buffer, 0, bytesUsed);
                    remaining = remaining[charsUsed..];
                    if (completed) break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int CompareOutline(SymbolRecord left, SymbolRecord right)
    {
        var value = left.StartLine.CompareTo(right.StartLine);
        if (value != 0) return value;
        value = left.StartColumn.CompareTo(right.StartColumn);
        return value != 0 ? value : StringComparer.Ordinal.Compare(left.Id, right.Id);
    }

    private static void Align(Stream stream, int alignment)
    {
        var remainder = stream.Position % alignment;
        if (remainder == 0) return;
        var padding = checked((int)(alignment - remainder));
        stream.Write(new byte[padding]);
    }

    private static void ValidateRepository(string actual, string expected, string kind)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"A {kind} belongs to another repository.");
    }

    private static void ValidateSegment(FileSegmentData data, string repositoryId, FileModuleKey expectedKey)
    {
        if (data.Key != expectedKey) throw new InvalidDataException("Segment dictionary key differs from its payload key.");
        foreach (var symbol in data.Symbols)
        {
            ValidateRepository(symbol.RepositoryId, repositoryId, "symbol");
            if (FileModuleKey.Create(symbol.ModuleId, symbol.FilePath) != expectedKey)
                throw new InvalidDataException("A symbol does not match its file segment key.");
        }
        foreach (var relationship in data.Relationships)
        {
            ValidateRepository(relationship.RepositoryId, repositoryId, "relationship");
            if (FileModuleKey.Create(relationship.ModuleId, relationship.FilePath) != expectedKey)
                throw new InvalidDataException("A relationship does not match its file segment key.");
        }
    }

    private static SymbolRecord Normalize(SymbolRecord value)
    {
        var path = FileModuleKey.NormalizePath(value.FilePath);
        return string.Equals(path, value.FilePath, StringComparison.Ordinal) ? value : value with { FilePath = path };
    }

    private static RelationshipRecord Normalize(RelationshipRecord value)
    {
        var path = FileModuleKey.NormalizePath(value.FilePath);
        return string.Equals(path, value.FilePath, StringComparison.Ordinal) ? value : value with { FilePath = path };
    }

    private sealed class StringTableBuilder
    {
        private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
        private readonly List<string> _values = [];
        private readonly int _maxStringBytes;

        public StringTableBuilder(int maxStringBytes) => _maxStringBytes = maxStringBytes;

        public int Count => _values.Count;
        public IReadOnlyList<string> Values => _values;
        public string this[int index] => _values[index];

        public int GetOrAddNullable(string? value) => value is null ? -1 : GetOrAdd(value);

        public int GetOrAdd(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_ids.TryGetValue(value, out var id)) return id;
            var byteCount = Encoding.UTF8.GetByteCount(value);
            if (byteCount > _maxStringBytes)
                throw new InvalidDataException($"A snapshot-pack string exceeds MaxStringBytes ({byteCount:N0} > {_maxStringBytes:N0}).");
            id = _values.Count;
            _values.Add(value);
            _ids.Add(value, id);
            return id;
        }

        public long[] BuildUtf8Offsets(out int byteLength)
        {
            var offsets = new long[_values.Count + 1];
            long total = 0;
            for (var index = 0; index < _values.Count; index++)
            {
                offsets[index] = total;
                total = checked(total + Encoding.UTF8.GetByteCount(_values[index]));
                if (total > int.MaxValue) throw new IOException("Snapshot string table exceeds the supported 2 GiB section size.");
            }
            offsets[^1] = total;
            byteLength = checked((int)total);
            return offsets;
        }
    }

    private readonly record struct LexemeAccumulator(ushort Frequency, LexemeField Fields);
    private readonly record struct LexemeOccurrence(int LexemeStringId, int SymbolOrdinal, ushort Frequency, LexemeField Fields);
    private readonly record struct UIntPair(uint Key, int Value);

    private sealed class UIntPairComparer : IComparer<UIntPair>
    {
        public static UIntPairComparer Instance { get; } = new();
        public int Compare(UIntPair left, UIntPair right)
        {
            var hash = left.Key.CompareTo(right.Key);
            return hash != 0 ? hash : left.Value.CompareTo(right.Value);
        }
    }

    private readonly record struct LookupRow(ulong Hash, int Ordinal)
    {
        public void Write(BinaryWriter writer)
        {
            writer.Write(Hash);
            writer.Write(Ordinal);
            writer.Write(0);
        }
    }

    private sealed class LookupRowComparer : IComparer<LookupRow>
    {
        public static LookupRowComparer Instance { get; } = new();
        public int Compare(LookupRow left, LookupRow right)
        {
            var hash = left.Hash.CompareTo(right.Hash);
            return hash != 0 ? hash : left.Ordinal.CompareTo(right.Ordinal);
        }
    }

    private readonly record struct FileRow(
        int PathStringId,
        int LanguageStringId,
        int OutlineStart,
        int OutlineCount,
        int SymbolCount,
        int Flags)
    {
        public void Write(BinaryWriter writer)
        {
            writer.Write(PathStringId);
            writer.Write(LanguageStringId);
            writer.Write(OutlineStart);
            writer.Write(OutlineCount);
            writer.Write(SymbolCount);
            writer.Write(Flags);
            writer.Write(0);
            writer.Write(0);
        }
    }

    private readonly record struct SymbolRow(
        int IdStringId,
        int ProjectStringId,
        int FileOrdinal,
        int NameStringId,
        int QualifiedNameStringId,
        int KindStringId,
        int SignatureStringId,
        int LanguageStringId,
        int ModuleStringId,
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn,
        int DocumentLength,
        int Flags,
        int StructuralIdentityStringId)
    {
        public void Write(BinaryWriter writer)
        {
            writer.Write(IdStringId);
            writer.Write(ProjectStringId);
            writer.Write(FileOrdinal);
            writer.Write(NameStringId);
            writer.Write(QualifiedNameStringId);
            writer.Write(KindStringId);
            writer.Write(SignatureStringId);
            writer.Write(LanguageStringId);
            writer.Write(ModuleStringId);
            writer.Write(StartLine);
            writer.Write(StartColumn);
            writer.Write(EndLine);
            writer.Write(EndColumn);
            writer.Write(DocumentLength);
            writer.Write(Flags);
            writer.Write(StructuralIdentityStringId);
        }
    }

    private readonly record struct RelationshipRow(
        int IdStringId,
        int SourceIdStringId,
        int TargetIdStringId,
        int SourceOrdinal,
        int TargetOrdinal,
        int KindStringId,
        int FileOrdinal,
        int Line,
        int Column,
        int ConfidenceStringId,
        int LanguageStringId,
        int ModuleStringId,
        int Flags,
        int StructuralIdentityStringId)
    {
        public void Write(BinaryWriter writer)
        {
            writer.Write(IdStringId);
            writer.Write(SourceIdStringId);
            writer.Write(TargetIdStringId);
            writer.Write(SourceOrdinal);
            writer.Write(TargetOrdinal);
            writer.Write(KindStringId);
            writer.Write(FileOrdinal);
            writer.Write(Line);
            writer.Write(Column);
            writer.Write(ConfidenceStringId);
            writer.Write(LanguageStringId);
            writer.Write(ModuleStringId);
            writer.Write(Flags);
            writer.Write(StructuralIdentityStringId);
        }
    }

    private readonly record struct LexemeRow(int StringId, int PostingStart, int PostingCount, int DocumentFrequency)
    {
        public void Write(BinaryWriter writer)
        {
            writer.Write(StringId);
            writer.Write(PostingStart);
            writer.Write(PostingCount);
            writer.Write(DocumentFrequency);
        }
    }

    private readonly record struct LexemePosting(int SymbolOrdinal, ushort Frequency, LexemeField Fields)
    {
        public void Write(BinaryWriter writer)
        {
            writer.Write(SymbolOrdinal);
            writer.Write(Frequency);
            writer.Write((ushort)Fields);
        }
    }

    private readonly record struct TrigramRow(uint Hash, int PostingStart, int PostingCount)
    {
        public void Write(BinaryWriter writer)
        {
            writer.Write(Hash);
            writer.Write(PostingStart);
            writer.Write(PostingCount);
            writer.Write(0);
        }
    }
}
