using NugetAudit.Core.Models;

namespace NugetAudit.Core.Services;

/// <summary>
/// Provides credentials for private NuGet feeds using a two-phase lookup strategy.
/// Phase 1: NuGet.Config XML lookup. Phase 2: External credential provider binary.
/// The two phases are intentionally separated — the credential provider is NEVER tried
/// when a NuGet.Config entry was found for the feed, regardless of whether credentials were present.
/// </summary>
public interface ICredentialProvider
{
    /// <summary>
    /// Attempts to retrieve credentials from a NuGet.Config file for the specified feed.
    /// </summary>
    /// <param name="feedName">The feed name as it appears in NuGet.Config packageSources.</param>
    /// <param name="feedUrl">The feed URL.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Feed credentials if found; null if a NuGet.Config entry exists for this feed but credentials are absent.
    /// When this method returns (with any result), the caller must NOT proceed to <see cref="GetFromCredentialProviderAsync"/>.
    /// Only call the credential provider when no NuGet.Config XML was found at all.
    /// </returns>
    Task<FeedCredential?> GetFromNuGetConfigAsync(string feedName, string feedUrl, CancellationToken ct);

    /// <summary>
    /// Attempts to retrieve credentials by invoking an external NuGet credential provider binary.
    /// Called only when <see cref="GetFromNuGetConfigAsync"/> found no NuGet.Config XML for the feed.
    /// </summary>
    /// <param name="feedUrl">The feed URL to authenticate against.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Feed credentials if the provider succeeded; null otherwise.</returns>
    Task<FeedCredential?> GetFromCredentialProviderAsync(string feedUrl, CancellationToken ct);
}
