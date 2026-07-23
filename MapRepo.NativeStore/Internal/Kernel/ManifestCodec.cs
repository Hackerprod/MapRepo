using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace MapRepo.NativeStore.Internal.Kernel;

internal static class ManifestCodec
{
    private static readonly byte[] Magic = "MRMAN001"u8.ToArray();
    private const int CurrentFormatVersion = 2;

    public static (byte[] Bytes, uint Checksum) Encode(StoreManifest manifest)
    {
        using var stream = new MemoryStream(4096);
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(CurrentFormatVersion);
            writer.Write(manifest.Sequence);
            BinaryCodec.WriteString(writer, manifest.RepositoryId);
            BinaryCodec.WriteString(writer, manifest.Generation);
            writer.Write(manifest.IndexedAt.ToUnixTimeMilliseconds());
            writer.Write(manifest.Diagnostics.Length);
            foreach (var diagnostic in manifest.Diagnostics) BinaryCodec.WriteString(writer, diagnostic);

            writer.Write(manifest.Snapshot is not null);
            if (manifest.Snapshot is { } snapshot)
            {
                BinaryCodec.WriteString(writer, snapshot.FileName);
                writer.Write(snapshot.Sequence);
                writer.Write(snapshot.RootChecksum);
                writer.Write(snapshot.Length);
                writer.Write(snapshot.SymbolCount);
                writer.Write(snapshot.RelationshipCount);
                writer.Write(snapshot.ResolvedRelationshipCount);
                writer.Write(snapshot.FileCount);
            }

            writer.Write(manifest.ActiveSegments.Count);
            foreach (var descriptor in manifest.ActiveSegments.Values.OrderBy(static value => value.Key))
            {
                BinaryCodec.WriteString(writer, descriptor.Key.ModuleId);
                BinaryCodec.WriteString(writer, descriptor.Key.FilePath);
                BinaryCodec.WriteString(writer, descriptor.FileName);
                writer.Write(descriptor.Sequence);
                writer.Write(descriptor.Checksum);
                writer.Write(descriptor.Length);
                writer.Write(descriptor.SymbolCount);
                writer.Write(descriptor.RelationshipCount);
            }
            writer.Flush();
        }
        var bytes = BinaryCodec.FinishWithChecksum(stream);
        return (bytes, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - sizeof(uint), sizeof(uint))));
    }

    public static StoreManifest Decode(byte[] bytes, NativeStoreOptions options)
    {
        _ = BinaryCodec.ValidateAndSlicePayload(bytes);
        using var stream = new MemoryStream(bytes, 0, bytes.Length - sizeof(uint), writable: false, publiclyVisible: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Invalid manifest magic.");
        var formatVersion = reader.ReadInt32();
        if (formatVersion is < 1 or > CurrentFormatVersion) throw new InvalidDataException("Unsupported manifest format.");
        var sequence = reader.ReadInt64();
        if (sequence < 0) throw new InvalidDataException("Invalid manifest sequence.");
        var repositoryId = BinaryCodec.ReadString(reader, options.MaxStringBytes);
        var generation = BinaryCodec.ReadString(reader, options.MaxStringBytes);
        var indexedAt = ReadTimestamp(reader, "manifest indexed-at");
        var diagnosticCount = ReadCount(reader, 1_000_000, "diagnostic");
        var diagnostics = ImmutableArray.CreateBuilder<string>(diagnosticCount);
        for (var i = 0; i < diagnosticCount; i++) diagnostics.Add(BinaryCodec.ReadString(reader, options.MaxStringBytes));

        SnapshotDescriptor? snapshot = null;
        if (formatVersion >= 2 && reader.ReadBoolean())
        {
            var fileName = BinaryCodec.ReadString(reader, options.MaxStringBytes);
            ValidateLeafFileName(fileName);
            snapshot = new SnapshotDescriptor(
                fileName,
                reader.ReadInt64(),
                reader.ReadUInt32(),
                reader.ReadInt64(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32());
            ValidateSnapshot(snapshot, sequence, options);
        }

        var segmentCount = ReadCount(reader, 10_000_000, "segment");
        var segments = ImmutableDictionary.CreateBuilder<FileModuleKey, SegmentDescriptor>();
        for (var i = 0; i < segmentCount; i++)
        {
            var key = FileModuleKey.Create(
                BinaryCodec.ReadString(reader, options.MaxStringBytes),
                BinaryCodec.ReadString(reader, options.MaxStringBytes));
            var fileName = BinaryCodec.ReadString(reader, options.MaxStringBytes);
            ValidateLeafFileName(fileName);
            var descriptor = new SegmentDescriptor(
                key,
                fileName,
                reader.ReadInt64(),
                reader.ReadUInt32(),
                reader.ReadInt64(),
                reader.ReadInt32(),
                reader.ReadInt32());
            if (descriptor.Length < sizeof(uint) || descriptor.Length > options.MaxSegmentBytes)
                throw new InvalidDataException("Invalid segment length in manifest.");
            if (descriptor.SymbolCount < 0 || descriptor.RelationshipCount < 0 ||
                (long)descriptor.SymbolCount + descriptor.RelationshipCount > options.MaxRecordsPerSegment)
                throw new InvalidDataException("Invalid record count in manifest.");
            if (descriptor.Sequence <= 0 || descriptor.Sequence > sequence)
                throw new InvalidDataException("Invalid segment sequence in manifest.");
            if (!segments.TryAdd(key, descriptor)) throw new InvalidDataException("Duplicate segment key in manifest.");
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected trailing manifest data.");
        return new StoreManifest(sequence, repositoryId, generation, indexedAt, diagnostics.MoveToImmutable(), segments.ToImmutable(), snapshot);
    }

    public static uint GetChecksum(byte[] bytes) =>
        bytes.Length < sizeof(uint)
            ? throw new InvalidDataException("Manifest is too short.")
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - sizeof(uint), sizeof(uint)));

    private static void ValidateSnapshot(SnapshotDescriptor value, long manifestSequence, NativeStoreOptions options)
    {
        if (value.Sequence != manifestSequence || value.Length < PackLayoutMinimum || value.Length > options.MaxSnapshotPackBytes)
            throw new InvalidDataException("Invalid snapshot descriptor in manifest.");
        if (value.SymbolCount < 0 || value.RelationshipCount < 0 || value.ResolvedRelationshipCount < 0 ||
            value.ResolvedRelationshipCount > value.RelationshipCount || value.FileCount < 0 ||
            (long)value.SymbolCount + value.RelationshipCount > options.MaxRecordsPerSnapshot)
            throw new InvalidDataException("Invalid snapshot counts in manifest.");
    }

    // Keep Kernel independent from the packing namespace's implementation details while still rejecting tiny files.
    private const int PackLayoutMinimum = 4096;

    private static DateTimeOffset ReadTimestamp(BinaryReader reader, string label)
    {
        var value = reader.ReadInt64();
        try { return DateTimeOffset.FromUnixTimeMilliseconds(value); }
        catch (ArgumentOutOfRangeException ex) { throw new InvalidDataException($"Invalid {label} timestamp.", ex); }
    }

    private static int ReadCount(BinaryReader reader, int maximum, string label)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > maximum) throw new InvalidDataException($"Invalid {label} count: {count}.");
        return count;
    }

    internal static void ValidateLeafFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName ||
            fileName.Contains(Path.DirectorySeparatorChar) || fileName.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidDataException("Manifest contains a non-leaf file name.");
    }
}
