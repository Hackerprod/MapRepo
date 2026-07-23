using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MapRepo.NativeStore.Identity;

/// <summary>
/// Logical symbol identity that is independent from source presentation and file location.
/// Use it for graph endpoints that should survive file moves and partial declarations.
/// </summary>
public sealed record LogicalSymbolIdentity(
    string Language,
    string Project,
    IReadOnlyList<string> NamespacePath,
    IReadOnlyList<TypeIdentity> ContainingTypes,
    string Kind,
    string MetadataName,
    int GenericArity,
    IReadOnlyList<TypedParameterIdentity> Parameters,
    string? ReturnType = null,
    string? ExplicitInterface = null)
{
    public const byte CanonicalFormatVersion = 1;

    public byte[] ToCanonicalBytes() => IdentityEncoder.Encode(writer =>
    {
        writer.Write(CanonicalFormatVersion);
        writer.WriteString(Language);
        writer.WriteString(Project);
        writer.WriteStrings(NamespacePath);
        writer.WriteInt32(ContainingTypes.Count);
        foreach (var type in ContainingTypes)
        {
            writer.WriteString(type.MetadataName);
            writer.WriteInt32(type.GenericArity);
        }
        writer.WriteString(Kind);
        writer.WriteString(MetadataName);
        writer.WriteInt32(GenericArity);
        writer.WriteInt32(Parameters.Count);
        foreach (var parameter in Parameters)
        {
            writer.WriteString(parameter.Type);
            writer.WriteString(parameter.RefKind);
            // Parameter names do not participate in CLR/C# overload identity.
        }
        writer.WriteString(ReturnType ?? string.Empty);
        writer.WriteString(ExplicitInterface ?? string.Empty);
    });

    public string ToCompactId(int hexCharacters = 32) =>
        IdentityEncoder.Hash(ToCanonicalBytes(), hexCharacters);
}

/// <summary>
/// Physical declaration identity. It combines a logical symbol with a source location, allowing
/// multiple declarations (for example partial types) to coexist without changing graph identity.
/// </summary>
public sealed record DeclarationIdentity(
    LogicalSymbolIdentity Symbol,
    string FilePath,
    int SpanStart,
    int DeclarationOrdinal = 0)
{
    public const byte CanonicalFormatVersion = 1;

    public byte[] ToCanonicalBytes() => IdentityEncoder.Encode(writer =>
    {
        writer.Write(CanonicalFormatVersion);
        writer.WriteBytes(Symbol.ToCanonicalBytes());
        writer.WriteString(NormalizePath(FilePath));
        writer.WriteInt32(SpanStart);
        writer.WriteInt32(DeclarationOrdinal);
    });

    public string ToCompactId(int hexCharacters = 32) =>
        IdentityEncoder.Hash(ToCanonicalBytes(), hexCharacters);

    private static string NormalizePath(string path) => path.Replace('\\', '/');
}

public sealed record TypeIdentity(string MetadataName, int GenericArity = 0);

public sealed record TypedParameterIdentity(string Type, string RefKind = "none", string? Name = null);

internal static class IdentityEncoder
{
    public static byte[] Encode(Action<IdentityWriter> encode)
    {
        using var stream = new MemoryStream(256);
        encode(new IdentityWriter(stream));
        return stream.ToArray();
    }

    public static string Hash(byte[] bytes, int hexCharacters)
    {
        if (hexCharacters is < 16 or > 64 || (hexCharacters & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(hexCharacters), "Use an even value from 16 to 64.");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..hexCharacters];
    }
}

internal sealed class IdentityWriter(Stream stream)
{
    public void Write(byte value) => stream.WriteByte(value);

    public void WriteInt32(int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    public void WriteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32(bytes.Length);
        stream.Write(bytes);
    }

    public void WriteStrings(IReadOnlyList<string> values)
    {
        WriteInt32(values.Count);
        foreach (var value in values) WriteString(value);
    }

    public void WriteBytes(byte[] bytes)
    {
        WriteInt32(bytes.Length);
        stream.Write(bytes);
    }
}
