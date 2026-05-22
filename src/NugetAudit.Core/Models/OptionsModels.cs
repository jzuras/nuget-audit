namespace NugetAudit.Core.Models;

/// <summary>
/// Options for running a full NuGet audit, mirroring the CLI parameter surface.
/// </summary>
/// <param name="Path">Path to a .sln, .slnx, .csproj, or directory to audit.</param>
/// <param name="ShowAll">When true, show all packages rather than only those needing review.</param>
/// <param name="PackageList">When true, output as a grouped package list instead of a table.</param>
/// <param name="IncludeExisting">When true, include existing trusted packages in the output.</param>
/// <param name="Check">When true, exit with a non-zero code if any issues are found.</param>
/// <param name="TrustConfigPath">Optional path to a TrustConfig.json file; defaults to searching from Path.</param>
public record AuditOptions(
    string Path,
    bool ShowAll,
    bool PackageList,
    bool IncludeExisting,
    bool Check,
    string? TrustConfigPath
);

/// <summary>
/// Options for previewing the impact of adding or updating a single package.
/// </summary>
/// <param name="Path">Path to the project or solution.</param>
/// <param name="PackageId">The NuGet package identifier to preview adding or updating.</param>
/// <param name="NewVersion">The target version; null means resolve the latest stable version.</param>
/// <param name="TrustConfigPath">Optional path to a TrustConfig.json file.</param>
/// <param name="UseFast">
/// When true, uses the approximate BFS resolver instead of running <c>dotnet restore</c>.
/// Faster but may not match what <c>dotnet restore</c> actually selects.
/// </param>
public record PreviewUpdateOptions(
    string Path,
    string PackageId,
    string? NewVersion,
    string? TrustConfigPath,
    bool UseFast = false
);

/// <summary>
/// Options for previewing the full restore graph for a project.
/// </summary>
/// <param name="Path">Path to a .csproj, .sln, .slnx, or directory.</param>
/// <param name="TargetFramework">The target framework moniker (TFM) used for dependency resolution. Null means auto-detect from the project file.</param>
/// <param name="TrustConfigPath">Optional path to a TrustConfig.json file.</param>
/// <param name="UseFast">
/// When true, uses the approximate BFS resolver instead of running <c>dotnet restore</c>.
/// Faster but may not match what <c>dotnet restore</c> actually selects.
/// </param>
public record PreviewRestoreOptions(
    string Path,
    string? TargetFramework,
    string? TrustConfigPath,
    bool UseFast = false
);
