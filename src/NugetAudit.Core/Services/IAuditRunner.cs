using NugetAudit.Core.Models;

namespace NugetAudit.Core.Services;

/// <summary>
/// Orchestrates a full NuGet package audit: dependency enumeration, metadata fetching, trust evaluation, and security checks.
/// </summary>
public interface IAuditRunner
{
    /// <summary>
    /// Runs a full NuGet audit for the specified path and options.
    /// </summary>
    /// <param name="options">Audit configuration including path, filter mode, and trust config location.</param>
    /// <param name="progress">
    /// Optional progress callback invoked with status messages during the audit.
    /// Callers in Blazor components must wrap this with <c>await InvokeAsync(StateHasChanged)</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The complete audit result.</returns>
    Task<AuditResult> RunAuditAsync(
        AuditOptions options,
        Func<string, Task>? progress,
        CancellationToken ct);
}
