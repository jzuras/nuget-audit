using NugetAudit.Core.Models;

namespace NugetAudit.Core.Services;

/// <summary>
/// Provides security advisory checks: package source mapping, lock file enforcement, and executable content detection.
/// </summary>
public interface ISecurityAdvisoryService
{
    /// <summary>
    /// Checks whether Package Source Mapping is configured for the solution.
    /// </summary>
    /// <param name="solutionPath">Path to the solution file or directory.</param>
    /// <returns>The package source mapping status.</returns>
    PackageSourceMappingStatus CheckPackageSourceMapping(string solutionPath);

    /// <summary>
    /// Checks whether a packages.lock.json file exists and whether RestoreLockedMode is enforced.
    /// </summary>
    /// <param name="solutionPath">Path to the solution file or directory.</param>
    /// <returns>The lock file status.</returns>
    LockFileStatus CheckLockFile(string solutionPath);

    /// <summary>
    /// Detects executable content in the local NuGet package cache for a specific package version.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier.</param>
    /// <param name="version">The package version string. Not lowercased — must match cache layout exactly.</param>
    /// <param name="cachePath">Root path of the NuGet package cache (e.g., ~/.nuget/packages).</param>
    /// <returns>
    /// null if the package is not in the local cache (not checked);
    /// an empty array if the package is cached but has no executable content;
    /// a non-empty array of labels (e.g., "MSBld", "Alyzr", "Tools") if executable content was found.
    /// </returns>
    string[]? GetExecutableContent(string packageId, string version, string cachePath);
}
