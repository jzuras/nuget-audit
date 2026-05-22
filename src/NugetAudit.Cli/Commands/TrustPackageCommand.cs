using NugetAudit.Core.Configuration;
using NugetAudit.Core.Models;
using Spectre.Console;
using System.CommandLine;
using System.Text.Json;

namespace NugetAudit.Cli.Commands;

/// <summary>
/// Builds the <c>trust-package</c> subcommand: adds or updates a package/version entry
/// in the trusted packages list in TrustConfig.json.
/// </summary>
internal static class TrustPackageCommand
{
    /// <summary>
    /// Creates and returns the configured <c>trust-package</c> <see cref="Command"/>.
    /// </summary>
    /// <returns>A configured System.CommandLine <see cref="Command"/> for the trust-package subcommand.</returns>
    internal static Command Create()
    {
        var idArg = new Argument<string>("id")
        {
            Description = "The NuGet package identifier.",
        };

        var versionArg = new Argument<string>("version")
        {
            Description = "The package version to trust.",
        };

        var pathOption = new Option<string>("--path")
        {
            Description = "Path to the solution, project, or directory containing TrustConfig.json.",
            DefaultValueFactory = _ => ".",
        };

        var trustConfigOption = new Option<string?>("--trust-config")
        {
            Description = "Path to TrustConfig.json, or a directory containing it. Defaults to the solution directory.",
        };

        var command = new Command("trust-package", "Add or update a package/version entry in the trusted packages list in TrustConfig.json.")
        {
            idArg,
            versionArg,
            pathOption,
            trustConfigOption,
        };

        command.SetAction(parseResult =>
        {
            string id = parseResult.GetValue(idArg) ?? string.Empty;
            string version = parseResult.GetValue(versionArg) ?? string.Empty;
            string path = parseResult.GetValue(pathOption) ?? ".";
            string? trustConfig = parseResult.GetValue(trustConfigOption);

            if (CommandHelpers.HasQuotedTrailingBackslash(path) || CommandHelpers.HasQuotedTrailingBackslash(trustConfig))
            {
                CommandHelpers.RenderTrailingBackslashError();
                Environment.ExitCode = 1;
                return;
            }

            Environment.ExitCode = RunTrustPackage(id, version, path, trustConfig);
        });

        return command;
    }

    #region Core Logic

    /// <summary>
    /// Loads TrustConfig.json, adds or updates the package entry, and saves.
    /// If an entry for the same package ID already exists with a different version, the version is updated.
    /// Returns the process exit code.
    /// </summary>
    /// <param name="id">The NuGet package identifier.</param>
    /// <param name="version">The version to trust.</param>
    /// <param name="path">The --path option value.</param>
    /// <param name="trustConfigPath">The --trust-config option value, or null.</param>
    /// <returns>0 on success, 1 on error.</returns>
    internal static int RunTrustPackage(string id, string version, string path, string? trustConfigPath)
    {
        string configPath = CommandHelpers.ResolveTrustConfigPath(path, trustConfigPath);

        TrustConfig config;

        try
        {
            config = new TrustConfigLoader().Load(configPath);
        }
        catch (FileNotFoundException)
        {
            AnsiConsole.MarkupLine($"[red]Error: TrustConfig.json not found at:[/] [white]{Markup.Escape(configPath)}[/]");
            AnsiConsole.MarkupLine("[yellow]  Run [bold]nuget-audit init[/] to create one.[/]");
            return 1;
        }
        catch (JsonException)
        {
            AnsiConsole.MarkupLine($"[red]Error: '{Markup.Escape(configPath)}' is not a valid JSON file.[/]");
            return 1;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        var existing = config.TrustedPackages
            .FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

        if (existing is not null && string.Equals(existing.Version, version, StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]'{Markup.Escape(id)} {Markup.Escape(version)}' is already in trustedPackages — no change made.[/]");
            return 0;
        }

        TrustedPackageEntry[] packages;
        bool replaced = existing is not null;

        if (replaced)
        {
            // Update the version for an existing entry (handles VersionChanged re-trust).
            packages = config.TrustedPackages
                .Select(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)
                    ? p with { Version = version }
                    : p)
                .ToArray();
        }
        else
        {
            packages = [.. config.TrustedPackages, new TrustedPackageEntry(id, version)];
        }

        var updated = config with { TrustedPackages = packages };

        try
        {
            new TrustConfigSaver().Save(updated, configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]Error saving TrustConfig.json: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        if (replaced)
        {
            AnsiConsole.MarkupLine(
                $"[green]Updated '[bold]{Markup.Escape(existing!.Id)}[/]' {Markup.Escape(existing.Version)} → {Markup.Escape(version)} in:[/] [white]{Markup.Escape(configPath)}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[green]Added '[bold]{Markup.Escape(id)} {Markup.Escape(version)}[/]' to trustedPackages in:[/] [white]{Markup.Escape(configPath)}[/]");
        }

        return 0;
    }

    #endregion

    #region Private Helpers


    #endregion
}
