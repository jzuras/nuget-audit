using NugetAudit.Core.Models;

namespace NugetAudit.Core.Services;

/// <summary>
/// Resolves the feed information for a package that is not available on nuget.org.
/// </summary>
public interface IPrivateFeedResolver
{
    /// <summary>
    /// Resolves the registration base URL and credentials for a package hosted on a private feed.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier.</param>
    /// <param name="solutionPath">Path to the solution or project, used to locate NuGet.Config files.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Feed information including the registration URL and credentials, or null if no private feed could be identified.
    /// </returns>
    Task<FeedInfo?> ResolvePackageFeedAsync(string packageId, string solutionPath, CancellationToken ct);
}
