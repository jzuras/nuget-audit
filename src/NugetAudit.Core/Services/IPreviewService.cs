using NugetAudit.Core.Models;

namespace NugetAudit.Core.Services;

/// <summary>
/// Provides preview operations for evaluating the impact of package changes before they are applied.
/// </summary>
public interface IPreviewService
{
    /// <summary>
    /// Previews the dependency graph changes that would result from adding or updating a single package.
    /// </summary>
    /// <param name="opts">Preview update options including the target package, version, and path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The preview update result showing added, changed, and removed packages.</returns>
    Task<PreviewUpdateResult> PreviewUpdateAsync(PreviewUpdateOptions opts, CancellationToken ct);

    /// <summary>
    /// Previews the full dependency graph that would result from restoring a project from scratch.
    /// </summary>
    /// <param name="opts">Preview restore options including the project path and target framework.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The preview restore result showing all packages that would be added.</returns>
    Task<PreviewRestoreResult> PreviewRestoreAsync(PreviewRestoreOptions opts, CancellationToken ct);
}
