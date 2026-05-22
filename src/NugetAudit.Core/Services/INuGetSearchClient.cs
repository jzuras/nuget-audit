using NugetAudit.Core.Models;

namespace NugetAudit.Core.Services;

/// <summary>
/// Queries the NuGet Search API to obtain publisher verification, ownership information,
/// and latest version data.
/// </summary>
public interface INuGetSearchClient
{
    /// <summary>
    /// Searches for a package by ID and returns its search result data including owner and verification status.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier to search for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The search result, or null if the package was not found in the search index.
    /// </returns>
    Task<SearchResult?> SearchPackageAsync(string packageId, CancellationToken ct);

    /// <summary>
    /// Returns the latest stable version of a package from the NuGet Search API.
    /// Used by <see cref="IVersionRangeResolver"/> to resolve non-exact version ranges
    /// and by the preview-update flow to auto-resolve the latest version when none is specified.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The latest stable version string (e.g., "13.0.3"), or null if not found or on error.
    /// </returns>
    Task<string?> GetLatestVersionAsync(string packageId, CancellationToken ct);
}
