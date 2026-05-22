using NugetAudit.Core.Models;

namespace NugetAudit.Core.Services;

/// <summary>
/// Fetches package metadata and dependency information from the NuGet Registration v3 API.
/// </summary>
public interface INuGetRegistrationClient
{
    /// <summary>
    /// Retrieves metadata for a specific package version from the NuGet Registration API.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier.</param>
    /// <param name="version">The concrete version to look up.</param>
    /// <param name="fallbackToLatest">
    /// When true and the exact version is not found, fall back to the latest available version.
    /// Only used in preview-update flows when the requested version does not exist.
    /// </param>
    /// <param name="baseUrl">The registration base URL for the feed to query.</param>
    /// <param name="credential">Optional credentials for private feeds; null for public feeds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="PackageMetadataResult"/> describing the outcome and data.</returns>
    Task<PackageMetadataResult> GetPackageMetadataAsync(
        string packageId,
        string version,
        bool fallbackToLatest,
        string baseUrl,
        FeedCredential? credential,
        CancellationToken ct);

    /// <summary>
    /// Fetches the direct dependency list for a specific package version from the NuGet Registration API.
    /// Selects the most applicable target framework group: exact match, then netstandard2.0, then no-framework
    /// catch-all, then first available group.
    /// Returns an empty array when the package has no dependencies, cannot be found, or is on a private feed.
    /// Used by the BFS engine in preview flows to resolve transitive dependencies without running dotnet restore.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier.</param>
    /// <param name="version">The concrete version whose dependencies to retrieve.</param>
    /// <param name="targetFramework">The preferred target framework moniker (e.g., "netstandard2.0").</param>
    /// <param name="baseUrl">The registration base URL for the feed to query.</param>
    /// <param name="credential">Optional credentials for private feeds; null for public feeds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An array of direct dependencies, or an empty array if none exist or on failure.</returns>
    Task<PackageDependency[]> GetPackageDependenciesAsync(
        string packageId,
        string version,
        string targetFramework,
        string baseUrl,
        FeedCredential? credential,
        CancellationToken ct);
}
