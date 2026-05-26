using System.CommandLine;
using NugetAudit.Cli.Commands;
#if INCLUDE_UI
using NugetAudit.Cli;
#endif

namespace NugetAudit.Cli;

/// <summary>
/// Entry point for the nuget-audit dotnet global tool.
/// Branches between headless CLI mode and Blazor Server GUI mode based on the invocation style.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Main entry point. Dispatches to the Blazor GUI when invoked with --ui,
    /// otherwise builds the System.CommandLine pipeline and invokes the appropriate subcommand.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

#if INCLUDE_UI
        if (IsGuiInvocation(args))
        {
            return await WebHostRunner.RunAsync(args);
        }
#endif

        var rootCommand = new RootCommand("NuGet package security audit tool for .NET solutions.");

#if INCLUDE_UI
        var uiCommand = new Command("ui", "Open the interactive browser UI (Blazor Server at http://localhost:5150).");
        uiCommand.Options.Add(new Option<string>("--path") { Description = "Path to the solution, project, or directory to pre-fill in the UI." });
        uiCommand.Options.Add(new Option<int>("--port") { Description = "Port to listen on (default: 5150)." });
        rootCommand.Subcommands.Add(uiCommand);
#endif

        rootCommand.Subcommands.Add(AuditCommand.Create());
        rootCommand.Subcommands.Add(PreviewUpdateCommand.Create());
        rootCommand.Subcommands.Add(PreviewRestoreCommand.Create());
        rootCommand.Subcommands.Add(InitCommand.Create());
        rootCommand.Subcommands.Add(TrustOwnerCommand.Create());
        rootCommand.Subcommands.Add(TrustPackageCommand.Create());
        rootCommand.Subcommands.Add(GuideCommand.Create());
        rootCommand.Subcommands.Add(ExplainCommand.Create());

        var parseResult = rootCommand.Parse(args);

        // When no subcommand is recognised (errors at root level), suppress the parse error
        // messages and show only the help output — the errors are noise for the user.
        if (parseResult.Errors.Count > 0 && parseResult.CommandResult.Command == rootCommand)
        {
            await rootCommand.Parse(["--help"]).InvokeAsync();
            return 0;
        }

        await parseResult.InvokeAsync();
        return Environment.ExitCode;
    }

#if INCLUDE_UI
    /// <summary>
    /// Determines whether this invocation should launch the Blazor GUI rather than run headless.
    /// GUI mode is used only when --ui or ui is explicitly specified.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>True if the GUI should be launched; false for headless CLI mode.</returns>
    private static bool IsGuiInvocation(string[] args)
    {
        return args.Any(a => a is "--ui" or "ui");
    }
#endif
}
