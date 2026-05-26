using NugetAudit.Core.Configuration;
using NugetAudit.Core.Models;
using Spectre.Console;
using System.CommandLine;
using System.Text.Json;

namespace NugetAudit.Cli.Commands;

/// <summary>
/// Builds the <c>trust-owner</c> subcommand: adds a nuget.org account name to the
/// trusted owners list in TrustConfig.json.
/// </summary>
internal static class TrustOwnerCommand
{
    /// <summary>
    /// Creates and returns the configured <c>trust-owner</c> <see cref="Command"/>.
    /// </summary>
    /// <returns>A configured System.CommandLine <see cref="Command"/> for the trust-owner subcommand.</returns>
    internal static Command Create()
    {
        var ownerArg = new Argument<string>("owner")
        {
            Description = "The nuget.org account name to add to trustedOwners (e.g. 'microsoft').",
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

        var command = new Command("trust-owner", "Add a nuget.org account name to the trusted owners list in TrustConfig.json.")
        {
            ownerArg,
            pathOption,
            trustConfigOption,
        };

        command.SetAction(parseResult =>
        {
            string owner = parseResult.GetValue(ownerArg) ?? string.Empty;
            string path = parseResult.GetValue(pathOption) ?? ".";
            string? trustConfig = parseResult.GetValue(trustConfigOption);

            if (CommandHelpers.HasQuotedTrailingBackslash(path) || CommandHelpers.HasQuotedTrailingBackslash(trustConfig))
            {
                CommandHelpers.RenderTrailingBackslashError();
                Environment.ExitCode = 1;
                return;
            }

            Environment.ExitCode = RunTrustOwner(owner, path, trustConfig);
        });

        return command;
    }

    #region Core Logic

    /// <summary>
    /// Loads TrustConfig.json, adds the owner if not already present, and saves.
    /// Returns the process exit code.
    /// </summary>
    /// <param name="owner">The nuget.org account name to add.</param>
    /// <param name="path">The --path option value.</param>
    /// <param name="trustConfigPath">The --trust-config option value, or null.</param>
    /// <returns>0 on success, 1 on error.</returns>
    internal static int RunTrustOwner(string owner, string path, string? trustConfigPath)
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

        if (config.TrustedOwners.Any(o => string.Equals(o, owner, StringComparison.OrdinalIgnoreCase)))
        {
            AnsiConsole.MarkupLine($"[yellow]'{Markup.Escape(owner)}' is already in trustedOwners  -- no change made.[/]");
            return 0;
        }

        var updated = config with { TrustedOwners = [.. config.TrustedOwners, owner] };

        try
        {
            new TrustConfigSaver().Save(updated, configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]Error saving TrustConfig.json: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"[green]Added '[bold]{Markup.Escape(owner)}[/]' to trustedOwners in:[/] [white]{Markup.Escape(configPath)}[/]");

        return 0;
    }

    #endregion
}
