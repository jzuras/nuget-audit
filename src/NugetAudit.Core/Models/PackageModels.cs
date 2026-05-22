namespace NugetAudit.Core.Models;

/// <summary>
/// Fully evaluated package information including trust status and metadata, ready for display or export.
/// </summary>
/// <param name="PackageId">The NuGet package identifier.</param>
/// <param name="Version">The resolved concrete version string.</param>
/// <param name="Authors">Comma-separated author names from the package metadata.</param>
/// <param name="Owners">Raw owner array as returned by the NuGet Search API.</param>
/// <param name="Verified">Whether the package publisher has a verified prefix reservation on nuget.org; null if unknown.</param>
/// <param name="TrustStatus">The evaluated trust status for this package.</param>
/// <param name="Description">Package description from registration metadata.</param>
/// <param name="ProjectUrl">Package project URL from registration metadata.</param>
/// <param name="LicenseExpression">SPDX license expression from registration metadata.</param>
/// <param name="LicenseUrl">Fallback license URL for older packages that predate license expressions.</param>
/// <param name="Published">The date and time the package version was published.</param>
/// <param name="IsDeprecated">Whether the package version is marked as deprecated on nuget.org.</param>
/// <param name="HasVulnerabilities">Whether the package version has known security vulnerabilities.</param>
/// <param name="DependencyType">Whether this is a direct or transitive dependency.</param>
/// <param name="ExecutableContent">
/// Detected executable content types: null = not checked (package not in local cache);
/// empty array = checked, none found; non-empty = found (e.g., "MSBld", "Alyzr", "Tools").
/// </param>
public record PackageInfo(
    string PackageId,
    string Version,
    string Authors,
    string[] Owners,
    bool? Verified,
    TrustStatus TrustStatus,
    string? Description,
    string? ProjectUrl,
    string? LicenseExpression,
    string? LicenseUrl,
    DateTimeOffset? Published,
    bool IsDeprecated,
    bool HasVulnerabilities,
    DependencyType DependencyType,
    string[]? ExecutableContent
);

/// <summary>
/// Raw search result data from the NuGet Search API for a given package.
/// </summary>
/// <param name="Verified">Whether the package publisher has a verified prefix reservation.</param>
/// <param name="Owners">Owner names returned by the API as a JSON array.</param>
/// <param name="TotalDownloads">Total download count across all versions.</param>
public record SearchResult(
    bool Verified,
    string[] Owners,
    long TotalDownloads
);

/// <summary>
/// Intermediate package data fetched from the NuGet Registration API before trust evaluation.
/// </summary>
/// <param name="PackageId">The NuGet package identifier.</param>
/// <param name="Version">The resolved concrete version string.</param>
/// <param name="Authors">Comma-separated author names.</param>
/// <param name="Description">Package description.</param>
/// <param name="ProjectUrl">Project URL.</param>
/// <param name="LicenseExpression">SPDX license expression.</param>
/// <param name="LicenseUrl">Fallback license URL for older packages.</param>
/// <param name="Published">Publication date of the package version.</param>
/// <param name="IsDeprecated">Whether the package version is deprecated.</param>
/// <param name="HasVulnerabilities">Whether the package has known vulnerabilities.</param>
/// <param name="IsUnlisted">Whether the version exists in the API but is unlisted.</param>
public record PackageRegistrationData(
    string PackageId,
    string Version,
    string? Authors,
    string? Description,
    string? ProjectUrl,
    string? LicenseExpression,
    string? LicenseUrl,
    DateTimeOffset? Published,
    bool IsDeprecated,
    bool HasVulnerabilities,
    bool IsUnlisted
);

/// <summary>
/// The result of a NuGet Registration API lookup for a specific package version.
/// </summary>
/// <param name="Outcome">The outcome of the registration lookup.</param>
/// <param name="Data">The registration data; null for PrivateFeed and Error outcomes.</param>
/// <param name="ErrorMessage">Error details; set only for the Error outcome.</param>
public record PackageMetadataResult(
    RegistrationOutcome Outcome,
    PackageRegistrationData? Data,
    string? ErrorMessage
);

/// <summary>
/// A package entry in the dependency graph used during delta BFS resolution.
/// </summary>
/// <param name="Id">The NuGet package identifier.</param>
/// <param name="Version">The resolved concrete version string.</param>
public record PackageEntry(string Id, string Version);

/// <summary>
/// A package reference parsed from a project file (.csproj, .sln, .slnx).
/// </summary>
/// <param name="Id">The NuGet package identifier.</param>
/// <param name="Version">The version expression (may be a range or CPM wildcard).</param>
/// <param name="ProjectFile">The absolute path to the project file that declares this reference.</param>
public record PackageRef(string Id, string Version, string ProjectFile);

/// <summary>
/// A single dependency entry from the NuGet Registration API catalog's dependencyGroups.
/// Represents one package that must be present for a given package version to function.
/// </summary>
/// <param name="Id">The NuGet package identifier of the dependency.</param>
/// <param name="Range">
/// The version range expression (e.g., "[1.0.0, )", "1.2.3", "[1.0.0]").
/// May be empty or null when no version constraint is specified.
/// </param>
public record PackageDependency(string Id, string Range);
