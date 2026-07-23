using System.Collections.Immutable;
using System.Text;

namespace MapRepo.NativeStore.Internal.Packing;

internal readonly record struct OverviewCountSeed(int StringId, int Count);
internal readonly record struct OverviewFileSeed(int FileOrdinal, int Count);
internal readonly record struct OverviewHubSeed(int SymbolOrdinal, int Degree);

internal sealed record OverviewSeed(
    int Symbols,
    int Relationships,
    ImmutableArray<OverviewCountSeed> Kinds,
    ImmutableArray<OverviewCountSeed> Languages,
    ImmutableArray<OverviewCountSeed> Projects,
    ImmutableArray<OverviewCountSeed> EdgeKinds,
    ImmutableArray<OverviewFileSeed> TopFiles,
    ImmutableArray<OverviewHubSeed> Hubs);

internal static class OverviewSeedCodec
{
    private static readonly byte[] Magic = "MROVER01"u8.ToArray();
    private const int Version = 1;

    public static byte[] Encode(OverviewSeed normal, OverviewSeed includingGenerated)
    {
        using var stream = new MemoryStream(2048);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        Write(writer, normal);
        Write(writer, includingGenerated);
        writer.Flush();
        return stream.ToArray();
    }

    public static (OverviewSeed Normal, OverviewSeed IncludingGenerated) Decode(ReadOnlySpan<byte> bytes)
    {
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Invalid overview projection magic.");
        if (reader.ReadInt32() != Version)
            throw new InvalidDataException("Unsupported overview projection format.");
        var normal = Read(reader);
        var includingGenerated = Read(reader);
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Unexpected trailing bytes in overview projection.");
        return (normal, includingGenerated);
    }

    private static void Write(BinaryWriter writer, OverviewSeed seed)
    {
        writer.Write(seed.Symbols);
        writer.Write(seed.Relationships);
        WriteCounts(writer, seed.Kinds);
        WriteCounts(writer, seed.Languages);
        WriteCounts(writer, seed.Projects);
        WriteCounts(writer, seed.EdgeKinds);
        WriteFiles(writer, seed.TopFiles);
        WriteHubs(writer, seed.Hubs);
    }

    private static OverviewSeed Read(BinaryReader reader)
    {
        var symbols = ReadNonNegative(reader, "symbol");
        var relationships = ReadNonNegative(reader, "relationship");
        return new OverviewSeed(
            symbols,
            relationships,
            ReadCounts(reader, 1_000_000),
            ReadCounts(reader, 1_000_000),
            ReadCounts(reader, 1_000_000),
            ReadCounts(reader, 1_000_000),
            ReadFiles(reader, 10_000),
            ReadHubs(reader, 10_000));
    }

    private static void WriteCounts(BinaryWriter writer, ImmutableArray<OverviewCountSeed> values)
    {
        writer.Write(values.Length);
        foreach (var value in values)
        {
            writer.Write(value.StringId);
            writer.Write(value.Count);
        }
    }

    private static ImmutableArray<OverviewCountSeed> ReadCounts(BinaryReader reader, int maximum)
    {
        var count = ReadCount(reader, maximum);
        var result = ImmutableArray.CreateBuilder<OverviewCountSeed>(count);
        for (var index = 0; index < count; index++)
        {
            var stringId = reader.ReadInt32();
            var value = ReadNonNegative(reader, "overview count");
            if (stringId < 0) throw new InvalidDataException("Negative overview string ID.");
            result.Add(new OverviewCountSeed(stringId, value));
        }
        return result.MoveToImmutable();
    }

    private static void WriteFiles(BinaryWriter writer, ImmutableArray<OverviewFileSeed> values)
    {
        writer.Write(values.Length);
        foreach (var value in values)
        {
            writer.Write(value.FileOrdinal);
            writer.Write(value.Count);
        }
    }

    private static ImmutableArray<OverviewFileSeed> ReadFiles(BinaryReader reader, int maximum)
    {
        var count = ReadCount(reader, maximum);
        var result = ImmutableArray.CreateBuilder<OverviewFileSeed>(count);
        for (var index = 0; index < count; index++)
        {
            var ordinal = reader.ReadInt32();
            var value = ReadNonNegative(reader, "file count");
            if (ordinal < 0) throw new InvalidDataException("Negative overview file ordinal.");
            result.Add(new OverviewFileSeed(ordinal, value));
        }
        return result.MoveToImmutable();
    }

    private static void WriteHubs(BinaryWriter writer, ImmutableArray<OverviewHubSeed> values)
    {
        writer.Write(values.Length);
        foreach (var value in values)
        {
            writer.Write(value.SymbolOrdinal);
            writer.Write(value.Degree);
        }
    }

    private static ImmutableArray<OverviewHubSeed> ReadHubs(BinaryReader reader, int maximum)
    {
        var count = ReadCount(reader, maximum);
        var result = ImmutableArray.CreateBuilder<OverviewHubSeed>(count);
        for (var index = 0; index < count; index++)
        {
            var ordinal = reader.ReadInt32();
            var degree = ReadNonNegative(reader, "hub degree");
            if (ordinal < 0) throw new InvalidDataException("Negative overview symbol ordinal.");
            result.Add(new OverviewHubSeed(ordinal, degree));
        }
        return result.MoveToImmutable();
    }

    private static int ReadCount(BinaryReader reader, int maximum)
    {
        var value = reader.ReadInt32();
        if (value < 0 || value > maximum) throw new InvalidDataException("Invalid overview array length.");
        return value;
    }

    private static int ReadNonNegative(BinaryReader reader, string label)
    {
        var value = reader.ReadInt32();
        if (value < 0) throw new InvalidDataException($"Invalid {label} value in overview projection.");
        return value;
    }
}
