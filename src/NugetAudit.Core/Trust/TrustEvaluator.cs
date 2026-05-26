using NugetAudit.Core.Models;
using NugetAudit.Core.Services;

namespace NugetAudit.Core.Trust;

/// <summary>
/// Evaluates the trust status of a NuGet package based on registration data, search results,
/// and the configured trust policy. This is a pure function with no external dependencies.
/// </summary>
public class TrustEvaluator : ITrustEvaluator
{
    /// <inheritdoc />
    public TrustStatus Evaluate(PackageRegistrationData data, SearchResult? search, TrustConfig config)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(config);
        if (search is null)
        {
            // Search API returned no result — fall back to TrustedPackages list check.
            return TestTrustedPackage(data.PackageId, data.Version, config);
        }

        if (search.Verified is true)
        {
            bool hasTrustedOwner = search.Owners.Any(owner =>
                config.TrustedOwners.Any(trusted =>
                    string.Equals(trusted, owner, StringComparison.OrdinalIgnoreCase)));

            if (hasTrustedOwner)
            {
                return TrustStatus.Verified;
            }

            // Owner not in trustedOwners — check if this specific package+version was explicitly approved.
            var idMatches = config.TrustedPackages
                .Where(p => string.Equals(p.Id, data.PackageId, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (idMatches.Length > 0)
            {
                return idMatches.Any(p => p.Version == data.Version)
                    ? TrustStatus.TrustedPackage
                    : TrustStatus.VersionChanged;
            }

            return TrustStatus.VerifiedUnknownOwner;
        }

        // Package is not verified — check TrustedPackages list.
        return TestTrustedPackage(data.PackageId, data.Version, config);
    }

    /// <summary>
    /// Checks a package ID and version against the TrustedPackages entries in the configuration.
    /// Returns TrustedPackage for an exact ID+version match, VersionChanged if the ID matches
    /// but the version differs, and Untrusted if the ID is not in the list.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier.</param>
    /// <param name="version">The resolved concrete version string.</param>
    /// <param name="config">The loaded trust configuration.</param>
    /// <returns>The resulting <see cref="TrustStatus"/> for this package.</returns>
    private static TrustStatus TestTrustedPackage(string packageId, string version, TrustConfig config)
    {
        var idMatches = config.TrustedPackages
            .Where(p => string.Equals(p.Id, packageId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (idMatches.Length == 0)
        {
            return TrustStatus.Untrusted;
        }

        if (idMatches.Any(p => p.Version == version))
        {
            return TrustStatus.TrustedPackage;
        }

        return TrustStatus.VersionChanged;
    }
}
