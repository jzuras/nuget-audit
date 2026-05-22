using NugetAudit.Cli.Commands;
using NugetAudit.Core.Models;
using Spectre.Console;

namespace NugetAudit.Cli.Output;

/// <summary>
/// Shared output helpers for preview subcommands.
/// </summary>
internal static class PreviewOutputHelpers
{

    /// <summary>
    /// Renders the recently-published section for the supplied package sequence.
    /// Uses the threshold (in days) configured in TrustConfig.json (default: 14 days).
    /// </summary>
    /// <param name="packages">The packages to evaluate for recency.</param>
    /// <param name="threshold">Maximum age in days for a package to be considered recently published.</param>
    internal static void RenderRecentlyPublished(IEnumerable<PackageInfo> packages, int threshold)
    {
        var now = DateTimeOffset.UtcNow;
        var recent = packages
            .Where(p => p.Published.HasValue)
            .Select(p => (pkg: p, days: (int)(now - p.Published!.Value).TotalDays))
            .Where(x => x.days <= threshold)
            .OrderBy(x => x.days)
            .ThenBy(x => x.pkg.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        AnsiConsole.MarkupLine(
            $"[yellow]Recently published (within {threshold} days -- higher supply chain risk):[/]");

        if (recent.Length > 0)
        {
            foreach (var (pkg, days) in recent)
            {
                string dayLabel = days == 0 ? "today" : days == 1 ? "1 day ago" : $"{days} days ago";
                string pubDate = pkg.Published!.Value.ToString("yyyy-MM-dd");
                string trustLabel = TableRenderer.FormatTrustStatus(pkg.TrustStatus);
                string color = CommandHelpers.NeedsReview.Contains(pkg.TrustStatus) ? "red" : "yellow";

                AnsiConsole.MarkupLine(
                    $"[{color}]  {Markup.Escape(pkg.PackageId)} {Markup.Escape(pkg.Version)}  published {pubDate} ({dayLabel})  [[{Markup.Escape(trustLabel)}]][/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]  No packages published within the last {threshold} days.[/]");
        }

        Console.WriteLine();
    }

}
