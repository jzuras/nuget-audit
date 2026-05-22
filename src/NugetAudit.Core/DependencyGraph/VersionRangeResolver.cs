using System.Text.RegularExpressions;
using NuGet.Versioning;
using NugetAudit.Core.Models;
using NugetAudit.Core.Services;

namespace NugetAudit.Core.DependencyGraph;

/// <summary>
/// Resolves NuGet version range expressions to concrete version strings using NuGet.Versioning
/// for accurate range classification and the NuGet Search API for non-exact ranges.
/// </summary>
/// <remarks>
/// Exact versions (<c>[1.0.0]</c>) are returned as-is and not flagged as approximate.
/// All other range expressions resolve to the latest stable version and are flagged as
/// approximate (IsApproximate = true). Seeds (direct package references from the project file)
/// are handled separately in the BFS caller — bare version strings like <c>10.0.6</c> bypass
/// the resolver entirely and are used as exact pins.
/// </remarks>
public class VersionRangeResolver : IVersionRangeResolver
{
    /// <summary>
    /// Gets the NuGet Search client used to resolve the latest stable version for non-exact ranges.
    /// </summary>
    private INuGetSearchClient SearchClient { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="VersionRangeResolver"/> class.
    /// </summary>
    /// <param name="searchClient">The NuGet Search API client.</param>
    public VersionRangeResolver(INuGetSearchClient searchClient)
    {
        this.SearchClient = searchClient;
    }

    /// <inheritdoc />
    public async Task<VersionResolutionResult> ResolveAsync(
        string packageId,
        string rangeExpression,
        CancellationToken ct)
    {
        // Empty range or wildcard — no constraint, resolve latest stable.
        if (string.IsNullOrWhiteSpace(rangeExpression) || rangeExpression.Trim() == "*")
        {
            string? latest = await this.SearchClient.GetLatestVersionAsync(packageId, ct);
            return new VersionResolutionResult(latest ?? "0.0.0", IsApproximate: true);
        }

        // Use NuGet.Versioning for accurate range parsing.
        if (VersionRange.TryParse(rangeExpression, out var range))
        {
            // Exact version: [1.0.0] — both bounds are present, inclusive, and equal.
            if (range.HasLowerBound && range.HasUpperBound
                && range.IsMinInclusive && range.IsMaxInclusive
                && string.Equals(
                    range.MinVersion?.ToNormalizedString(),
                    range.MaxVersion?.ToNormalizedString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return new VersionResolutionResult(
                    range.MinVersion!.ToNormalizedString(),
                    IsApproximate: false);
            }

            // Non-exact range — resolve latest stable and check if it satisfies the range.
            // Using latest stable is the best approximation for transitive dependency ranges
            // (e.g. "[2.0.0, )") because the minimum version would resolve to an older
            // package with more compat-shim dependencies. Seeds (direct refs) bypass this
            // resolver with exact-version pinning handled in the BFS caller.
            string? latest = await this.SearchClient.GetLatestVersionAsync(packageId, ct);

            if (latest is not null
                && NuGetVersion.TryParse(latest, out var latestNuGet)
                && range.Satisfies(latestNuGet))
            {
                return new VersionResolutionResult(latest, IsApproximate: true);
            }

            // Fall back to minimum if latest doesn't satisfy the range or wasn't found.
            if (range.MinVersion is not null)
            {
                return new VersionResolutionResult(
                    range.MinVersion.ToNormalizedString(),
                    IsApproximate: true);
            }

            if (latest is not null)
            {
                return new VersionResolutionResult(latest, IsApproximate: true);
            }
        }

        // Fallback: strip all range notation and treat remaining as a raw version string.
        string stripped = Regex.Replace(rangeExpression, @"[\[\]()\s,<>=]", "").Trim();
        return new VersionResolutionResult(
            string.IsNullOrWhiteSpace(stripped) ? "0.0.0" : stripped,
            IsApproximate: true);
    }
}
