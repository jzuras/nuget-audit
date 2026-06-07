using NugetAudit.Core.Configuration;
using NugetAudit.Core.Models;
using Spectre.Console;
using System.CommandLine;

namespace NugetAudit.Cli.Commands;

/// <summary>
/// Builds the <c>init</c> subcommand: creates TrustConfig.json and Directory.Build.targets
/// to set up a project for the zero-trust NuGet audit workflow.
/// </summary>
internal static class InitCommand
{
    #region Constants

    /// <summary>
    /// Gets the default trusted owner account names written by <c>nuget-audit init</c>.
    /// These are the nuget.org account names (not display names) of well-known publishers.
    /// </summary>
    private static string[] DefaultTrustedOwners { get; } =
    [
        "Microsoft",
        "dotnetfoundation",
        "aspnet"
    ];

    /// <summary>
    /// Gets the MSBuild target content written to Directory.Build.targets by <c>nuget-audit init</c>.
    /// The target runs <c>nuget-audit audit --check</c> before each build when the lock file has
    /// changed since the last clean audit, using a sentinel file to avoid redundant checks.
    /// </summary>
    private const string TargetsFileContent = """
        <Project>
          <Target Name="AuditIfRestoreChanged" BeforeTargets="Build">
            <PropertyGroup>
              <_SentinelFile>$(MSBuildThisFileDirectory).nuget-audit-ok</_SentinelFile>
              <_LockFile>$([System.IO.Path]::Combine('$(MSBuildProjectDirectory)', 'packages.lock.json'))</_LockFile>
              <_AuditPath>$([System.IO.Path]::GetFullPath('$(MSBuildThisFileDirectory).'))</_AuditPath>
            </PropertyGroup>
            <Exec
              Command="nuget-audit audit --check --path &quot;$(_AuditPath)&quot;"
              Condition="!Exists('$(_SentinelFile)') Or
                         $([System.IO.File]::GetLastWriteTime('$(_LockFile)').Ticks) &gt;
                          $([System.IO.File]::GetLastWriteTime('$(_SentinelFile)').Ticks)" />
            <Touch Files="$(_SentinelFile)" AlwaysCreate="true"
                   Condition="'$(MSBuildLastTaskResult)' != 'false'" />
          </Target>
        </Project>
        """;

    #endregion

    /// <summary>
    /// Creates and returns the configured <c>init</c> <see cref="Command"/>.
    /// </summary>
    internal static Command Create()
    {
        var pathOption = new Option<string>("--path")
        {
            Description = "Directory in which to create the files.",
            DefaultValueFactory = _ => "."
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Overwrite existing files."
        };

        var command = new Command("init", "Create TrustConfig.json and Directory.Build.targets for the zero-trust workflow.")
        {
            pathOption,
            forceOption
        };

        command.SetAction(parseResult =>
        {
            string path = parseResult.GetValue(pathOption) ?? ".";
            bool force = parseResult.GetValue(forceOption);

            Environment.ExitCode = RunInit(path, force);
        });

        return command;
    }

    #region Core Logic

    /// <summary>
    /// Resolves the output directory, then creates TrustConfig.json and Directory.Build.targets.
    /// Existing files are skipped unless <paramref name="force"/> is true.
    /// Returns the process exit code.
    /// </summary>
    /// <param name="path">The --path option value (directory or explicit file path).</param>
    /// <param name="force">When true, overwrites existing files.</param>
    /// <returns>0 on success, 1 on error.</returns>
    private static int RunInit(string path, bool force)
    {
        string outputDir = ResolveOutputDirectory(path);
        string configPath = Path.Combine(outputDir, "TrustConfig.json");
        string targetsPath = Path.Combine(outputDir, "Directory.Build.targets");

        bool wroteConfig = false;
        bool wroteTargets = false;

        // TrustConfig.json
        if (File.Exists(configPath) && !force)
        {
            AnsiConsole.MarkupLine("[grey]TrustConfig.json already exists  -- skipped (use --force to overwrite).[/]");
        }
        else
        {
            try
            {
                WriteInitConfig(configPath);
                wroteConfig = true;
                AnsiConsole.MarkupLine($"[green]Created TrustConfig.json at:[/] [white]{Markup.Escape(configPath)}[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error writing TrustConfig.json: {Markup.Escape(ex.Message)}[/]");
                return 1;
            }
        }

        // Directory.Build.targets
        if (File.Exists(targetsPath) && !force)
        {
            AnsiConsole.MarkupLine("[grey]Directory.Build.targets already exists  -- skipped (use --force to overwrite).[/]");
        }
        else
        {
            try
            {
                WriteTargetsFile(targetsPath);
                wroteTargets = true;
                AnsiConsole.MarkupLine($"[green]Created Directory.Build.targets at:[/] [white]{Markup.Escape(targetsPath)}[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error writing Directory.Build.targets: {Markup.Escape(ex.Message)}[/]");
                return 1;
            }
        }

        if (!wroteConfig && !wroteTargets)
        {
            return 0;
        }

        Console.WriteLine();

        if (wroteConfig)
        {
            AnsiConsole.MarkupLine("[white]Default trusted owners:[/]");

            foreach (string owner in DefaultTrustedOwners)
            {
                AnsiConsole.MarkupLine($"[grey]  {Markup.Escape(owner)}[/]");
            }

            Console.WriteLine();
            AnsiConsole.MarkupLine("[white]Example trusted packages entry (replace with your own):[/]");
            AnsiConsole.MarkupLine("[grey]  { \"id\": \"EXAMPLE.PACKAGE\", \"version\": \"1.0.0\" }[/]");
            Console.WriteLine();
        }

        string escapedDir = Markup.Escape(outputDir);

        AnsiConsole.MarkupLine("[cyan]Next steps:[/]");

        int step = 1;

        if (wroteConfig)
        {
            AnsiConsole.MarkupLine($"[white]  {step++}. Edit TrustConfig.json  -- add your organization's trusted nuget.org account names,[/]");
            AnsiConsole.MarkupLine("[grey]       then populate trusted packages:[/]");
            AnsiConsole.MarkupLine($"[grey]       Run: [bold]nuget-audit audit --package-list --path \"{escapedDir}\"[/][/]");
            AnsiConsole.MarkupLine("[grey]       This outputs ready-to-paste entries for each package needing review.[/]");
            AnsiConsole.MarkupLine("[grey]       Or use trust-owner / trust-package to update the file directly[/]");
            AnsiConsole.MarkupLine("[grey]       (run [bold]nuget-audit trust-owner --help[/] or [bold]trust-package --help[/] for details).[/]");
            AnsiConsole.MarkupLine("[grey]       The EXAMPLE.PACKAGE placeholder is removed automatically when you add your first real entry.[/]");
            AnsiConsole.MarkupLine($"[white]  {step++}. Run: [bold]nuget-audit audit --path \"{escapedDir}\"[/][/]");
        }

        AnsiConsole.MarkupLine($"[white]  {step++}. Add [bold].nuget-audit-ok[/] to .gitignore[/]");
        AnsiConsole.MarkupLine($"[grey]       This is a machine-local sentinel file created by Directory.Build.targets.[/]");
        AnsiConsole.MarkupLine($"[white]  {step}. Enable lock file enforcement:[/]");
        AnsiConsole.MarkupLine($"[grey]       Run [bold]nuget-audit explain lock-files[/] for setup steps.[/]");

        Console.WriteLine();

        return 0;
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Serializes and writes the initial TrustConfig.json, including the example entry.
    /// </summary>
    /// <param name="outputPath">The full path to write TrustConfig.json.</param>
    private static void WriteInitConfig(string outputPath)
    {
        var config = new TrustConfig(
            DefaultTrustedOwners,
            [new TrustedPackageEntry("EXAMPLE.PACKAGE", "1.0.0")],
            TrustConfigLoader.DefaultRecentDaysThreshold);

        new TrustConfigSaver().Save(config, outputPath);

        File.AppendAllText(outputPath, Environment.NewLine);
    }

    /// <summary>
    /// Writes the Directory.Build.targets file with the pre-build audit target.
    /// </summary>
    /// <param name="targetsPath">The full path to write Directory.Build.targets.</param>
    private static void WriteTargetsFile(string targetsPath)
    {
        string? directory = Path.GetDirectoryName(targetsPath);

        if (directory is not null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(targetsPath, TargetsFileContent + Environment.NewLine);
    }

    /// <summary>
    /// Resolves the output directory from the --path option value.
    /// If <paramref name="path"/> points to an existing file or a .json path, its containing
    /// directory is used. Otherwise the path is treated as a directory.
    /// </summary>
    /// <param name="path">The --path option value.</param>
    /// <returns>The absolute directory path in which to create the files.</returns>
    private static string ResolveOutputDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);

        // If an existing file is given (e.g. a .sln or .slnx), use its containing directory.
        if (File.Exists(fullPath))
        {
            return Path.GetDirectoryName(fullPath) ?? fullPath;
        }

        // If it looks like a .json path, use its containing directory.
        if (string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(fullPath) ?? fullPath;
        }

        // Otherwise treat it as a directory.
        return fullPath;
    }

    #endregion
}
