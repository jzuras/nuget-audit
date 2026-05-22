namespace NugetAudit.Core.Security;

/// <summary>
/// Resolves the global NuGet package cache root directory.
/// Uses the same priority order as the NuGet client:
/// <c>NUGET_PACKAGES</c> environment variable → <c>~/.nuget/packages</c>.
/// </summary>
public static class NuGetCacheLocator
{
    /// <summary>
    /// Name of the environment variable that overrides the NuGet cache path.
    /// </summary>
    private static string NuGetPackagesEnvVar { get; } = "NUGET_PACKAGES";

    /// <summary>
    /// Returns the root directory of the global NuGet package cache.
    /// Checks the <c>NUGET_PACKAGES</c> environment variable first, then falls back
    /// to the platform-standard <c>~/.nuget/packages</c> path.
    /// </summary>
    /// <returns>The full path to the NuGet package cache root directory.</returns>
    public static string GetCachePath()
    {
        string? envPath = Environment.GetEnvironmentVariable(NuGetCacheLocator.NuGetPackagesEnvVar);

        if (!string.IsNullOrWhiteSpace(envPath))
        {
            string fullPath = Path.GetFullPath(envPath);

            if (Directory.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget",
            "packages");
    }
}
