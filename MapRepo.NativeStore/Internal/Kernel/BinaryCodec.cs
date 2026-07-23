using System.Buffers.Binary;
using System.Text;

namespace MapRepo.NativeStore.Internal.Kernel;

internal static class BinaryCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void WriteString(BinaryWriter writer, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        if (bytes.Length != 0) writer.Write(bytes);
    }

    public static string ReadString(BinaryReader reader, int maxBytes)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > maxBytes)
            throw new InvalidDataException($"Invalid UTF-8 string length: {length}.");
        if (length == 0) return string.Empty;
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return StrictUtf8.GetString(bytes);
    }

    public static void WriteNullableString(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null) WriteString(writer, value);
    }

    public static string? ReadNullableString(BinaryReader reader, int maxBytes) =>
        reader.ReadBoolean() ? ReadString(reader, maxBytes) : null;

    public static byte[] FinishWithChecksum(MemoryStream payload)
    {
        var body = payload.ToArray();
        using var output = new MemoryStream(body.Length + sizeof(uint));
        output.Write(body);
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(Crc32C.Compute(body));
        writer.Flush();
        return output.ToArray();
    }

    public static ReadOnlyMemory<byte> ValidateAndSlicePayload(byte[] bytes)
    {
        if (bytes.Length < sizeof(uint)) throw new InvalidDataException("File is too short for a checksum.");
        var payloadLength = bytes.Length - sizeof(uint);
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(payloadLength, sizeof(uint)));
        var computed = Crc32C.Compute(bytes.AsSpan(0, payloadLength));
        if (stored != computed)
            throw new InvalidDataException($"CRC32C mismatch: stored {stored:x8}, computed {computed:x8}.");
        return bytes.AsMemory(0, payloadLength);
    }
}
