using NugetAudit.Cli.Output;
using NugetAudit.Core;
using NugetAudit.Core.Models;
using NugetAudit.Core.Services;
using Spectre.Console;
using System.CommandLine;

namespace NugetAudit.Cli.Commands;

/// <summary>
/// Builds and handles the <c>audit</c> subcommand: runs a NuGet package security audit
/// and renders results as a table, CSV, JSON, or package list.
/// </summary>
internal static class AuditCommand
{
    #region Trust Status Sets

    /// <summary>
    /// Gets the set of trust statuses considered trusted/verified for output condensing purposes.
    /// </summary>
    private static HashSet<TrustStatus> Trusted { get; } =
    [
        TrustStatus.Verified,
        TrustStatus.TrustedPackage,
        TrustStatus.PrivateFeed,
    ];

    /// <summary>
    /// Gets the maximum number of transitive packages shown per primary owner in the table
    /// before the remainder is collapsed into a suppressed-count note.
    /// Exec-content packages always appear and count toward this limit.
    /// </summary>
    private const int TransitivePerOwnerLimit = 2;

    #endregion

    /// <summary>
    /// Creates and returns the configured <c>audit</c> <see cref="Command"/>.
    /// </summary>
    /// <returns>A configured System.CommandLine <see cref="Command"/> for the audit subcommand.</returns>
    internal static Command Create()
    {
        var pathOption = new Option<string>("--path")
        {
            Description = "Path to the solution, project, or directory to audit.",
            DefaultValueFactory = _ => ".",
        };

        var allOption = new Option<bool>("--all")
        {
            Description = "Show all packages, not just those needing review.",
        };

        var packageListOption = new Option<bool>("--package-list")
        {
            Description = "Output as a grouped package list for copy-pasting into TrustConfig.json.",
        };

        var includeExistingOption = new Option<bool>("--include-existing")
        {
            Description = "Include existing trusted packages in the output (use with --package-list).",
        };

        var checkOption = new Option<bool>("--check")
        {
            Description = "Exit with code 1 if any packages need review, are deprecated, have vulnerabilities, or if security advisories are present (missing lock file, RestoreLockedMode, pre-build target, or Package Source Mapping).",
        };

        var formatOption = new Option<string>("--format")
        {
            Description = "Output format: table, csv, or json.",
            DefaultValueFactory = _ => "table",
        };

        var outputOption = new Option<string?>("--output")
        {
            Description = "Write output to the specified file. Requires --format csv or --format json.",
        };

        var trustConfigOption = new Option<string?>("--trust-config")
        {
            Description = "Path to TrustConfig.json, or a directory containing it. Defaults to the solution directory.",
        };

        var verboseOption = new Option<bool>("--verbose")
        {
            Description = "Show full output without condensing: all transitive packages, all recently-published entries.",
        };

        var command = new Command("audit", "Run a NuGet package security audit.")
        {
            pathOption,
            allOption,
            packageListOption,
            includeExistingOption,
            checkOption,
            formatOption,
            outputOption,
            trustConfigOption,
            verboseOption,
        };

        command.SetAction(parseResult =>
        {
            string path = parseResult.GetValue(pathOption) ?? ".";
            bool showAll = parseResult.GetValue(allOption);
            bool packageList = parseResult.GetValue(packageListOption);
            bool includeExisting = parseResult.GetValue(includeExistingOption);
            bool check = parseResult.GetValue(checkOption);
            string format = parseResult.GetValue(formatOption) ?? "table";
            string? outputFile = parseResult.GetValue(outputOption);
            string? trustConfigPath = parseResult.GetValue(trustConfigOption);
            bool verbose = parseResult.GetValue(verboseOption);

            if (CommandHelpers.HasQuotedTrailingBackslash(path) || CommandHelpers.HasQuotedTrailingBackslash(trustConfigPath))
            {
                CommandHelpers.RenderTrailingBackslashError();
                Environment.ExitCode = 1;
                return;
            }

            int exitCode = RunAuditAsync(
                path, showAll, packageList, includeExisting, check, format, outputFile, trustConfigPath, verbose,
                CancellationToken.None).GetAwaiter().GetResult();

            Environment.ExitCode = exitCode;
        });

        return command;
    }

    #region Core Logic

    /// <summary>
    /// Runs the audit and renders output. Returns the process exit code.
    /// </summary>
    /// <param name="path">Path to audit.</param>
    /// <param name="showAll">Show all packages, not just those needing review.</param>
    /// <param name="packageList">Output in package list mode.</param>
    /// <param name="includeExisting">Include all packages in package list mode.</param>
    /// <param name="check">Exit with code 1 on issues.</param>
    /// <param name="format">Output format: table, csv, or json.</param>
    /// <param name="outputFile">Optional output file path.</param>
    /// <param name="trustConfigPath">Optional TrustConfig.json path.</param>
    /// <param name="verbose">Show full output without condensing any sections.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>0 on success (or no issues), 1 on error or issues in --check mode.</returns>
    private static async Task<int> RunAuditAsync(
        string path,
        bool showAll,
        bool packageList,
        bool includeExisting,
        bool check,
        string format,
        string? outputFile,
        string? trustConfigPath,
        bool verbose,
        CancellationToken ct)
    {
        // Validate --output is only used with csv or json format.
        bool isTableFormat = string.Equals(format, "table", StringComparison.OrdinalIgnoreCase);
        if (outputFile is not null && isTableFormat)
        {
            AnsiConsole.MarkupLine("[red]Error: --output requires --format csv or --format json. Table output cannot be written to a file.[/]");
            AnsiConsole.MarkupLine("[yellow]  Use shell redirection instead: nuget-audit audit [[options]] > out.txt[/]");
            return 1;
        }

        // Build DI container.
        var services = new ServiceCollection();
        services.AddNugetAuditCoreServices();
        using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<IAuditRunner>();

        var options = new AuditOptions(path, showAll, packageList, includeExisting, check, trustConfigPath);

        AuditResult result;
        int lastProgressLen = 0;

        // Suppress progress when stdout is redirected (CI pipelines, file output) or in --check mode.
        // \r line-overwriting does not work in redirected output, and --check is a CI-first flag.
        bool showProgress = !Console.IsOutputRedirected && !check;

        try
        {
            result = await runner.RunAuditAsync(
                options,
                msg =>
                {
                    if (showProgress is true)
                    {
                        string padded = msg.PadRight(lastProgressLen);
                        lastProgressLen = msg.Length;
                        Console.Write($"\r{padded}");
                    }

                    return Task.CompletedTask;
                },
                ct);
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

        // Clear the progress line before rendering output.
        if (showProgress is true)
        {
            Console.Write($"\r{new string(' ', lastProgressLen)}\r");
        }

        Console.WriteLine();

        // --- Package List Mode ---
        if (packageList is true)
        {
            RenderPackageListOutput(result, includeExisting);
            return 0;
        }

        // All packages that need review (used for counts and as the base for CSV/JSON).
        PackageInfo[] allNeedsReview = showAll
            ? result.Packages
            : result.Packages.Where(p => CommandHelpers.NeedsReview.Contains(p.TrustStatus)).ToArray();

        // --- Check Mode ---
        if (check is true)
        {
            return RenderCheckOutput(result);
        }

        // For table format: apply per-owner transitive grouping unless --all or --verbose.
        // CSV/JSON always receives the full set (machine-readable output should be complete).
        bool applyGrouping = isTableFormat && !showAll && !verbose;

        PackageInfo[] tablePackages;
        int suppressedTransitiveCount = 0;
        PackageInfo[] renderedTransNeedsReview;

        if (applyGrouping)
        {
            var directNeedsReview = allNeedsReview
                .Where(p => p.DependencyType == DependencyType.Direct)
                .ToArray();

            var transitiveNeedsReview = allNeedsReview
                .Where(p => p.DependencyType == DependencyType.Transitive)
                .ToArray();

            var (groupedTrans, suppressed) = ApplyTransitiveGrouping(transitiveNeedsReview);
            suppressedTransitiveCount = suppressed;
            tablePackages = [.. directNeedsReview, .. groupedTrans];
            renderedTransNeedsReview = groupedTrans;
        }
        else
        {
            tablePackages = allNeedsReview;
            renderedTransNeedsReview = allNeedsReview
                .Where(p => p.DependencyType == DependencyType.Transitive)
                .ToArray();
        }

        // --- Normal Table/CSV/JSON Output ---
        if (!showAll)
        {
            Console.WriteLine(
                $"Filtering to show only packages needing review... Found {allNeedsReview.Length} package(s) needing review.");
            Console.WriteLine();
        }

        if (tablePackages.Length > 0)
        {
            RenderPackageOutput(tablePackages, format, outputFile);

            if (suppressedTransitiveCount > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[grey]{suppressedTransitiveCount} more transitive package(s) from owners already listed — use [bold]--verbose[/] for full list.[/]");
            }

            Console.WriteLine();
        }
        else
        {
            AnsiConsole.MarkupLine("[green]No packages match the current filter criteria.[/]");
        }

        // Summary and advisories always rendered in normal mode.
        RenderNoTrustConfigWarning(result);
        RenderSummary(result);
        RenderSecurityConcerns(result);
        RenderRecentlyPublished(result, verbose);
        RenderExecContentAlert(result);
        RenderActionRequired(result);
        RenderTransitiveNuGetWhy(renderedTransNeedsReview, path);
        RenderAdvisories(result);

        if (!showAll)
        {
            AnsiConsole.MarkupLine("[grey]Run [bold]nuget-audit audit --all[/] to show all packages in the table.[/]");
        }

#if INCLUDE_UI
        AnsiConsole.MarkupLine("[grey]Run [bold]nuget-audit ui[/] to open the interactive browser UI.[/]");
        Console.WriteLine();
#endif

        RenderSponsorNudge();

        return 0;
    }

    /// <summary>
    /// Displays a sponsor message on 20% of interactive runs.
    /// Suppressed when stdout is redirected or NUGET_AUDIT_NO_SPONSOR is set.
    /// </summary>
    private static void RenderSponsorNudge()
    {
        if (Console.IsOutputRedirected) return;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_AUDIT_NO_SPONSOR"))) return;
        if (Random.Shared.NextDouble() >= 0.20) return;

        Console.WriteLine();
        AnsiConsole.MarkupLine("[grey]nuget-audit is free. If it's useful, consider sponsoring: https://www.nuget.org/packages/nuget-audit  (set NUGET_AUDIT_NO_SPONSOR=1 to disable)[/]");
    }

    #endregion

    #region Transitive Grouping

    /// <summary>
    /// Applies per-owner grouping to transitive packages needing review.
    /// Up to <see cref="TransitivePerOwnerLimit"/> packages are shown per primary owner.
    /// Packages with executable content, deprecation, or vulnerabilities are always included
    /// and count toward the limit. Plain packages (none of these flags) fill remaining slots.
    /// </summary>
    /// <param name="transNeedsReview">All transitive packages needing review.</param>
    /// <returns>
    /// The filtered set to display, and the count of packages that were suppressed.
    /// </returns>
    private static (PackageInfo[] Filtered, int Suppressed) ApplyTransitiveGrouping(
        PackageInfo[] transNeedsReview)
    {
        var display = new List<PackageInfo>();
        int suppressed = 0;

        // Group by primary owner (first element of Owners array).
        var groups = transNeedsReview
            .GroupBy(p => p.Owners.Length > 0 ? p.Owners[0] : string.Empty,
                StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            // Always show packages with exec content, deprecation, or vulnerabilities —
            // each is independently actionable regardless of the per-owner limit.
            var alwaysShow = group
                .Where(p => p.ExecutableContent?.Length > 0 || p.IsDeprecated || p.HasVulnerabilities)
                .OrderBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var plain = group
                .Where(p => p.ExecutableContent is null || p.ExecutableContent.Length == 0)
                .Where(p => !p.IsDeprecated && !p.HasVulnerabilities)
                .OrderBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var pkg in alwaysShow)
            {
                display.Add(pkg);
            }

            // Fill remaining slots up to the per-owner limit with plain packages.
            int remaining = Math.Max(0, TransitivePerOwnerLimit - alwaysShow.Count);

            foreach (var pkg in plain)
            {
                if (remaining > 0)
                {
                    display.Add(pkg);
                    remaining--;
                }
                else
                {
                    suppressed++;
                }
            }
        }

        return ([.. display], suppressed);
    }

    #endregion

    #region Output Sections

    /// <summary>
    /// Renders output in --check mode: counts only, no table.
    /// Exits with code 1 if any package issues are found or if any security advisories are present.
    /// Security advisories cover setup gaps that undermine the zero-trust workflow:
    /// missing lock file, missing RestoreLockedMode, missing pre-build target, or missing PSM.
    /// </summary>
    /// <param name="result">The audit result.</param>
    /// <returns>0 if no issues or advisories, 1 if issues or advisories found.</returns>
    private static int RenderCheckOutput(AuditResult result)
    {
        int trustIssues = result.Packages
            .Count(p => CommandHelpers.NeedsReview.Contains(p.TrustStatus));
        int deprecated = result.Packages.Count(p => p.IsDeprecated);
        int vulnerable = result.Packages.Count(p => p.HasVulnerabilities);

        string trustColor = trustIssues > 0 ? "yellow" : "green";
        string deprColor = deprecated > 0 ? "red" : "green";
        string vulnColor = vulnerable > 0 ? "red" : "green";

        AnsiConsole.MarkupLine(
            $"[{trustColor}]Packages needing trust review: {trustIssues}[/]");
        AnsiConsole.MarkupLine(
            $"[{deprColor}]Deprecated packages:           {deprecated}[/]");
        AnsiConsole.MarkupLine(
            $"[{vulnColor}]Packages with vulnerabilities: {vulnerable}[/]");

        // Advisory conditions — setup gaps that undermine the zero-trust workflow.
        bool hasLockAdvisory = result.LockFileStatus is not LockFileStatus.LockedAndEnforced;
        bool hasPsmAdvisory = result.PsmStatus is PackageSourceMappingStatus.NotConfigured;
        bool hasAdvisoryIssue = hasLockAdvisory || hasPsmAdvisory;

        if (hasAdvisoryIssue)
        {
            Console.WriteLine();
        }

        if (result.LockFileStatus is LockFileStatus.NoLockFile)
        {
            AnsiConsole.MarkupLine("[red]Security advisory: No packages.lock.json found — lock file enforcement is missing.[/]");
        }
        else if (result.LockFileStatus is LockFileStatus.LockFileNoEnforcement)
        {
            AnsiConsole.MarkupLine("[red]Security advisory: packages.lock.json found but RestoreLockedMode is not set.[/]");
        }
        else if (result.LockFileStatus is LockFileStatus.LockedEnforcedNoBuildTarget)
        {
            AnsiConsole.MarkupLine("[red]Security advisory: RestoreLockedMode=true is set but no Directory.Build.targets with a nuget-audit invocation was found.[/]");
        }

        if (hasPsmAdvisory)
        {
            AnsiConsole.MarkupLine("[red]Security advisory: Package Source Mapping is not configured — dependency confusion risk with multiple feeds.[/]");
        }

        if (result.HasIssues || hasAdvisoryIssue)
        {
            Console.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Run [bold]nuget-audit audit[/] for full details.[/]");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Renders package list output (--package-list mode).
    /// </summary>
    /// <param name="result">The audit result.</param>
    /// <param name="includeExisting">When true, shows all packages grouped by status.</param>
    private static void RenderPackageListOutput(AuditResult result, bool includeExisting)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[cyan bold]=================================================================[/]");
        AnsiConsole.MarkupLine("[cyan bold]  Package List Mode[/]");
        AnsiConsole.MarkupLine("[cyan bold]=================================================================[/]");
        AnsiConsole.WriteLine();

        if (includeExisting)
        {
            PackageListFormatter.RenderAll(result.Packages);
        }
        else
        {
            PackageListFormatter.RenderNeedsReview(result.Packages);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[cyan]Location: Edit TrustConfig.json at the solution root.[/]");
        AnsiConsole.MarkupLine("[cyan]After updating the list, run the audit normally to identify packages needing review.[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Renders the package table, CSV, or JSON output.
    /// </summary>
    /// <param name="packages">The filtered packages to display.</param>
    /// <param name="format">Output format: table, csv, or json.</param>
    /// <param name="outputFile">Optional file path for output.</param>
    private static void RenderPackageOutput(PackageInfo[] packages, string format, string? outputFile)
    {
        switch (format.ToLowerInvariant())
        {
            case "csv":
                CsvFormatter.Write(packages, outputFile);
                break;

            case "json":
                JsonFormatter.Write(packages, outputFile);
                break;

            case "table":
            default:
                TableRenderer.Render(packages);
                break;
        }
    }

    /// <summary>
    /// Renders a warning when no TrustConfig.json was found, prompting the user to run init.
    /// </summary>
    /// <param name="result">The audit result.</param>
    private static void RenderNoTrustConfigWarning(AuditResult result)
    {
        if (result.HasTrustConfig)
        {
            return;
        }

        AnsiConsole.MarkupLine("[yellow bold]WARNING: No TrustConfig.json found.[/]");
        AnsiConsole.MarkupLine(
            "[yellow]  All packages are evaluated with no trusted owners or trusted packages.[/]");
        AnsiConsole.MarkupLine(
            "[yellow]  Run [bold]nuget-audit init[/] to create a TrustConfig.json, then edit it to add your trusted owners.[/]");
        Console.WriteLine();
    }

    /// <summary>
    /// Renders the summary section: total counts, breakdown by trust status.
    /// </summary>
    /// <param name="result">The audit result.</param>
    private static void RenderSummary(AuditResult result)
    {
        var pkgs = result.Packages;

        int direct = pkgs.Count(p => p.DependencyType == DependencyType.Direct);
        int transitive = pkgs.Count(p => p.DependencyType == DependencyType.Transitive);
        int verified = pkgs.Count(p => p.TrustStatus == TrustStatus.Verified);
        int privateFeed = pkgs.Count(p => p.TrustStatus == TrustStatus.PrivateFeed);
        int trusted = pkgs.Count(p => p.TrustStatus == TrustStatus.TrustedPackage);
        int unknownOwner = pkgs.Count(p => p.TrustStatus == TrustStatus.VerifiedUnknownOwner);
        int versionChanged = pkgs.Count(p => p.TrustStatus == TrustStatus.VersionChanged);
        int untrusted = pkgs.Count(p => p.TrustStatus == TrustStatus.Untrusted);

        AnsiConsole.MarkupLine("[cyan bold]=== Summary ===[/]");
        AnsiConsole.MarkupLine($"[white]Total packages: {pkgs.Length}  (projects: {result.TotalProjects})[/]");
        AnsiConsole.MarkupLine($"[white]  Direct dependencies:     {direct}[/]");
        AnsiConsole.MarkupLine($"[white]  Transitive dependencies: {transitive}[/]");
        Console.WriteLine();

        AnsiConsole.MarkupLine("[white]Packages by trust status:[/]");
        AnsiConsole.MarkupLine($"[green]  Verified (prefix reserved, trusted owner): {verified}[/]");
        AnsiConsole.MarkupLine($"[cyan]  Private feeds (not on nuget.org):          {privateFeed}[/]");

        if (trusted > 0)
        {
            AnsiConsole.MarkupLine($"[green]  Manually approved (trustedPackages list):  {trusted}[/]");
        }

        if (unknownOwner > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]  Verified but untrusted owner:              {unknownOwner}[/]");
        }

        if (versionChanged > 0)
        {
            AnsiConsole.MarkupLine($"[red]  Version changed - re-review required:      {versionChanged}[/]");
        }

        if (untrusted > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]  Untrusted (no prefix reservation):         {untrusted}[/]");
        }
        Console.WriteLine();
    }

    /// <summary>
    /// Renders the security concerns section: deprecated and vulnerable packages.
    /// </summary>
    /// <param name="result">The audit result.</param>
    private static void RenderSecurityConcerns(AuditResult result)
    {
        var deprecated = result.Packages.Where(p => p.IsDeprecated).ToArray();
        var vulnerable = result.Packages.Where(p => p.HasVulnerabilities).ToArray();

        AnsiConsole.MarkupLine("[white]Security concerns:[/]");

        string deprColor = deprecated.Length > 0 ? "red" : "white";
        AnsiConsole.MarkupLine($"[{deprColor}]  Deprecated packages:           {deprecated.Length}[/]");

        foreach (var pkg in deprecated)
        {
            AnsiConsole.MarkupLine($"[red]    - {Markup.Escape(pkg.PackageId)} {Markup.Escape(pkg.Version)}[/]");
        }

        string vulnColor = vulnerable.Length > 0 ? "red" : "white";
        AnsiConsole.MarkupLine($"[{vulnColor}]  Packages with vulnerabilities: {vulnerable.Length}[/]");

        foreach (var pkg in vulnerable)
        {
            AnsiConsole.MarkupLine($"[red]    - {Markup.Escape(pkg.PackageId)} {Markup.Escape(pkg.Version)}[/]");
        }

        Console.WriteLine();

        // dotnet nuget why for security-concern transitives.
        var securityTransitive = deprecated
            .Concat(vulnerable)
            .Where(p => p.DependencyType == DependencyType.Transitive)
            .GroupBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (securityTransitive.Length > 0)
        {
            AnsiConsole.MarkupLine(
                "[red]Use 'dotnet nuget why' to trace which package brings in these security concerns:[/]");

            foreach (var pkg in securityTransitive)
            {
                AnsiConsole.MarkupLine(
                    $"[cyan]  dotnet nuget why {Markup.Escape(pkg.PackageId)}[/]");
            }

            Console.WriteLine();
        }
    }

    /// <summary>
    /// Renders the recently-published packages section.
    /// Non-trusted packages are always shown individually.
    /// Trusted/verified packages are collapsed to a single count line unless <paramref name="verbose"/> is true.
    /// </summary>
    /// <param name="result">The audit result.</param>
    /// <param name="verbose">When true, all entries are shown without condensing.</param>
    private static void RenderRecentlyPublished(AuditResult result, bool verbose)
    {
        int threshold = result.RecentDaysThreshold;

        var now = DateTimeOffset.UtcNow;
        var recent = result.Packages
            .Where(p => p.Published.HasValue)
            .Select(p => (pkg: p, days: (int)(now - p.Published!.Value).TotalDays))
            .Where(x => x.days <= threshold)
            .OrderBy(x => x.days)
            .ThenBy(x => x.pkg.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        AnsiConsole.MarkupLine(
            $"[yellow]Recently published packages (within {threshold} days -- higher supply chain risk):[/]");

        if (recent.Length == 0)
        {
            AnsiConsole.MarkupLine($"[green]  No packages published within the last {threshold} days.[/]");
            Console.WriteLine();
            return;
        }

        var nonTrusted = recent.Where(x => !Trusted.Contains(x.pkg.TrustStatus)).ToArray();
        var trusted = recent.Where(x => Trusted.Contains(x.pkg.TrustStatus)).ToArray();

        // Non-trusted entries are always listed individually — these are the actionable signal.
        foreach (var (pkg, days) in nonTrusted)
        {
            string dayLabel = days == 0 ? "today" : days == 1 ? "1 day ago" : $"{days} days ago";
            string pubDate = pkg.Published!.Value.ToString("yyyy-MM-dd");
            string trustLabel = TableRenderer.FormatTrustStatus(pkg.TrustStatus);

            AnsiConsole.MarkupLine(
                $"[red]  {Markup.Escape(pkg.PackageId)} {Markup.Escape(pkg.Version)}  published {pubDate} ({dayLabel})  [[{Markup.Escape(trustLabel)}]][/]");
        }

        // Trusted/verified entries: collapse to a count line unless --verbose.
        if (trusted.Length > 0)
        {
            if (verbose)
            {
                foreach (var (pkg, days) in trusted)
                {
                    string dayLabel = days == 0 ? "today" : days == 1 ? "1 day ago" : $"{days} days ago";
                    string pubDate = pkg.Published!.Value.ToString("yyyy-MM-dd");
                    string trustLabel = TableRenderer.FormatTrustStatus(pkg.TrustStatus);

                    AnsiConsole.MarkupLine(
                        $"[yellow]  {Markup.Escape(pkg.PackageId)} {Markup.Escape(pkg.Version)}  published {pubDate} ({dayLabel})  [[{Markup.Escape(trustLabel)}]][/]");
                }
            }
            else
            {
                string also = nonTrusted.Length > 0 ? " also" : "";
                AnsiConsole.MarkupLine(
                    $"[grey]  {trusted.Length} verified/trusted package(s){also} published within {threshold} days — use [bold]--verbose[/] for full list.[/]");
            }
        }

        if (nonTrusted.Length == 0 && trusted.Length > 0 && !verbose)
        {
            // All recent packages are trusted — no actionable signal to highlight.
        }
        else if (nonTrusted.Length == 0)
        {
            AnsiConsole.MarkupLine($"[green]  No non-trusted packages published within the last {threshold} days.[/]");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Renders the executable content alert section for packages needing review that also have exec content.
    /// </summary>
    /// <param name="result">The audit result.</param>
    private static void RenderExecContentAlert(AuditResult result)
    {
        var execReview = result.Packages
            .Where(p => CommandHelpers.NeedsReview.Contains(p.TrustStatus)
                && p.ExecutableContent is not null
                && p.ExecutableContent.Length > 0)
            .OrderBy(p => p.TrustStatus)
            .ThenBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (execReview.Length == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine("[red bold]=== Packages needing review with executable content ===[/]");

        foreach (var pkg in execReview)
        {
            string trustLabel = TableRenderer.FormatTrustStatus(pkg.TrustStatus);
            string exec = string.Join(", ", pkg.ExecutableContent!);
            AnsiConsole.MarkupLine(
                $"[red]  {Markup.Escape(pkg.PackageId)} {Markup.Escape(pkg.Version)}  [[{Markup.Escape(trustLabel)}]]  {Markup.Escape(exec)}[/]");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Renders the "ACTION REQUIRED" banner when any package has VersionChanged status.
    /// </summary>
    /// <param name="result">The audit result.</param>
    private static void RenderActionRequired(AuditResult result)
    {
        int versionChanged = result.Packages.Count(p => p.TrustStatus == TrustStatus.VersionChanged);

        if (versionChanged == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine("[red bold]ACTION REQUIRED: One or more previously trusted packages have a new version.[/]");
        AnsiConsole.MarkupLine("[red]  Review the new version and update your trustedPackages entry if satisfied.[/]");
        AnsiConsole.MarkupLine("[red]  Run --package-list to see formatted entries.[/]");
        Console.WriteLine();
    }

    /// <summary>
    /// Renders dotnet nuget why commands for transitive packages needing review.
    /// Always limited to NeedsReview statuses regardless of the display mode — suppressed
    /// packages and verified/trusted packages are excluded from why-command output.
    /// </summary>
    /// <param name="renderedTransitives">The transitive packages that were actually shown in the table.</param>
    /// <param name="path">The solution/project path to pass to dotnet nuget why.</param>
    private static void RenderTransitiveNuGetWhy(PackageInfo[] renderedTransitives, string path)
    {
        var attentionTransitive = renderedTransitives
            .Where(p => CommandHelpers.NeedsReview.Contains(p.TrustStatus))
            .GroupBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (attentionTransitive.Length == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine(
            "[yellow]For transitive packages needing attention, use 'dotnet nuget why' to trace the chain:[/]");

        foreach (var pkg in attentionTransitive)
        {
            AnsiConsole.MarkupLine(
                $"[cyan]  dotnet nuget why \"{Markup.Escape(path)}\" {Markup.Escape(pkg.PackageId)}[/]");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Renders Package Source Mapping and lock file advisories.
    /// </summary>
    /// <param name="result">The audit result.</param>
    private static void RenderAdvisories(AuditResult result)
    {
        if (result.PsmStatus == PackageSourceMappingStatus.NotConfigured)
        {
            AnsiConsole.MarkupLine("[red bold]SECURITY ADVISORY: Package Source Mapping is not configured.[/]");
            AnsiConsole.MarkupLine(
                "[yellow]  Without it, NuGet may resolve packages from unintended feeds, enabling[/]");
            AnsiConsole.MarkupLine(
                "[yellow]  dependency confusion attacks. Run [bold]nuget-audit explain psm[/] for details.[/]");
            Console.WriteLine();
        }

        switch (result.LockFileStatus)
        {
            case LockFileStatus.NoLockFile:
                AnsiConsole.MarkupLine("[red bold]SECURITY ADVISORY: No packages.lock.json found.[/]");
                AnsiConsole.MarkupLine(
                    "[yellow]  Without a lock file, dotnet restore can silently pull in changed or[/]");
                AnsiConsole.MarkupLine(
                    "[yellow]  compromised packages. Run [bold]nuget-audit explain lock-files[/] for setup steps.[/]");
                Console.WriteLine();
                break;

            case LockFileStatus.LockFileNoEnforcement:
                AnsiConsole.MarkupLine(
                    "[red bold]SECURITY ADVISORY: packages.lock.json found but RestoreLockedMode is not set.[/]");
                AnsiConsole.MarkupLine(
                    "[yellow]  Add RestoreLockedMode=true to Directory.Build.props to enforce the lock[/]");
                AnsiConsole.MarkupLine(
                    "[yellow]  on all machines. Run [bold]nuget-audit explain lock-files[/] for details.[/]");
                Console.WriteLine();
                break;

            case LockFileStatus.LockedEnforcedNoBuildTarget:
                AnsiConsole.MarkupLine(
                    "[red bold]SECURITY ADVISORY: RestoreLockedMode=true is set but no Directory.Build.targets with a nuget-audit invocation was found.[/]");
                AnsiConsole.MarkupLine(
                    "[yellow]  It enforces --check before each Visual Studio build. Run [bold]nuget-audit init[/] to create it.[/]");
                Console.WriteLine();
                break;

            case LockFileStatus.LockedAndEnforced:
            default:
                break;
        }
    }


    #endregion
}
