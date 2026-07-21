namespace MapRepo.Core;

/// <summary>
/// Directory names skipped by every language module and the file watcher. Kept in one place —
/// three independent copies (server watcher, C# module, TypeScript module) had already drifted:
/// the C# module was missing dist/build/coverage, and two of the three globally excluded "Data"
/// and "packages", which are common names for legitimate, hand-authored source folders in a
/// user's own repository (unlike node_modules/bin/obj, which are never source).
/// </summary>
public static class PathExclusions
{
    private static readonly HashSet<string> ExcludedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".tmp", "node_modules", "dist", "build", "coverage", "bin", "obj"
    };

    public static bool IsExcluded(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(ExcludedSegments.Contains);
}
