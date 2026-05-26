using System.CommandLine;
using Spectre.Console;

namespace NugetAudit.Cli.Commands;

/// <summary>
/// Builds the <c>guide</c> subcommand: prints a concise workflow walkthrough for new and ongoing use.
/// </summary>
internal static class GuideCommand
{
    /// <summary>
    /// Creates and returns the configured <c>guide</c> <see cref="Command"/>.
    /// </summary>
    internal static Command Create()
    {
        var command = new Command("guide", "Show a workflow walkthrough for new and ongoing use.");

        command.SetAction(_ => RunGuide());

        return command;
    }

    #region Core Logic

    /// <summary>
    /// Prints the workflow guide to the console.
    /// </summary>
    private static void RunGuide()
    {
        Console.WriteLine();
        AnsiConsole.MarkupLine("[cyan bold]=== New project setup ===[/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine("[white]1. [bold]nuget-audit init --path <dir>[/][/]");
        AnsiConsole.MarkupLine("[grey]     Creates TrustConfig.json and Directory.Build.targets for the zero-trust workflow,[/]");
        AnsiConsole.MarkupLine("[grey]     then prints next steps.[/]");
        AnsiConsole.MarkupLine("[grey]     Follow those steps  -- they cover trust config, audit, and lock file setup.[/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine("[cyan bold]=== Ongoing use ===[/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine("[white]1. [bold]nuget-audit audit --path <dir>[/][/]");
        AnsiConsole.MarkupLine("[white]2. If packages need review, run [bold]--package-list[/], update TrustConfig.json, re-run.[/]");
        AnsiConsole.MarkupLine("[white]3. Before updating a package:[/]");
        AnsiConsole.MarkupLine("[white]     [bold]nuget-audit preview-update <id> --version <ver> --path <dir>[/][/]");
        AnsiConsole.MarkupLine("[white]4. Before restoring a fresh clone[/]");
        AnsiConsole.MarkupLine("[grey]     (clone with git CLI, not VS  -- VS restores automatically):[/]");
        AnsiConsole.MarkupLine("[white]     [bold]nuget-audit preview-restore --path <dir>[/][/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine("[grey]Run [bold]nuget-audit <command> --help[/] for all options.[/]");
        AnsiConsole.MarkupLine("[grey]Full documentation: https://jzuras.github.io/nuget-audit/guide/[/]");
        Console.WriteLine();
    }

    #endregion
}
