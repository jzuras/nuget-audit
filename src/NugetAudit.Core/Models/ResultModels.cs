namespace NugetAudit.Core.Models;

/// <summary>
/// The complete result of a NuGet audit run.
/// </summary>
/// <param name="Packages">All evaluated packages from the audit.</param>
/// <param name="PsmStatus">Whether Package Source Mapping is configured for the solution.</param>
/// <param name="LockFileStatus">The lock file enforcement status for the solution.</param>
/// <param name="TotalProjects">Total number of projects found in the solution.</param>
/// <param name="HasTrustConfig">True when a TrustConfig.json file was found and loaded; false when running with an empty configuration.</param>
/// <param name="RecentDaysThreshold">Number of days used to flag recently published packages as higher supply chain risk.</param>
public record AuditResult(
    PackageInfo[] Packages,
    PackageSourceMappingStatus PsmStatus,
    LockFileStatus LockFileStatus,
    int TotalProjects,
    bool HasTrustConfig = true,
    int RecentDaysThreshold = 14
)
{
    /// <summary>
    /// Gets a value indicating whether the audit found any actionable package-level issues.
    /// Covers untrusted/version-changed/unknown-owner packages, deprecated packages, and vulnerable packages.
    /// Note: --check mode also fails on setup advisory conditions (missing lock file, RestoreLockedMode,
    /// pre-build target, or Package Source Mapping) which are checked separately in RenderCheckOutput.
    /// </summary>
    public bool HasIssues =>
        this.Packages.Any(p => p.TrustStatus is
            TrustStatus.Untrusted or TrustStatus.VersionChanged or TrustStatus.VerifiedUnknownOwner)
        || this.Packages.Any(p => p.IsDeprecated)
        || this.Packages.Any(p => p.HasVulnerabilities);
}

/// <summary>
/// A package that changed versions as part of a preview-update operation.
/// </summary>
/// <param name="Package">The package info evaluated at the new version.</param>
/// <param name="OldVersion">The version string before the update.</param>
public record PackageChangedEntry(PackageInfo Package, string OldVersion);

/// <summary>
/// The result of previewing a package add or update operation.
/// </summary>
/// <param name="Added">Packages that would be newly added to the dependency graph.</param>
/// <param name="Changed">Packages whose versions would change, with trust data at the new version.</param>
/// <param name="Removed">Package IDs that would be removed from the graph.</param>
/// <param name="ResolvedVersion">The actual version that would be used (may differ from requested).</param>
/// <param name="VersionNote">Human-readable note when a version was auto-resolved (e.g., "No version specified; using latest: X.Y.Z").</param>
/// <param name="IsNewPackage">True when this is a new add; false when updating an existing package.</param>
/// <param name="IsPartialResult">True when the result is incomplete due to unresolvable dependencies.</param>
/// <param name="PartialResultReason">The reason for a partial result: "VersionRequired" or "CredentialsUnavailable".</param>
/// <param name="HasTrustConfig">True when a TrustConfig.json file was found and loaded; false when running with an empty configuration.</param>
/// <param name="RecentDaysThreshold">Number of days used to flag recently published packages as higher supply chain risk.</param>
/// <param name="IsApproximate">True when the result was produced by the BFS resolver rather than an exact <c>dotnet restore</c> run. Always true when <c>--fast</c> is used; also true when private-feed packages fall back to BFS.</param>
/// <param name="IsPrivateToPublicTransition">True when the package being updated moves from a private feed to the public nuget.org feed, indicating a supply-chain risk worth surfacing to the user.</param>
public record PreviewUpdateResult(
    PackageInfo[] Added,
    PackageChangedEntry[] Changed,
    string[] Removed,
    string ResolvedVersion,
    string? VersionNote,
    bool IsNewPackage,
    bool IsPartialResult,
    string? PartialResultReason,
    bool HasTrustConfig = true,
    int RecentDaysThreshold = 14,
    bool IsApproximate = false,
    bool IsPrivateToPublicTransition = false
);

/// <summary>
/// The result of previewing a full package restore for a project.
/// </summary>
/// <param name="Added">All packages that would be added to the project.</param>
/// <param name="DirectRefs">The explicit PackageReference seeds from the project file.</param>
/// <param name="IsApproximate">True when some transitive dependencies could not be fully resolved.</param>
/// <param name="PrivateFeedCount">Number of packages that are from private feeds and were not resolved transitively.</param>
/// <param name="NeedsReviewCount">Number of packages that need trust review.</param>
/// <param name="HasTrustConfig">True when a TrustConfig.json file was found and loaded; false when running with an empty configuration.</param>
/// <param name="RecentDaysThreshold">Number of days used to flag recently published packages as higher supply chain risk.</param>
/// <param name="ParseWarnings">Optional array of warning messages produced while parsing the assets file or project references; null when no warnings were generated.</param>
public record PreviewRestoreResult(
    PackageInfo[] Added,
    PackageRef[] DirectRefs,
    bool IsApproximate,
    int PrivateFeedCount,
    int NeedsReviewCount,
    bool HasTrustConfig = true,
    int RecentDaysThreshold = 14,
    string[]? ParseWarnings = null
);
