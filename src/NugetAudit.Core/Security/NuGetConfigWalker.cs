namespace NugetAudit.Core.Security;

/// <summary>
/// Walks the directory hierarchy to locate NuGet.Config files, local-first.
/// </summary>
internal static class NuGetConfigWalker
{
    /// <summary>
    /// Yields the path of each NuGet.Config found walking up from <paramref name="startDir"/>
    /// to the filesystem root, followed by the global NuGet.Config.
    /// Only paths where the file actually exists are yielded.
    /// </summary>
    /// <param name="startDir">The directory to begin searching from.</param>
    internal static IEnumerable<string> WalkConfigPaths(string startDir)
    {
        string? current = startDir;

        while (current is not null)
        {
            string configPath = Path.Combine(current, "NuGet.Config");

            if (File.Exists(configPath))
            {
                yield return configPath;
            }

            string? parent = Path.GetDirectoryName(current);

            if (parent == current)
            {
                break;
            }

            current = parent;
        }

        string globalConfig = NuGetConfigLocator.GetGlobalNuGetConfigPath();

        if (File.Exists(globalConfig))
        {
            yield return globalConfig;
        }
    }

    /// <summary>
    /// Returns the directory for a given path.
    /// If the path points to a file, returns its parent directory; otherwise returns the path itself.
    /// </summary>
    /// <param name="path">A path to a file or directory.</param>
    /// <returns>The directory to search from.</returns>
    internal static string GetDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath)
            ? Path.GetDirectoryName(fullPath) ?? fullPath
            : fullPath;
    }
}
