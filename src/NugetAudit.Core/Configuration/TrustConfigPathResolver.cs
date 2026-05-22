namespace NugetAudit.Core.Configuration;

/// <summary>
/// Resolves the path to TrustConfig.json from a target path and an optional explicit override.
/// </summary>
internal static class TrustConfigPathResolver
{
    /// <summary>
    /// Returns the full path to TrustConfig.json.
    /// When <paramref name="trustConfigPath"/> is provided, resolves it directly.
    /// Otherwise derives the location from <paramref name="targetPath"/>:
    /// if it is a file, uses its directory; if it is a directory, uses it directly.
    /// </summary>
    /// <param name="targetPath">The --path option value (file or directory).</param>
    /// <param name="trustConfigPath">Optional explicit --trust-config path; null to derive from targetPath.</param>
    /// <returns>The absolute path to TrustConfig.json.</returns>
    internal static string Resolve(string targetPath, string? trustConfigPath)
    {
        if (!string.IsNullOrWhiteSpace(trustConfigPath))
        {
            string full = Path.GetFullPath(trustConfigPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Directory.Exists(full)
                ? Path.Combine(full, "TrustConfig.json")
                : full;
        }

        string fullPath = Path.GetFullPath(targetPath);
        string dir = File.Exists(fullPath)
            ? Path.GetDirectoryName(fullPath) ?? fullPath
            : fullPath;

        return Path.Combine(dir, "TrustConfig.json");
    }
}
