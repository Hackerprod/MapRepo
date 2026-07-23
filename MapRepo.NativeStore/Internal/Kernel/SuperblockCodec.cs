using System.Text;

namespace MapRepo.NativeStore.Internal.Kernel;

internal static class SuperblockCodec
{
    private static readonly byte[] Magic = "MRSUP001"u8.ToArray();
    private const int FormatVersion = 1;

    public static byte[] Encode(SuperblockPointer pointer)
    {
        using var stream = new MemoryStream(256);
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(pointer.Sequence);
            BinaryCodec.WriteString(writer, pointer.ManifestFileName);
            writer.Write(pointer.ManifestChecksum);
            writer.Write(pointer.ManifestLength);
            writer.Write(pointer.CreatedAt.ToUnixTimeMilliseconds());
            writer.Flush();
        }
        return BinaryCodec.FinishWithChecksum(stream);
    }

    public static SuperblockPointer Decode(byte[] bytes, NativeStoreOptions options)
    {
        _ = BinaryCodec.ValidateAndSlicePayload(bytes);
        using var stream = new MemoryStream(bytes, 0, bytes.Length - sizeof(uint), writable: false, publiclyVisible: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Invalid superblock magic.");
        if (reader.ReadInt32() != FormatVersion) throw new InvalidDataException("Unsupported superblock format.");
        var sequence = reader.ReadInt64();
        var manifest = BinaryCodec.ReadString(reader, options.MaxStringBytes);
        ManifestCodec.ValidateLeafFileName(manifest);
        var checksum = reader.ReadUInt32();
        var manifestLength = reader.ReadInt64();
        var timestamp = reader.ReadInt64();
        DateTimeOffset createdAt;
        try { createdAt = DateTimeOffset.FromUnixTimeMilliseconds(timestamp); }
        catch (ArgumentOutOfRangeException ex) { throw new InvalidDataException("Invalid superblock timestamp.", ex); }
        var pointer = new SuperblockPointer(sequence, manifest, checksum, manifestLength, createdAt);
        if (pointer.Sequence <= 0 || pointer.ManifestLength < sizeof(uint) || pointer.ManifestLength > options.MaxManifestBytes)
            throw new InvalidDataException("Invalid superblock pointer.");
        if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected trailing superblock data.");
        return pointer;
    }
}
