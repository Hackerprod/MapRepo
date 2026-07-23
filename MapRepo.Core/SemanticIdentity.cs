using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MapRepo.Core;

/// <summary>
/// Versioned, length-delimited identity encoder shared by analyzers and storage. Display strings may
/// still be stored for presentation, but they never define persistence identity.
/// </summary>
public static class SemanticIdentity
{
    public const byte FormatVersion = 1;

    public static string CreateDeclarationId(
        string language,
        string moduleId,
        string project,
        string kind,
        string structuralIdentity,
        string filePath,
        int hexadecimalCharacters = 24) => Hash(writer =>
    {
        writer.WriteByte(FormatVersion);
        writer.WriteString("declaration");
        writer.WriteString(language);
        writer.WriteString(moduleId);
        writer.WriteString(project);
        writer.WriteString(kind);
        writer.WriteString(structuralIdentity);
        writer.WriteString(NormalizePath(filePath));
    }, hexadecimalCharacters);

    public static string CreateRelationshipId(
        string moduleId,
        string sourceId,
        string targetId,
        string kind,
        string filePath,
        int line,
        int column,
        int hexadecimalCharacters = 24) => Hash(writer =>
    {
        writer.WriteByte(FormatVersion);
        writer.WriteString("relationship");
        writer.WriteString(moduleId);
        writer.WriteString(sourceId);
        writer.WriteString(targetId);
        writer.WriteString(kind);
        writer.WriteString(NormalizePath(filePath));
        writer.WriteInt32(line);
        writer.WriteInt32(column);
    }, hexadecimalCharacters);

    public static string CreateEvidenceId(
        string moduleId,
        string language,
        string filePath,
        string kind,
        string value,
        int line,
        int column,
        int hexadecimalCharacters = 24) => Hash(writer =>
    {
        writer.WriteByte(FormatVersion);
        writer.WriteString("evidence");
        writer.WriteString(moduleId);
        writer.WriteString(language);
        writer.WriteString(NormalizePath(filePath));
        writer.WriteString(kind);
        writer.WriteString(value);
        writer.WriteInt32(line);
        writer.WriteInt32(column);
    }, hexadecimalCharacters);

    public static string NormalizePath(string path) => path.Replace('\\', '/');

    private static string Hash(Action<CanonicalWriter> write, int hexadecimalCharacters)
    {
        if (hexadecimalCharacters is < 16 or > 64 || (hexadecimalCharacters & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(hexadecimalCharacters), "Use an even value from 16 through 64.");

        using var stream = new MemoryStream(512);
        write(new CanonicalWriter(stream));
        var hash = Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
        return hash[..hexadecimalCharacters].ToLowerInvariant();
    }

    private sealed class CanonicalWriter(Stream stream)
    {
        public void WriteByte(byte value) => stream.WriteByte(value);

        public void WriteInt32(int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            stream.Write(bytes);
        }

        public void WriteString(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var byteCount = Encoding.UTF8.GetByteCount(value);
            WriteInt32(byteCount);
            if (byteCount == 0) return;
            var bytes = GC.AllocateUninitializedArray<byte>(byteCount);
            Encoding.UTF8.GetBytes(value.AsSpan(), bytes.AsSpan());
            stream.Write(bytes);
        }
    }
}
