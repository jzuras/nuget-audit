using NugetAudit.Core.Models;

namespace NugetAudit.Core.DependencyGraph;

/// <summary>
/// A deduplicated package entry produced by <see cref="DotnetListPackageRunner"/>.
/// Combines the package identity with the resolved dependency type.
/// </summary>
/// <param name="Id">The NuGet package identifier (original casing from dotnet output).</param>
/// <param name="Version">The resolved concrete version string.</param>
/// <param name="DependencyType">Whether this package is a direct or transitive dependency.</param>
public sealed record PackageListEntry(string Id, string Version, DependencyType DependencyType);
