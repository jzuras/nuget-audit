using NugetAudit.Core.Models;

namespace NugetAudit.Core.Services;

/// <summary>
/// Resolves NuGet dependency graphs using BFS traversal of the NuGet Registration API.
/// Provides two distinct BFS modes: delta resolution for package updates, and full resolution for new restores.
/// </summary>
public interface IDependencyGraphResolver
{
    /// <summary>
    /// Resolves the dependency graph delta caused by adding or updating a single package.
    /// Performs BFS on top of the existing resolved graph, computing adds, changes, and removals.
    /// </summary>
    /// <param name="packageId">The NuGet package identifier being added or updated.</param>
    /// <param name="newVersion">The new version of the package.</param>
    /// <param name="currentGraph">The current resolved dependency graph keyed by lowercased package ID.</param>
    /// <param name="feedInfo">Private feed information for the target package; null for public packages.</param>
    /// <param name="targetFramework">The target framework moniker used for dependency resolution.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The preview update result showing adds, changes, and removals.</returns>
    Task<PreviewUpdateResult> ResolveDeltaAsync(
        string packageId,
        string newVersion,
        IReadOnlyDictionary<string, PackageEntry> currentGraph,
        FeedInfo? feedInfo,
        string targetFramework,
        CancellationToken ct);

    /// <summary>
    /// Resolves the full dependency graph starting from a set of direct package references.
    /// Performs BFS from an empty graph, suitable for previewing a fresh restore.
    /// </summary>
    /// <param name="seeds">The direct package references to use as BFS seeds.</param>
    /// <param name="targetFramework">The target framework moniker used for dependency resolution.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The preview restore result showing all packages in the resolved graph.</returns>
    Task<PreviewRestoreResult> ResolveFullAsync(
        IReadOnlyList<PackageRef> seeds,
        string targetFramework,
        CancellationToken ct);
}
