namespace NugetAudit.Core.DependencyGraph;

/// <summary>
/// Root DTO for deserializing the JSON output of `dotnet list package --include-transitive --format json`.
/// </summary>
/// <param name="Projects">All projects found in the solution or project path.</param>
public record DotnetListOutput(DotnetListProject[] Projects);

/// <summary>
/// A single project entry in the dotnet list package JSON output.
/// </summary>
/// <param name="Path">The absolute path to the .csproj file.</param>
/// <param name="Frameworks">Per-framework package lists for this project.</param>
public record DotnetListProject(string Path, DotnetListFramework[] Frameworks);

/// <summary>
/// Per-framework package data within a project from the dotnet list package JSON output.
/// </summary>
/// <param name="Framework">The target framework moniker (e.g., "net10.0").</param>
/// <param name="TopLevelPackages">Direct package references; may be null if no direct references.</param>
/// <param name="TransitivePackages">Transitive package references; may be null if no transitives.</param>
public record DotnetListFramework(
    string Framework,
    DotnetListPackageRef[]? TopLevelPackages,
    DotnetListPackageRef[]? TransitivePackages
);

/// <summary>
/// A single package reference entry from the dotnet list package JSON output.
/// </summary>
/// <param name="Id">The NuGet package identifier.</param>
/// <param name="RequestedVersion">The version expression from the project file (may be a range or "*" for CPM).</param>
/// <param name="ResolvedVersion">The concrete version that was resolved during restore.</param>
public record DotnetListPackageRef(
    string Id,
    string RequestedVersion,
    string ResolvedVersion
);
