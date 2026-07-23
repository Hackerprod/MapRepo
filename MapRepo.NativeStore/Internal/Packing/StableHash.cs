namespace MapRepo.NativeStore.Internal.Packing;

internal static class StableHash
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    /// <summary>Stable FNV-1a over UTF-16 code units. Hash collisions are always verified by string comparison.</summary>
    public static ulong String64(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var hash = Offset;
        foreach (var character in value)
        {
            hash = (hash ^ (byte)character) * Prime;
            hash = (hash ^ (byte)(character >> 8)) * Prime;
        }
        return hash;
    }
}
