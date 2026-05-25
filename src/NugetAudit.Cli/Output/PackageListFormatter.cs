using NugetAudit.Cli.Commands;
using NugetAudit.Core.Models;
using Spectre.Console;

namespace NugetAudit.Cli.Output;

/// <summary>
/// Formats audit results in Package List mode (<c>--package-list</c> flag).
/// Outputs packages grouped by trust status in a format suitable for copy-pasting
/// into TrustConfig.json, mirroring the PS tool's -PackageList output.
/// </summary>
internal static class PackageListFormatter
{

    /// <summary>
    /// Renders all packages grouped by trust status.
    /// Used with <c>--package-list --include-existing</c>.
    /// </summary>
    /// <param name="packages">All audited packages.</param>
    public static void RenderAll(IEnumerable<PackageInfo> packages)
    {
        var groups = packages
            .OrderBy(p => p.TrustStatus.ToString())
            .ThenBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
            .GroupBy(p => p.TrustStatus)
            .OrderBy(g => g.Key.ToString());

        AnsiConsole.MarkupLine("[bold]# All packages with trust status:[/]");
        AnsiConsole.WriteLine();

        foreach (var group in groups)
        {
            string color = TableRenderer.GetTrustColor(group.Key);
            AnsiConsole.MarkupLine($"[{color}]# {group.Key} ({group.Count()} package(s)):[/]");

            foreach (var pkg in group)
            {
                string owners = string.Join(", ", pkg.Owners);
                AnsiConsole.MarkupLine(
                    $"[{color}]  {Markup.Escape(pkg.PackageId)} {Markup.Escape(pkg.Version)}  [[Owners: {Markup.Escape(owners)}]][/]");
            }

            AnsiConsole.WriteLine();
        }
    }

    /// <summary>
    /// Renders only the packages needing review, grouped into three sections:
    /// prefix-verified untrusted owner, unverified, and version-changed.
    /// Includes a TrustedOwners alternative suggestion at the end.
    /// </summary>
    /// <param name="packages">All audited packages.</param>
    public static void RenderNeedsReview(IEnumerable<PackageInfo> packages)
    {
        var needsReview = packages
            .Where(p => CommandHelpers.NeedsReview.Contains(p.TrustStatus))
            .OrderBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (needsReview.Length == 0)
        {
            AnsiConsole.MarkupLine("[green]# All packages are verified or from private feeds - nothing to add![/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Use --include-existing to see all packages with status indicators.[/]");
            return;
        }

        var verifiedUnknownOwner = needsReview
            .Where(p => p.TrustStatus == TrustStatus.VerifiedUnknownOwner)
            .ToArray();

        var untrusted = needsReview
            .Where(p => p.TrustStatus == TrustStatus.Untrusted)
            .ToArray();

        var versionChanged = needsReview
            .Where(p => p.TrustStatus == TrustStatus.VersionChanged)
            .ToArray();

        // Group 1 — Prefix-verified, unknown owner
        if (verifiedUnknownOwner.Length > 0)
        {
            AnsiConsole.MarkupLine("[white]# --- Prefix-verified packages (unknown owner) ---[/]");
            AnsiConsole.MarkupLine("[white]# These packages have nuget.org prefix reservation but their owner is not in[/]");
            AnsiConsole.MarkupLine("[white]# trustedOwners. Pin by version below, or add the owner to trustedOwners (see end).[/]");
            AnsiConsole.WriteLine();

            foreach (var pkg in verifiedUnknownOwner)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]{{ \"id\": \"{Markup.Escape(pkg.PackageId)}\", \"version\": \"{Markup.Escape(pkg.Version)}\" }},[/]");
            }

            AnsiConsole.WriteLine();
        }

        // Group 2 — Unverified packages
        if (untrusted.Length > 0)
        {
            AnsiConsole.MarkupLine("[white]# --- Unverified packages ---[/]");
            AnsiConsole.MarkupLine("[white]# These packages have no nuget.org prefix reservation. Review carefully before trusting.[/]");
            AnsiConsole.WriteLine();

            foreach (var pkg in untrusted)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]{{ \"id\": \"{Markup.Escape(pkg.PackageId)}\", \"version\": \"{Markup.Escape(pkg.Version)}\" }},[/]");
            }

            AnsiConsole.WriteLine();
        }

        // Group 3 — Version changed (previously trusted, re-review required)
        if (versionChanged.Length > 0)
        {
            AnsiConsole.MarkupLine("[white]# --- Previously trusted, version changed (re-review required) ---[/]");
            AnsiConsole.MarkupLine("[white]# These packages were in trustedPackages but the version has changed.[/]");
            AnsiConsole.MarkupLine("[white]# Update the version entry after reviewing the new version.[/]");
            AnsiConsole.WriteLine();

            foreach (var pkg in versionChanged)
            {
                AnsiConsole.MarkupLine(
                    $"[red]{{ \"id\": \"{Markup.Escape(pkg.PackageId)}\", \"version\": \"{Markup.Escape(pkg.Version)}\" }},  # was previously trusted at a different version[/]");
            }

            AnsiConsole.WriteLine();
        }

        // TrustedOwners alternative for prefix-verified packages.
        if (verifiedUnknownOwner.Length > 0)
        {
            var uniqueOwners = verifiedUnknownOwner
                .SelectMany(p => p.Owners)
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(o => o, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (uniqueOwners.Length > 0)
            {
                AnsiConsole.MarkupLine("[white]# --- trustedOwners alternative (prefix-verified packages only) ---[/]");
                AnsiConsole.MarkupLine("[white]# Instead of pinning by version, add these publishers to trustedOwners to trust[/]");
                AnsiConsole.MarkupLine("[white]# all current and future versions. See TrustConfig.json for the trade-offs.[/]");
                AnsiConsole.WriteLine();

                foreach (string owner in uniqueOwners)
                {
                    AnsiConsole.MarkupLine($"[cyan]\"{Markup.Escape(owner)}\",[/]");
                }

                AnsiConsole.WriteLine();
            }
        }

        AnsiConsole.MarkupLine("[grey]NOTE: Verified packages and private-feed packages are not shown.[/]");
        AnsiConsole.MarkupLine("[grey]      Use --include-existing to see all packages with status indicators.[/]");
    }

}
