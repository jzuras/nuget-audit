using NugetAudit.Core.Models;
using NugetAudit.Core.Security;
using NugetAudit.Core.Services;

namespace NugetAudit.Core.Audit;

/// <summary>
/// Shared helper that fetches registration and search metadata for a single package,
/// evaluates its trust status, and optionally checks for executable content in the
/// local NuGet cache.
/// </summary>
internal static class PackageInfoBuilder
{
    #region Constants

    /// <summary>
    /// Gets the NuGet.org registration v3 API base URL (gzip + semver2 endpoint).
    /// </summary>
    internal static string NuGetOrgRegistrationBaseUrl { get; } =
        "https://api.nuget.org/v3/registration5-gz-semver2/";

    #endregion

    /// <summary>
    /// Fetches registration and search metadata for a single package, evaluates trust,
    /// and optionally checks for executable content in the local NuGet cache.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier.</param>
    /// <param name="version">The concrete version string.</param>
    /// <param name="feedInfo">
    /// Private feed info; <see langword="null"/> routes to nuget.org with no credentials.
    /// </param>
    /// <param name="trustConfig">The loaded trust configuration.</param>
    /// <param name="dependencyType">Whether the package is a direct or transitive dependency.</param>
    /// <param name="cachePath">
    /// Root of the real NuGet package cache for executable content scanning.
    /// Pass <see langword="null"/> to skip the scan (e.g. when packages were downloaded to
    /// a temp directory that has already been deleted, as in the preview flows).
    /// </param>
    /// <param name="registrationClient">NuGet Registration API client.</param>
    /// <param name="searchClient">NuGet Search API client.</param>
    /// <param name="trustEvaluator">Trust status evaluator.</param>
    /// <param name="securityAdvisoryService">
    /// Security advisory service for executable content detection.
    /// Pass <see langword="null"/> when <paramref name="cachePath"/> is also <see langword="null"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A fully populated <see cref="PackageInfo"/> for the package.</returns>
    internal static async Task<PackageInfo> BuildAsync(
        string packageId,
        string version,
        FeedInfo? feedInfo,
        TrustConfig trustConfig,
        DependencyType dependencyType,
        string? cachePath,
        INuGetRegistrationClient registrationClient,
        INuGetSearchClient searchClient,
        ITrustEvaluator trustEvaluator,
        ISecurityAdvisoryService? securityAdvisoryService,
        CancellationToken ct)
    {
        string baseUrl = feedInfo?.RegistrationBaseUrl ?? NuGetOrgRegistrationBaseUrl;
        FeedCredential? credential = feedInfo?.Credential;

        var metadataResult = await registrationClient.GetPackageMetadataAsync(
            packageId,
            version,
            fallbackToLatest: false,
            baseUrl,
            credential,
            ct);

        PackageRegistrationData? regData = metadataResult.Data;
        SearchResult? searchResult = null;
        TrustStatus trustStatus;

        switch (metadataResult.Outcome)
        {
            case RegistrationOutcome.PrivateFeed:
                trustStatus = TrustStatus.PrivateFeed;
                break;

            case RegistrationOutcome.Found:
            case RegistrationOutcome.Unlisted:
                searchResult = await searchClient.SearchPackageAsync(packageId, ct);
                regData ??= new PackageRegistrationData(
                    packageId, version, null, null, null, null, null, null, false, false, false);
                trustStatus = trustEvaluator.Evaluate(regData, searchResult, trustConfig);
                break;

            case RegistrationOutcome.Error:
            default:
                trustStatus = TrustStatus.Untrusted;
                break;
        }

        // Prefer the caller's packageId casing over regData.PackageId — private feed registration
        // APIs (e.g. GitHub Packages) return IDs in lowercase, which would corrupt display.
        // When both refer to the same package (case-insensitive match), the caller's casing wins.
        string resolvedId = string.Equals(regData?.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
            ? packageId
            : (regData?.PackageId ?? packageId);

        string[]? execContent = (cachePath is not null && securityAdvisoryService is not null)
            ? securityAdvisoryService.GetExecutableContent(packageId, version, cachePath)
            : null;

        return new PackageInfo(
            PackageId: resolvedId,
            Version: regData?.Version ?? version,
            Authors: regData?.Authors ?? string.Empty,
            Owners: searchResult?.Owners ?? [],
            Verified: searchResult?.Verified,
            TrustStatus: trustStatus,
            Description: regData?.Description,
            ProjectUrl: regData?.ProjectUrl,
            LicenseExpression: regData?.LicenseExpression,
            LicenseUrl: regData?.LicenseUrl,
            Published: regData?.Published,
            IsDeprecated: regData?.IsDeprecated ?? false,
            HasVulnerabilities: regData?.HasVulnerabilities ?? false,
            DependencyType: dependencyType,
            ExecutableContent: execContent);
    }
}
