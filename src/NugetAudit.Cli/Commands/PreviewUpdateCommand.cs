using NugetAudit.Cli.Output;
using NugetAudit.Core;
using NugetAudit.Core.Models;
using NugetAudit.Core.Services;
using Spectre.Console;
using System.CommandLine;

namespace NugetAudit.Cli.Commands;

/// <summary>
/// Builds the <c>preview-update</c> subcommand: previews the dependency graph impact
/// of adding or updating a NuGet package.
/// </summary>
internal static class PreviewUpdateCommand
{


    /// <summary>
    /// Creates and returns the configured <c>preview-update</c> <see cref="Command"/>.
    /// </summary>
    internal static Command Create()
    {
        var packageIdArgument = new Argument<string>("package-id")
        {
            Description = "The NuGet package identifier to add or update."
        };

        var pathOption = new Option<string>("--path")
        {
            Description = "Path to the solution, project, or directory.",
            DefaultValueFactory = _ => "."
        };

        var versionOption = new Option<string?>("--version")
        {
            Description = "Target version. Omit to resolve to the latest stable version."
        };

        var trustConfigOption = new Option<string?>("--trust-config")
        {
            Description = "Path to TrustConfig.json, or a directory containing it. Defaults to the solution directory."
        };

        var fastOption = new Option<bool>("--fast")
        {
            Description = "Use the approximate BFS resolver instead of running dotnet restore. Faster but may not match the actual restore graph. Not recommended for security-critical decisions."
        };

        var command = new Command(
            "preview-update",
            "Preview the dependency graph impact of adding or updating a NuGet package.")
        {
            pathOption,
            versionOption,
            trustConfigOption,
            fastOption
        };

        command.Arguments.Add(packageIdArgument);

        command.SetAction(parseResult =>
        {
            string packageId = parseResult.GetValue(packageIdArgument) ?? string.Empty;
            string path = parseResult.GetValue(pathOption) ?? ".";
            string? version = parseResult.GetValue(versionOption);
            string? trustConfigPath = parseResult.GetValue(trustConfigOption);
            bool useFast = parseResult.GetValue(fastOption);

            if (CommandHelpers.HasQuotedTrailingBackslash(path) || CommandHelpers.HasQuotedTrailingBackslash(trustConfigPath))
            {
                CommandHelpers.RenderTrailingBackslashError();
                Environment.ExitCode = 1;
                return;
            }

            int exitCode = RunPreviewUpdateAsync(
                packageId, path, version, trustConfigPath, useFast,
                CancellationToken.None).GetAwaiter().GetResult();

            Environment.ExitCode = exitCode;
        });

        return command;
    }

    #region Core Logic

    /// <summary>
    /// Runs the preview-update flow and renders output. Returns the process exit code.
    /// </summary>
    /// <param name="packageId">The package to add or update.</param>
    /// <param name="path">Path to the solution, project, or directory.</param>
    /// <param name="version">Target version; null resolves latest stable.</param>
    /// <param name="trustConfigPath">Optional path to TrustConfig.json.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>0 on success, 1 on error.</returns>
    private static async Task<int> RunPreviewUpdateAsync(
        string packageId,
        string path,
        string? version,
        string? trustConfigPath,
        bool useFast,
        CancellationToken ct)
    {
        var services = new ServiceCollection();
        services.AddNugetAuditCoreServices();
        using var provider = services.BuildServiceProvider();
        var previewService = provider.GetRequiredService<IPreviewService>();

        var options = new PreviewUpdateOptions(path, packageId, version, trustConfigPath, UseFast: useFast);

        PreviewUpdateResult result;

        try
        {
            result = await previewService.PreviewUpdateAsync(options, ct);
        }
        catch (System.Xml.XmlException)
        {
            Console.WriteLine();
            AnsiConsole.MarkupLine("[red]Error: The solution or project file contains invalid XML and could not be parsed.[/]");
            return 1;
        }
        catch (System.Text.Json.JsonException)
        {
            Console.WriteLine();
            AnsiConsole.MarkupLine("[red]Error: The trust config path does not point to a valid JSON file — make sure you selected TrustConfig.json, not a project or solution file.[/]");
            return 1;
        }
        catch (UnauthorizedAccessException)
        {
            Console.WriteLine();
            AnsiConsole.MarkupLine("[red]Error: Access to the specified file was denied — check file permissions.[/]");
            return 1;
        }
        catch (IOException ex)
        {
            Console.WriteLine();
            AnsiConsole.MarkupLine($"[red]Error: File error: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            if (ex.Data.Contains("VersionNote") && ex.Data["VersionNote"] is string note)
            {
                AnsiConsole.MarkupLine($"[yellow]Note: {Markup.Escape(note)}[/]");
                Console.WriteLine();
            }

            AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        Console.WriteLine();

        if (result.IsPartialResult)
        {
            RenderPartialResult(result, packageId);
            return 1;
        }

        if (result.IsApproximate)
        {
            if (useFast)
            {
                AnsiConsole.MarkupLine("[yellow bold]WARNING: --fast uses an approximate BFS resolver that may not match dotnet restore.[/]");
                AnsiConsole.MarkupLine("[yellow]  Results may differ from what actually gets installed.[/]");
                AnsiConsole.MarkupLine("[yellow]  Do not use for security-critical decisions.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow bold]WARNING: This package is on a private feed. Exact restore is not supported for private feeds — results are approximate.[/]");
                AnsiConsole.MarkupLine("[yellow]  Results may differ from what actually gets installed.[/]");
                AnsiConsole.MarkupLine("[yellow]  Do not use for security-critical decisions.[/]");
            }

            Console.WriteLine();
        }

        if (!string.IsNullOrWhiteSpace(result.VersionNote))
        {
            AnsiConsole.MarkupLine($"[yellow]Note: {Markup.Escape(result.VersionNote)}[/]");
            Console.WriteLine();
        }

        if (result.IsPrivateToPublicTransition)
        {
            RenderPrivateToPublicWarning(result, packageId);
        }

        string action = result.IsNewPackage ? "ADD" : "UPDATE";
        string? currentVersion = result.Changed
            .FirstOrDefault(c => string.Equals(c.Package.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
            ?.OldVersion;
        string fromPart = currentVersion is not null ? $" {Markup.Escape(currentVersion)}" : string.Empty;
        AnsiConsole.MarkupLine(
            $"[cyan bold]Previewing {action}: {Markup.Escape(packageId)}{fromPart} → {Markup.Escape(result.ResolvedVersion)}[/]");
        Console.WriteLine();

        if (!result.HasTrustConfig)
        {
            AnsiConsole.MarkupLine("[yellow bold]WARNING: No TrustConfig.json found.[/]");
            AnsiConsole.MarkupLine(
                "[yellow]  All packages are evaluated with no trusted owners or trusted packages.[/]");
            AnsiConsole.MarkupLine(
                "[yellow]  Run [bold]nuget-audit init[/] to create a TrustConfig.json, then edit it to add your trusted owners.[/]");
            Console.WriteLine();
        }

        RenderAddedSection(result);
        RenderChangedSection(result);
        RenderRemovedSection(result);
        PreviewOutputHelpers.RenderRecentlyPublished(
            result.Added.Concat(result.Changed.Select(c => c.Package)),
            result.RecentDaysThreshold);
        RenderSummary(result);

        return 0;
    }

    #endregion

    #region Output Sections

    /// <summary>
    /// Renders a supply chain warning when a package has moved from a private feed to nuget.org.
    /// Uses red for unverified publishers (potential attack) and yellow for verified ones
    /// (likely legitimate vendor move, but still worth confirming).
    /// </summary>
    /// <param name="result">The preview result containing trust data.</param>
    /// <param name="packageId">The package being updated.</param>
    private static void RenderPrivateToPublicWarning(PreviewUpdateResult result, string packageId)
    {
        // Find the trust status of the target package in the changed or added list.
        TrustStatus trust = result.Changed
            .FirstOrDefault(c => string.Equals(
                c.Package.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
            ?.Package.TrustStatus
            ?? result.Added
                .FirstOrDefault(p => string.Equals(
                    p.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
                ?.TrustStatus
            ?? TrustStatus.Untrusted;

        if (IsHighSeverityTransition(trust))
        {
            AnsiConsole.MarkupLine(
                $"[red bold]⛔ Supply chain alert:[/] [red]{Markup.Escape(packageId)} appeared on " +
                "nuget.org for the first time with no prefix reservation. " +
                "This may indicate a supply chain attack. " +
                "Do not update without independently verifying the publisher.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[yellow bold]⚠ Supply chain notice:[/] [yellow]{Markup.Escape(packageId)} was " +
                "previously only available on a private feed and has now appeared on nuget.org. " +
                "Verify this is an intentional vendor release before updating.[/]");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Renders the partial result message when the preview cannot be completed.
    /// </summary>
    /// <param name="result">The partial result.</param>
    /// <param name="packageId">The requested package ID.</param>
    private static void RenderPartialResult(PreviewUpdateResult result, string packageId)
    {
        AnsiConsole.MarkupLine(
            $"[yellow]Cannot preview {Markup.Escape(packageId)}.[/]");

        switch (result.PartialResultReason)
        {
            case "VersionRequired":
                AnsiConsole.MarkupLine(
                    "[yellow]  This package is not on nuget.org. To search private feeds, specify --version.[/]");
                break;

            case "CredentialsUnavailable":
                AnsiConsole.MarkupLine(
                    "[yellow]  This package is not on nuget.org. Private feed search was attempted but credentials could not be resolved. Ensure NuGet.Config or a credential provider is configured.[/]");
                break;

            default:
                if (!string.IsNullOrWhiteSpace(result.PartialResultReason))
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]  Reason: {Markup.Escape(result.PartialResultReason)}[/]");
                }

                break;
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Renders the ADDED section: packages that would be newly added to the dependency graph.
    /// </summary>
    /// <param name="result">The preview result.</param>
    private static void RenderAddedSection(PreviewUpdateResult result)
    {
        AnsiConsole.MarkupLine("[cyan bold]=== ADDED ===[/]");

        if (result.Added.Length == 0)
        {
            AnsiConsole.MarkupLine("[white]  (none)[/]");
        }
        else
        {
            TableRenderer.Render(result.Added);
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Renders the CHANGED section: packages whose versions would change.
    /// </summary>
    /// <param name="result">The preview result.</param>
    private static void RenderChangedSection(PreviewUpdateResult result)
    {
        AnsiConsole.MarkupLine("[cyan bold]=== CHANGED ===[/]");

        if (result.Changed.Length == 0)
        {
            AnsiConsole.MarkupLine("[white]  (none)[/]");
        }
        else
        {
            foreach (var entry in result.Changed.OrderBy(e => e.Package.PackageId, StringComparer.OrdinalIgnoreCase))
            {
                string color = CommandHelpers.NeedsReview.Contains(entry.Package.TrustStatus) ? "yellow" : "white";
                string trustLabel = TableRenderer.FormatTrustStatus(entry.Package.TrustStatus);

                AnsiConsole.MarkupLine(
                    $"[{color}]  {Markup.Escape(entry.Package.PackageId)}  {Markup.Escape(entry.OldVersion)} → {Markup.Escape(entry.Package.Version)}  [[{Markup.Escape(trustLabel)}]][/]");
            }
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Renders the REMOVED section: packages that would be removed from the dependency graph.
    /// </summary>
    /// <param name="result">The preview result.</param>
    private static void RenderRemovedSection(PreviewUpdateResult result)
    {
        AnsiConsole.MarkupLine("[cyan bold]=== REMOVED ===[/]");

        if (result.Removed.Length == 0)
        {
            AnsiConsole.MarkupLine("[white]  (none)[/]");
        }
        else
        {
            foreach (string id in result.Removed.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine($"[white]  {Markup.Escape(id)}[/]");
            }
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Renders the summary section: counts of added, changed, removed, and trust review required.
    /// </summary>
    /// <param name="result">The preview result.</param>
    private static void RenderSummary(PreviewUpdateResult result)
    {
        int reviewCount = result.Added.Count(p => CommandHelpers.NeedsReview.Contains(p.TrustStatus))
            + result.Changed.Count(p => CommandHelpers.NeedsReview.Contains(p.Package.TrustStatus));

        AnsiConsole.MarkupLine("[cyan bold]=== Summary ===[/]");
        AnsiConsole.MarkupLine($"[white]  Added:   {result.Added.Length}[/]");
        AnsiConsole.MarkupLine($"[white]  Changed: {result.Changed.Length}[/]");
        AnsiConsole.MarkupLine($"[white]  Removed: {result.Removed.Length}[/]");

        string reviewColor = reviewCount > 0 ? "yellow" : "green";
        AnsiConsole.MarkupLine($"[{reviewColor}]  Needs trust review: {reviewCount}[/]");
        Console.WriteLine();

        if (reviewCount > 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]Run [bold]nuget-audit audit[/] after adding the package to review trust status.[/]");
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the transition trust status warrants a red supply
    /// chain alert. <see cref="TrustStatus.Untrusted"/> (no prefix reservation) and
    /// <see cref="TrustStatus.VerifiedUnknownOwner"/> (prefix-reserved but unknown to user)
    /// are high severity. All other statuses produce a yellow informational notice.
    /// </summary>
    /// <param name="trust">Trust status of the package's new nuget.org version.</param>
    internal static bool IsHighSeverityTransition(TrustStatus trust)
        => trust is TrustStatus.Untrusted or TrustStatus.VerifiedUnknownOwner;

    #endregion
}
