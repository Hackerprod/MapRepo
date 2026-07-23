using System.Collections.Immutable;
using System.Text;
using MapRepo.Core;

namespace MapRepo.NativeStore.Internal.Search;

internal static class CodeTokenizer
{
    public static string Normalize(string value) => value.Replace('\\', '/').ToLowerInvariant();

    public static ImmutableArray<string> Tokenize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var result = new HashSet<string>(StringComparer.Ordinal);
        var chunk = new StringBuilder();
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                chunk.Append(character);
                continue;
            }
            FlushChunk(chunk, result);
        }
        FlushChunk(chunk, result);
        return result.Order(StringComparer.Ordinal).ToImmutableArray();
    }

    public static IEnumerable<uint> TrigramHashes(string normalized)
    {
        if (normalized.Length < 3) yield break;
        for (var index = 0; index <= normalized.Length - 3; index++)
            yield return HashThree(normalized[index], normalized[index + 1], normalized[index + 2]);
    }

    public static IEnumerable<uint> TrigramHashes(SymbolRecord symbol)
    {
        var seen = new HashSet<uint>();
        foreach (var value in new[] { symbol.Name, symbol.QualifiedName, symbol.Signature, symbol.FilePath })
            foreach (var hash in TrigramHashes(Normalize(value)))
                if (seen.Add(hash)) yield return hash;
    }

    public static Dictionary<string, ushort> WeightedTerms(SymbolRecord symbol, out int documentLength)
    {
        var result = WeightedTerms(symbol.Name, symbol.QualifiedName, symbol.Signature, symbol.FilePath);
        documentLength = Math.Max(1, result.Values.Sum(static value => (int)value));
        return result;
    }

    public static Dictionary<string, ushort> WeightedTerms(
        string name,
        string qualifiedName,
        string signature,
        string filePath)
    {
        var frequencies = new Dictionary<string, ushort>(StringComparer.Ordinal);
        Add(frequencies, Tokenize(name), 10);
        Add(frequencies, Tokenize(qualifiedName), 4);
        Add(frequencies, Tokenize(signature), 2);
        Add(frequencies, Tokenize(filePath), 1);
        return frequencies;
    }

    private static uint HashThree(char first, char second, char third)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        hash = (hash ^ first) * prime;
        hash = (hash ^ second) * prime;
        hash = (hash ^ third) * prime;
        return hash;
    }

    private static void Add(Dictionary<string, ushort> target, IEnumerable<string> values, ushort weight)
    {
        foreach (var value in values)
            target[value] = (ushort)Math.Min(ushort.MaxValue, target.GetValueOrDefault(value) + weight);
    }

    private static void FlushChunk(StringBuilder chunk, HashSet<string> result)
    {
        if (chunk.Length == 0) return;
        var value = chunk.ToString();
        chunk.Clear();
        result.Add(value.ToLowerInvariant());
        foreach (var part in SplitIdentifier(value))
            if (part.Length > 0) result.Add(part.ToLowerInvariant());
    }

    private static IEnumerable<string> SplitIdentifier(string value)
    {
        if (value.Length < 2)
        {
            yield return value;
            yield break;
        }
        var start = 0;
        for (var index = 1; index < value.Length; index++)
        {
            var previous = value[index - 1];
            var current = value[index];
            var nextIsLower = index + 1 < value.Length && char.IsLower(value[index + 1]);
            var boundary =
                char.IsLower(previous) && char.IsUpper(current) ||
                char.IsLetter(previous) && char.IsDigit(current) ||
                char.IsDigit(previous) && char.IsLetter(current) ||
                char.IsUpper(previous) && char.IsUpper(current) && nextIsLower;
            if (!boundary) continue;
            yield return value[start..index];
            start = index;
        }
        yield return value[start..];
    }
}
