using NugetAudit.Core.Models;

namespace NugetAudit.Core.Services;

/// <summary>
/// Evaluates the trust status of a package based on registration data, search results, and the configured trust policy.
/// This is a pure function with no side effects or external dependencies.
/// </summary>
public interface ITrustEvaluator
{
    /// <summary>
    /// Evaluates the trust status for a package.
    /// </summary>
    /// <param name="data">Registration metadata for the package version.</param>
    /// <param name="search">Search API result for the package; null if not found in the search index.</param>
    /// <param name="config">The loaded trust configuration defining trusted owners and packages.</param>
    /// <returns>The <see cref="TrustStatus"/> for this package.</returns>
    TrustStatus Evaluate(PackageRegistrationData data, SearchResult? search, TrustConfig config);
}
