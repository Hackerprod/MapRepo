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

    public static bool IsExcluded(string path) => IsExcluded(path, null);

    /// <summary>Same as <see cref="IsExcluded(string)"/>, plus a per-repository list of extra
    /// substrings (case-insensitive, matched anywhere in the path) for project-specific folders
    /// the universal list can't know about — e.g. a custom build-verification scratch directory.</summary>
    public static bool IsExcluded(string path, IReadOnlyList<string>? extraPatterns)
    {
        if (path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(ExcludedSegments.Contains)) return true;
        if (extraPatterns is not { Count: > 0 }) return false;
        return extraPatterns.Any(pattern => !string.IsNullOrWhiteSpace(pattern) && path.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Recursive file search that tolerates a locked/permission-denied subdirectory
    /// instead of aborting the whole walk — <see cref="Directory.EnumerateFiles(string, string,
    /// SearchOption)"/> throws as soon as it meets one inaccessible directory anywhere in the
    /// tree, which a post-hoc <see cref="IsExcluded(string, IReadOnlyList{string}?)"/> filter on
    /// the results can never prevent. Also skips descending into excluded directories entirely,
    /// rather than walking them and discarding their files afterward.</summary>
    public static IEnumerable<string> EnumerateFiles(string root, string searchPattern, IReadOnlyList<string>? extraPatterns = null)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] subdirectories;
            string[] files;
            try
            {
                subdirectories = Directory.GetDirectories(directory);
                files = Directory.GetFiles(directory, searchPattern);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }
            foreach (var file in files) yield return file;
            foreach (var subdirectory in subdirectories)
                if (!IsExcluded(subdirectory, extraPatterns)) pending.Push(subdirectory);
        }
    }
}
