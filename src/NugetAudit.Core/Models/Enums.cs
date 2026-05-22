namespace NugetAudit.Core.Models;

/// <summary>
/// Represents the trust status of a NuGet package as determined by the trust evaluator.
/// </summary>
public enum TrustStatus
{
    /// <summary>The package is from a verified publisher with a trusted owner.</summary>
    Verified,

    /// <summary>The package was resolved from a private feed and could not be evaluated against nuget.org.</summary>
    PrivateFeed,

    /// <summary>The package is explicitly listed in TrustConfig.json as a trusted package entry.</summary>
    TrustedPackage,

    /// <summary>The package is from a verified publisher but the owner is not in the trusted owners list.</summary>
    VerifiedUnknownOwner,

    /// <summary>The package version has changed since it was added to TrustConfig.json.</summary>
    VersionChanged,

    /// <summary>The package is not from a verified publisher and not in the trusted list.</summary>
    Untrusted,
}

/// <summary>
/// Indicates whether a package is a direct or transitive dependency.
/// </summary>
public enum DependencyType
{
    /// <summary>Explicitly referenced in a project file.</summary>
    Direct,

    /// <summary>Pulled in as a transitive dependency of another package.</summary>
    Transitive,
}

/// <summary>
/// Represents the four distinct outcomes when fetching package metadata from the NuGet registration API.
/// </summary>
public enum RegistrationOutcome
{
    /// <summary>Package metadata was found successfully.</summary>
    Found,

    /// <summary>The package version exists but is unlisted on nuget.org.</summary>
    Unlisted,

    /// <summary>The package is hosted on a private feed and cannot be queried via the public API.</summary>
    PrivateFeed,

    /// <summary>An error occurred while fetching package metadata.</summary>
    Error,
}

/// <summary>
/// Indicates the status of NuGet Package Source Mapping configuration for the solution.
/// </summary>
public enum PackageSourceMappingStatus
{
    /// <summary>Package source mapping is configured in NuGet.Config.</summary>
    Configured,

    /// <summary>Package source mapping is not configured.</summary>
    NotConfigured,
}

/// <summary>
/// Indicates the lock file enforcement status for the solution.
/// </summary>
public enum LockFileStatus
{
    /// <summary>No packages.lock.json file was found.</summary>
    NoLockFile,

    /// <summary>A packages.lock.json file exists but RestoreLockedMode is not set to true.</summary>
    LockFileNoEnforcement,

    /// <summary>A packages.lock.json file exists and RestoreLockedMode is true.</summary>
    LockedAndEnforced,

    /// <summary>A packages.lock.json file exists and RestoreLockedMode is true, but no Directory.Build.targets with a nuget-audit invocation was found.</summary>
    LockedEnforcedNoBuildTarget,
}

/// <summary>
/// The authentication scheme used to connect to a private NuGet feed.
/// </summary>
public enum FeedAuthScheme
{
    /// <summary>HTTP Basic authentication (username + password).</summary>
    Basic,

    /// <summary>Bearer token authentication.</summary>
    Bearer,
}
