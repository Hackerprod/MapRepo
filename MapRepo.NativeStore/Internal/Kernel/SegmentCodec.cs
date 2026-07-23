using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using MapRepo.Core;

namespace MapRepo.NativeStore.Internal.Kernel;

internal static class SegmentCodec
{
    private static readonly byte[] Magic = "MRSEG001"u8.ToArray();
    private const int CurrentFormatVersion = 2;

    public static (byte[] Bytes, uint Checksum) Encode(FileSegmentData segment, long sequence)
    {
        using var stream = new MemoryStream(4096);
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(CurrentFormatVersion);
            writer.Write(sequence);
            BinaryCodec.WriteString(writer, segment.Key.ModuleId);
            BinaryCodec.WriteString(writer, segment.Key.FilePath);
            writer.Write(segment.Symbols.Length);
            writer.Write(segment.Relationships.Length);
            foreach (var symbol in segment.Symbols) WriteSymbol(writer, symbol);
            foreach (var relationship in segment.Relationships) WriteRelationship(writer, relationship);
            writer.Flush();
        }

        var bytes = BinaryCodec.FinishWithChecksum(stream);
        return (bytes, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - sizeof(uint), sizeof(uint))));
    }

    public static FileSegmentData Decode(
        byte[] bytes,
        SegmentDescriptor descriptor,
        NativeStoreOptions options)
    {
        if (bytes.LongLength > options.MaxSegmentBytes)
            throw new InvalidDataException($"Segment exceeds MaxSegmentBytes: {bytes.LongLength}.");
        var payload = BinaryCodec.ValidateAndSlicePayload(bytes);
        var checksum = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - sizeof(uint), sizeof(uint)));
        if (checksum != descriptor.Checksum) throw new InvalidDataException("Segment checksum differs from manifest.");
        if (bytes.LongLength != descriptor.Length) throw new InvalidDataException("Segment length differs from manifest.");

        using var stream = new MemoryStream(bytes, 0, payload.Length, writable: false, publiclyVisible: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var magic = reader.ReadBytes(Magic.Length);
        if (!magic.AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Invalid segment magic.");
        var formatVersion = reader.ReadInt32();
        if (formatVersion is < 1 or > CurrentFormatVersion)
            throw new InvalidDataException($"Unsupported segment format: {formatVersion}.");
        var sequence = reader.ReadInt64();
        if (sequence != descriptor.Sequence) throw new InvalidDataException("Segment sequence differs from manifest.");
        var key = FileModuleKey.Create(
            BinaryCodec.ReadString(reader, options.MaxStringBytes),
            BinaryCodec.ReadString(reader, options.MaxStringBytes));
        if (key != descriptor.Key) throw new InvalidDataException("Segment key differs from manifest.");

        var symbolCount = ReadCount(reader, descriptor.SymbolCount, options.MaxRecordsPerSegment, "symbols");
        var relationshipCount = ReadCount(reader, descriptor.RelationshipCount, options.MaxRecordsPerSegment, "relationships");
        if ((long)symbolCount + relationshipCount > options.MaxRecordsPerSegment)
            throw new InvalidDataException("Segment record count exceeds MaxRecordsPerSegment.");
        var symbols = ImmutableArray.CreateBuilder<SymbolRecord>(symbolCount);
        var relationships = ImmutableArray.CreateBuilder<RelationshipRecord>(relationshipCount);
        for (var i = 0; i < symbolCount; i++) symbols.Add(ReadSymbol(reader, options.MaxStringBytes, formatVersion));
        for (var i = 0; i < relationshipCount; i++) relationships.Add(ReadRelationship(reader, options.MaxStringBytes, formatVersion));
        if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected trailing segment data.");
        return new FileSegmentData(key, symbols.MoveToImmutable(), relationships.MoveToImmutable());
    }

    private static int ReadCount(BinaryReader reader, int expected, int maximum, string label)
    {
        var value = reader.ReadInt32();
        if (value < 0 || value > maximum) throw new InvalidDataException($"Invalid {label} count: {value}.");
        if (value != expected) throw new InvalidDataException($"{label} count differs from manifest.");
        return value;
    }

    private static void WriteSymbol(BinaryWriter writer, SymbolRecord value)
    {
        BinaryCodec.WriteString(writer, value.Id);
        BinaryCodec.WriteString(writer, value.RepositoryId);
        BinaryCodec.WriteNullableString(writer, value.Project);
        BinaryCodec.WriteString(writer, FileModuleKey.NormalizePath(value.FilePath));
        BinaryCodec.WriteString(writer, value.Name);
        BinaryCodec.WriteString(writer, value.QualifiedName);
        BinaryCodec.WriteString(writer, value.Kind);
        writer.Write(value.StartLine);
        writer.Write(value.StartColumn);
        writer.Write(value.EndLine);
        writer.Write(value.EndColumn);
        BinaryCodec.WriteString(writer, value.Signature);
        BinaryCodec.WriteString(writer, value.Language);
        BinaryCodec.WriteString(writer, value.ModuleId);
        BinaryCodec.WriteNullableString(writer, value.StructuralIdentity);
    }

    private static SymbolRecord ReadSymbol(BinaryReader reader, int maxStringBytes, int formatVersion)
    {
        var id = BinaryCodec.ReadString(reader, maxStringBytes);
        var repositoryId = BinaryCodec.ReadString(reader, maxStringBytes);
        var project = BinaryCodec.ReadNullableString(reader, maxStringBytes);
        var filePath = BinaryCodec.ReadString(reader, maxStringBytes);
        var name = BinaryCodec.ReadString(reader, maxStringBytes);
        var qualifiedName = BinaryCodec.ReadString(reader, maxStringBytes);
        var kind = BinaryCodec.ReadString(reader, maxStringBytes);
        var startLine = reader.ReadInt32();
        var startColumn = reader.ReadInt32();
        var endLine = reader.ReadInt32();
        var endColumn = reader.ReadInt32();
        var signature = BinaryCodec.ReadString(reader, maxStringBytes);
        var language = BinaryCodec.ReadString(reader, maxStringBytes);
        var moduleId = BinaryCodec.ReadString(reader, maxStringBytes);
        var structuralIdentity = formatVersion >= 2
            ? BinaryCodec.ReadNullableString(reader, maxStringBytes)
            : null;
        return new SymbolRecord(id, repositoryId, project, filePath, name, qualifiedName, kind,
            startLine, startColumn, endLine, endColumn, signature, language, moduleId, structuralIdentity);
    }

    private static void WriteRelationship(BinaryWriter writer, RelationshipRecord value)
    {
        BinaryCodec.WriteString(writer, value.Id);
        BinaryCodec.WriteString(writer, value.RepositoryId);
        BinaryCodec.WriteString(writer, value.SourceId);
        BinaryCodec.WriteString(writer, value.TargetId);
        BinaryCodec.WriteString(writer, value.Kind);
        BinaryCodec.WriteString(writer, FileModuleKey.NormalizePath(value.FilePath));
        writer.Write(value.Line);
        writer.Write(value.Column);
        BinaryCodec.WriteString(writer, value.Confidence);
        BinaryCodec.WriteString(writer, value.Language);
        BinaryCodec.WriteString(writer, value.ModuleId);
        BinaryCodec.WriteNullableString(writer, value.StructuralIdentity);
    }

    private static RelationshipRecord ReadRelationship(BinaryReader reader, int maxStringBytes, int formatVersion)
    {
        var id = BinaryCodec.ReadString(reader, maxStringBytes);
        var repositoryId = BinaryCodec.ReadString(reader, maxStringBytes);
        var sourceId = BinaryCodec.ReadString(reader, maxStringBytes);
        var targetId = BinaryCodec.ReadString(reader, maxStringBytes);
        var kind = BinaryCodec.ReadString(reader, maxStringBytes);
        var filePath = BinaryCodec.ReadString(reader, maxStringBytes);
        var line = reader.ReadInt32();
        var column = reader.ReadInt32();
        var confidence = BinaryCodec.ReadString(reader, maxStringBytes);
        var language = BinaryCodec.ReadString(reader, maxStringBytes);
        var moduleId = BinaryCodec.ReadString(reader, maxStringBytes);
        var structuralIdentity = formatVersion >= 2
            ? BinaryCodec.ReadNullableString(reader, maxStringBytes)
            : null;
        return new RelationshipRecord(id, repositoryId, sourceId, targetId, kind, filePath,
            line, column, confidence, language, moduleId, structuralIdentity);
    }
}
