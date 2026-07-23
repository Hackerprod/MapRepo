namespace MapRepo.Core;

/// <summary>Separates analyzer warnings from informational index-summary lines.</summary>
public static class RepositoryDiagnostics
{
    private static readonly string[] InformationalPrefixes =
        ["ts-semantic (full):", "ts-semantic (incremental):", "tsconfig:"];

    public static (IReadOnlyList<string> Diagnostics, IReadOnlyList<string>? Summary) Split(IReadOnlyList<string> raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var diagnostics = new List<string>();
        var summary = new List<string>();
        foreach (var line in raw)
            (InformationalPrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal)) ? summary : diagnostics).Add(line);
        return (diagnostics, summary.Count == 0 ? null : summary);
    }
}
