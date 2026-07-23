using System.Buffers.Binary;
using MapRepo.NativeStore.Internal.Kernel;

namespace MapRepo.NativeStore.Internal.Packing;

internal static class PackHeaderCodec
{
    private static readonly byte[] Magic = "MRPACK02"u8.ToArray();
    private const int Version = 2;
    private const int RootChecksumOffset = 32;
    private const int DirectoryOffset = 128;
    private const int MaxDirectoryEntries = (PackLayout.HeaderSize - DirectoryOffset) / PackLayout.DirectoryEntrySize;

    public static (byte[] Header, uint RootChecksum) Encode(
        string repositoryId,
        long sequence,
        long length,
        int symbolCount,
        int relationshipCount,
        int resolvedRelationshipCount,
        int fileCount,
        int stringCount,
        long totalDocumentLength,
        DateTimeOffset indexedAt,
        IReadOnlyList<PackSectionDescriptor> sections)
    {
        if (sections.Count * PackLayout.DirectoryEntrySize > PackLayout.HeaderSize - DirectoryOffset)
            throw new InvalidOperationException("Snapshot pack has too many sections for the fixed header.");
        var header = new byte[PackLayout.HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), Version);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), PackLayout.HeaderSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16, 8), sequence);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24, 8), length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(36, 4), sections.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40, 4), symbolCount);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(44, 4), relationshipCount);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(48, 4), resolvedRelationshipCount);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(52, 4), fileCount);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(56, 4), stringCount);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(64, 8), indexedAt.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(72, 8), StableHash.String64(repositoryId));
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(80, 8), totalDocumentLength);

        for (var index = 0; index < sections.Count; index++)
        {
            var value = sections[index];
            var target = header.AsSpan(DirectoryOffset + index * PackLayout.DirectoryEntrySize, PackLayout.DirectoryEntrySize);
            BinaryPrimitives.WriteInt32LittleEndian(target, (int)value.Kind);
            BinaryPrimitives.WriteInt64LittleEndian(target[8..], value.Offset);
            BinaryPrimitives.WriteInt64LittleEndian(target[16..], value.Length);
            BinaryPrimitives.WriteInt32LittleEndian(target[24..], value.Count);
            BinaryPrimitives.WriteInt32LittleEndian(target[28..], value.RecordSize);
            BinaryPrimitives.WriteUInt32LittleEndian(target[32..], value.Checksum);
        }

        var root = ComputeRootChecksum(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(RootChecksumOffset, sizeof(uint)), root);
        return (header, root);
    }

    /// <summary>
    /// Validates only the fixed header and section directory. This is the metadata/status path and
    /// deliberately avoids mapping or reading the body of a potentially multi-gigabyte pack.
    /// </summary>
    public static SnapshotPackHeader DecodeHeader(
        ReadOnlySpan<byte> header,
        long actualFileLength,
        string repositoryId,
        SnapshotDescriptor expected,
        NativeStoreOptions options)
    {
        if (header.Length != PackLayout.HeaderSize)
            throw new InvalidDataException("Snapshot-pack header is truncated.");
        if (!header[..Magic.Length].SequenceEqual(Magic)) throw new InvalidDataException("Invalid snapshot-pack magic.");
        if (BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != Version)
            throw new InvalidDataException("Unsupported snapshot-pack format.");
        if (BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != PackLayout.HeaderSize)
            throw new InvalidDataException("Invalid snapshot-pack header size.");

        var sequence = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);
        var length = BinaryPrimitives.ReadInt64LittleEndian(header[24..]);
        var root = BinaryPrimitives.ReadUInt32LittleEndian(header[RootChecksumOffset..]);
        if (root != ComputeRootChecksum(header)) throw new InvalidDataException("Snapshot-pack root checksum mismatch.");
        if (sequence != expected.Sequence || length != expected.Length || length != actualFileLength || root != expected.RootChecksum)
            throw new InvalidDataException("Snapshot-pack identity differs from the active manifest.");
        if (BinaryPrimitives.ReadUInt64LittleEndian(header[72..]) != StableHash.String64(repositoryId))
            throw new InvalidDataException("Snapshot pack belongs to another repository.");

        var sectionCount = ReadCount(header, 36, MaxDirectoryEntries, "section");
        var symbolCount = ReadCount(header, 40, options.MaxRecordsPerSnapshot, "symbol");
        var relationshipCount = ReadCount(header, 44, options.MaxRecordsPerSnapshot, "relationship");
        if ((long)symbolCount + relationshipCount > options.MaxRecordsPerSnapshot)
            throw new InvalidDataException("Snapshot pack exceeds MaxRecordsPerSnapshot.");
        var resolvedCount = ReadCount(header, 48, relationshipCount, "resolved relationship");
        var fileCount = ReadCount(header, 52, 10_000_000, "file");
        var stringCount = ReadCount(header, 56, 100_000_000, "string");
        var totalDocumentLength = BinaryPrimitives.ReadInt64LittleEndian(header[80..]);
        if (totalDocumentLength < 0) throw new InvalidDataException("Invalid total document length.");
        if (symbolCount != expected.SymbolCount || relationshipCount != expected.RelationshipCount ||
            resolvedCount != expected.ResolvedRelationshipCount || fileCount != expected.FileCount)
            throw new InvalidDataException("Snapshot-pack counts differ from the active manifest.");

        DateTimeOffset indexedAt;
        try { indexedAt = DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64LittleEndian(header[64..])); }
        catch (ArgumentOutOfRangeException ex) { throw new InvalidDataException("Invalid snapshot-pack timestamp.", ex); }

        var sections = new Dictionary<PackSectionKind, PackSectionDescriptor>();
        var ranges = new List<(long Start, long End, PackSectionKind Kind)>();
        for (var index = 0; index < sectionCount; index++)
        {
            var source = header.Slice(DirectoryOffset + index * PackLayout.DirectoryEntrySize, PackLayout.DirectoryEntrySize);
            var kindValue = BinaryPrimitives.ReadInt32LittleEndian(source);
            if (!Enum.IsDefined(typeof(PackSectionKind), kindValue)) throw new InvalidDataException("Unknown snapshot-pack section.");
            var descriptor = new PackSectionDescriptor(
                (PackSectionKind)kindValue,
                BinaryPrimitives.ReadInt64LittleEndian(source[8..]),
                BinaryPrimitives.ReadInt64LittleEndian(source[16..]),
                BinaryPrimitives.ReadInt32LittleEndian(source[24..]),
                BinaryPrimitives.ReadInt32LittleEndian(source[28..]),
                BinaryPrimitives.ReadUInt32LittleEndian(source[32..]));
            ValidateSection(descriptor, actualFileLength);
            if (!sections.TryAdd(descriptor.Kind, descriptor)) throw new InvalidDataException("Duplicate snapshot-pack section.");
            ranges.Add((descriptor.Offset, descriptor.Offset + descriptor.Length, descriptor.Kind));
        }

        var ordered = ranges.OrderBy(static value => value.Start).ToArray();
        for (var index = 1; index < ordered.Length; index++)
            if (ordered[index - 1].End > ordered[index].Start) throw new InvalidDataException("Overlapping snapshot-pack sections.");
        foreach (var required in Enum.GetValues<PackSectionKind>())
            if (!sections.ContainsKey(required)) throw new InvalidDataException($"Snapshot pack is missing section {required}.");

        ValidateShape(sections, symbolCount, relationshipCount, fileCount, stringCount, resolvedCount);
        return new SnapshotPackHeader(sequence, length, root, symbolCount, relationshipCount, resolvedCount,
            fileCount, stringCount, totalDocumentLength, indexedAt, sections);
    }

    public static SnapshotPackHeader Decode(
        IReadOnlyPackFile file,
        string repositoryId,
        SnapshotDescriptor expected,
        NativeStoreOptions options,
        bool verifySections)
    {
        var result = DecodeHeader(file.Slice(0, PackLayout.HeaderSize), file.Length, repositoryId, expected, options);
        if (!verifySections) return result;

        foreach (var descriptor in result.Sections.Values.OrderBy(static value => value.Offset))
        {
            var state = Crc32C.Begin();
            var remaining = descriptor.Length;
            var offset = descriptor.Offset;
            while (remaining > 0)
            {
                var chunk = checked((int)Math.Min(remaining, 16L * 1024 * 1024));
                state.Append(file.Slice(offset, chunk));
                offset += chunk;
                remaining -= chunk;
            }
            if (state.Finish() != descriptor.Checksum)
                throw new InvalidDataException($"CRC32C mismatch in snapshot-pack section {descriptor.Kind}.");
        }
        return result;
    }

    private static uint ComputeRootChecksum(ReadOnlySpan<byte> header)
    {
        var state = Crc32C.Begin();
        state.Append(header[..RootChecksumOffset]);
        state.Append(header[(RootChecksumOffset + sizeof(uint))..]);
        return state.Finish();
    }

    private static int ReadCount(ReadOnlySpan<byte> header, int offset, int maximum, string label)
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(header[offset..]);
        if (value < 0 || value > maximum) throw new InvalidDataException($"Invalid snapshot-pack {label} count: {value}.");
        return value;
    }

    private static void ValidateSection(PackSectionDescriptor descriptor, long fileLength)
    {
        if (descriptor.Offset < PackLayout.HeaderSize || descriptor.Length < 0 || descriptor.Offset > fileLength - descriptor.Length)
            throw new InvalidDataException($"Invalid bounds for snapshot-pack section {descriptor.Kind}.");
        if (descriptor.Count < 0 || descriptor.RecordSize <= 0)
            throw new InvalidDataException($"Invalid shape for snapshot-pack section {descriptor.Kind}.");
        if ((long)descriptor.Count * descriptor.RecordSize != descriptor.Length)
            throw new InvalidDataException($"Length/count mismatch in snapshot-pack section {descriptor.Kind}.");
    }

    private static void ValidateShape(
        IReadOnlyDictionary<PackSectionKind, PackSectionDescriptor> sections,
        int symbolCount,
        int relationshipCount,
        int fileCount,
        int stringCount,
        int resolvedRelationshipCount)
    {
        Expect(PackSectionKind.StringOffsets, stringCount + 1, sizeof(long));
        ExpectSize(PackSectionKind.StringBytes, sizeof(byte));
        Expect(PackSectionKind.Files, fileCount, PackLayout.FileRowSize);
        Expect(PackSectionKind.Symbols, symbolCount, PackLayout.SymbolRowSize);
        Expect(PackSectionKind.Relationships, relationshipCount, PackLayout.RelationshipRowSize);
        Expect(PackSectionKind.SymbolLookup, symbolCount, PackLayout.LookupRowSize);
        Expect(PackSectionKind.RelationshipLookup, relationshipCount, PackLayout.LookupRowSize);
        ExpectSize(PackSectionKind.Lexemes, PackLayout.LexemeRowSize);
        ExpectSize(PackSectionKind.LexemePostings, PackLayout.LexemePostingSize);
        ExpectSize(PackSectionKind.Trigrams, PackLayout.TrigramRowSize);
        ExpectSize(PackSectionKind.TrigramPostings, sizeof(int));
        ExpectSize(PackSectionKind.FileTrigrams, PackLayout.TrigramRowSize);
        ExpectSize(PackSectionKind.FileTrigramPostings, sizeof(int));
        ExpectSize(PackSectionKind.OutlineSymbols, sizeof(int));
        Expect(PackSectionKind.OutgoingOffsets, symbolCount + 1, sizeof(int));
        Expect(PackSectionKind.IncomingOffsets, symbolCount + 1, sizeof(int));
        Expect(PackSectionKind.OutgoingEdges, resolvedRelationshipCount, sizeof(int));
        Expect(PackSectionKind.IncomingEdges, resolvedRelationshipCount, sizeof(int));
        var overview = sections[PackSectionKind.Overview];
        if (overview.RecordSize != sizeof(byte) || overview.Count <= 0)
            throw new InvalidDataException("Unexpected shape for snapshot-pack overview section.");

        void Expect(PackSectionKind kind, int count, int size)
        {
            var value = sections[kind];
            if (value.Count != count || value.RecordSize != size)
                throw new InvalidDataException($"Unexpected shape for snapshot-pack section {kind}.");
        }

        void ExpectSize(PackSectionKind kind, int size)
        {
            if (sections[kind].RecordSize != size)
                throw new InvalidDataException($"Unexpected record size for snapshot-pack section {kind}.");
        }
    }
}
