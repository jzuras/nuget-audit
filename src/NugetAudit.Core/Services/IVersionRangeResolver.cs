using NugetAudit.Core.Models;

namespace NugetAudit.Core.Services;

/// <summary>
/// Resolves NuGet version range expressions to concrete version strings.
/// Uses NuGet.Versioning for range parsing and the Search API for non-exact ranges.
/// </summary>
public interface IVersionRangeResolver
{
    /// <summary>
    /// Resolves a version range expression to a concrete version string.
    /// Exact versions are returned as-is. Non-exact ranges are resolved via the NuGet Search API.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier.</param>
    /// <param name="rangeExpression">
    /// A version string or range expression (e.g., "1.2.3", "[1.0,2.0)", "*").
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="VersionResolutionResult"/> with the concrete version and a flag indicating
    /// whether the result is approximate (i.e., resolved via Search API rather than exact match).
    /// </returns>
    Task<VersionResolutionResult> ResolveAsync(string packageId, string rangeExpression, CancellationToken ct);
}
