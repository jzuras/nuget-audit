namespace NugetAudit.Core.Models;

/// <summary>
/// The trust configuration loaded from TrustConfig.json, defining which publishers and packages are trusted.
/// </summary>
/// <param name="TrustedOwners">Owner names considered trusted (matched against nuget.org owner data).</param>
/// <param name="TrustedPackages">Specific package/version pairs explicitly trusted regardless of owner.</param>
/// <param name="RecentDaysThreshold">Packages published within this many days are flagged as recently published.</param>
public record TrustConfig(
    string[] TrustedOwners,
    TrustedPackageEntry[] TrustedPackages,
    int RecentDaysThreshold
);

/// <summary>
/// An explicitly trusted package entry in TrustConfig.json.
/// </summary>
/// <param name="Id">The NuGet package identifier.</param>
/// <param name="Version">The specific version that is trusted.</param>
public record TrustedPackageEntry(string Id, string Version);

/// <summary>
/// Feed credential for authenticating against a private NuGet feed.
/// </summary>
/// <param name="Username">The username or client ID.</param>
/// <param name="Password">The password, PAT, or bearer token.</param>
/// <param name="AuthScheme">The authentication scheme to use.</param>
public record FeedCredential(string Username, string Password, FeedAuthScheme AuthScheme);

/// <summary>
/// Information about a NuGet feed including its registration base URL and optional credentials.
/// </summary>
/// <param name="RegistrationBaseUrl">The base URL for the NuGet Registration v3 API.</param>
/// <param name="Credential">Feed credentials; null if the feed is public or unauthenticated.</param>
public record FeedInfo(string RegistrationBaseUrl, FeedCredential? Credential);

/// <summary>
/// The result of resolving a version range expression to a concrete version.
/// </summary>
/// <param name="Version">The resolved concrete version string.</param>
/// <param name="IsApproximate">
/// True when the resolution used the Search API to find the latest satisfying version
/// rather than matching an exact pinned version.
/// </param>
public record VersionResolutionResult(string Version, bool IsApproximate);
