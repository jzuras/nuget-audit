using NugetAudit.Cli.Output;
using NugetAudit.Core;
using NugetAudit.Core.Models;
using NugetAudit.Core.Services;
using Spectre.Console;
using System.CommandLine;

namespace NugetAudit.Cli.Commands;

/// <summary>
/// Builds the <c>preview-restore</c> subcommand: previews the full dependency graph
/// that would result from restoring a project.
/// </summary>
internal static class PreviewRestoreCommand
{


    /// <summary>
    /// Creates and returns the configured <c>preview-restore</c> <see cref="Command"/>.
    /// </summary>
    internal static Command Create()
    {
        var pathOption = new Option<string>("--path")
        {
            Description = "Path to the project, solution, or directory.",
            DefaultValueFactory = _ => "."
        };

        var frameworkOption = new Option<string?>("--framework")
        {
            Description = "Target framework moniker (TFM) for dependency resolution. Auto-detected from the project file when omitted."
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
            "preview-restore",
            "Preview the full dependency graph that would result from restoring a project.")
        {
            pathOption,
            frameworkOption,
            trustConfigOption,
            fastOption
        };

        command.SetAction(parseResult =>
        {
            string path = parseResult.GetValue(pathOption) ?? ".";
            string? framework = parseResult.GetValue(frameworkOption);
            string? trustConfigPath = parseResult.GetValue(trustConfigOption);
            bool useFast = parseResult.GetValue(fastOption);

            if (CommandHelpers.HasQuotedTrailingBackslash(path) || CommandHelpers.HasQuotedTrailingBackslash(trustConfigPath))
            {
                CommandHelpers.RenderTrailingBackslashError();
                Environment.ExitCode = 1;
                return;
            }

            int exitCode = RunPreviewRestoreAsync(
                path, framework, trustConfigPath, useFast,
                CancellationToken.None).GetAwaiter().GetResult();

            Environment.ExitCode = exitCode;
        });

        return command;
    }

    #region Core Logic

    /// <summary>
    /// Runs the preview-restore flow and renders output. Returns the process exit code.
    /// </summary>
    /// <param name="path">Path to the solution, project, or directory.</param>
    /// <param name="targetFramework">TFM used for transitive dependency resolution.</param>
    /// <param name="trustConfigPath">Optional path to TrustConfig.json.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>0 on success, 1 on error.</returns>
    private static async Task<int> RunPreviewRestoreAsync(
        string path,
        string? targetFramework,
        string? trustConfigPath,
        bool useFast,
        CancellationToken ct)
    {
        var services = new ServiceCollection();
        services.AddNugetAuditCoreServices();
        using var provider = services.BuildServiceProvider();
        var previewService = provider.GetRequiredService<IPreviewService>();

        var options = new PreviewRestoreOptions(path, targetFramework, trustConfigPath, UseFast: useFast);

        PreviewRestoreResult result;

        try
        {
            result = await previewService.PreviewRestoreAsync(options, ct);
        }
        catch (System.Xml.XmlException)
        {
            AnsiConsole.MarkupLine("[red]Error: The solution or project file contains invalid XML and could not be parsed.[/]");
            return 1;
        }
        catch (System.Text.Json.JsonException)
        {
            AnsiConsole.MarkupLine("[red]Error: The trust config path does not point to a valid JSON file — make sure you selected TrustConfig.json, not a project or solution file.[/]");
            return 1;
        }
        catch (UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine("[red]Error: Access to the specified file was denied — check file permissions.[/]");
            return 1;
        }
        catch (IOException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: File error: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        Console.WriteLine();

        AnsiConsole.MarkupLine(
            $"[cyan bold]Preview Restore — {result.Added.Length} package(s) in dependency graph[/]");

        if (result.ParseWarnings is { Length: > 0 })
        {
            AnsiConsole.MarkupLine(
                $"[yellow]WARNING: {result.ParseWarnings.Length} project file(s) could not be parsed and were skipped — results may be incomplete.[/]");

            foreach (string w in result.ParseWarnings)
            {
                AnsiConsole.MarkupLine($"[yellow]  {Markup.Escape(w)}[/]");
            }
        }

        if (result.IsApproximate)
        {
            AnsiConsole.MarkupLine("[yellow bold]WARNING: --fast uses an approximate BFS resolver that may not match dotnet restore.[/]");
            AnsiConsole.MarkupLine("[yellow]  Results may differ from what actually gets installed.[/]");
            AnsiConsole.MarkupLine("[yellow]  Do not use for security-critical decisions.[/]");
            Console.WriteLine();
        }

        if (!result.HasTrustConfig)
        {
            AnsiConsole.MarkupLine("[yellow bold]WARNING: No TrustConfig.json found.[/]");
            AnsiConsole.MarkupLine(
                "[yellow]  All packages are evaluated with no trusted owners or trusted packages.[/]");
            AnsiConsole.MarkupLine(
                "[yellow]  Run [bold]nuget-audit init[/] to create a TrustConfig.json, then edit it to add your trusted owners.[/]");
        }

        Console.WriteLine();

        RenderAllPackages(result);
        PreviewOutputHelpers.RenderRecentlyPublished(result.Added, result.RecentDaysThreshold);
        RenderSummary(result, path);

        return 0;
    }

    #endregion

    #region Output Sections

    /// <summary>
    /// Renders the full package table for all resolved packages.
    /// </summary>
    /// <param name="result">The preview restore result.</param>
    private static void RenderAllPackages(PreviewRestoreResult result)
    {
        AnsiConsole.MarkupLine("[cyan bold]=== Resolved Packages ===[/]");

        if (result.Added.Length == 0)
        {
            AnsiConsole.MarkupLine("[white]  (no packages found — check path or project file)[/]");
        }
        else
        {
            TableRenderer.Render(result.Added);
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Renders the summary section: total packages, direct seeds, trust breakdown, action hints.
    /// </summary>
    /// <param name="result">The preview restore result.</param>
    /// <param name="path">The path being previewed (for audit command hint).</param>
    private static void RenderSummary(PreviewRestoreResult result, string path)
    {
        var pkgs = result.Added;

        int verified     = pkgs.Count(p => p.TrustStatus == TrustStatus.Verified);
        int trustedPkg   = pkgs.Count(p => p.TrustStatus == TrustStatus.TrustedPackage);
        int unknownOwner = pkgs.Count(p => p.TrustStatus == TrustStatus.VerifiedUnknownOwner);
        int versionChg   = pkgs.Count(p => p.TrustStatus == TrustStatus.VersionChanged);

        AnsiConsole.MarkupLine("[cyan bold]=== Summary ===[/]");
        AnsiConsole.MarkupLine($"[white]Total packages:  {pkgs.Length}[/]");
        AnsiConsole.MarkupLine($"[white]Direct refs:     {result.DirectRefs.Length}[/]");

        if (result.PrivateFeedCount > 0)
        {
            AnsiConsole.MarkupLine($"[cyan]Private feed:    {result.PrivateFeedCount}[/]");
        }

        Console.WriteLine();
        AnsiConsole.MarkupLine("[white]Packages by trust status:[/]");
        AnsiConsole.MarkupLine($"[green]  Verified:                   {verified}[/]");

        if (trustedPkg > 0)
        {
            AnsiConsole.MarkupLine($"[green]  Manually approved:          {trustedPkg}[/]");
        }

        if (result.PrivateFeedCount > 0)
        {
            AnsiConsole.MarkupLine($"[cyan]  Private feed:               {result.PrivateFeedCount}[/]");
        }

        if (unknownOwner > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]  Verified but untrusted owner:  {unknownOwner}[/]");
        }

        if (versionChg > 0)
        {
            AnsiConsole.MarkupLine($"[red]  Version changed:            {versionChg}[/]");
        }

        string reviewColor = result.NeedsReviewCount > 0 ? "yellow" : "green";
        AnsiConsole.MarkupLine($"[{reviewColor}]  Needs review:               {result.NeedsReviewCount}[/]");

        Console.WriteLine();

        if (result.NeedsReviewCount > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Run [bold]nuget-audit audit --path \"{Markup.Escape(path)}\"[/] after restore to review.[/]");
            Console.WriteLine();
        }

        if (result.IsApproximate)
        {
            AnsiConsole.MarkupLine(
                "[yellow]This is an approximate result (--fast mode).[/]");
            AnsiConsole.MarkupLine(
                "[yellow]Run [bold]dotnet restore[/] followed by [bold]nuget-audit audit[/] for exact results.[/]");
            Console.WriteLine();
        }
    }

    #endregion
}
