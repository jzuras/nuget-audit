using Spectre.Console;
using System.CommandLine;

namespace NugetAudit.Cli.Commands;

/// <summary>
/// Builds the <c>explain</c> subcommand: prints in-depth explanations of security concepts
/// used by this tool, with context for the zero-trust workflow.
/// </summary>
internal static class ExplainCommand
{
    #region Command Definition

    /// <summary>
    /// Creates and returns the configured <c>explain</c> <see cref="Command"/>.
    /// </summary>
    internal static Command Create()
    {
        var topicArgument = new Argument<string?>("topic")
        {
            Description = "Topic to explain. Omit to list available topics.",
            Arity = ArgumentArity.ZeroOrOne
        };

        var command = new Command("explain", "Show an in-depth explanation of a security concept used by this tool.")
        {
            topicArgument
        };

        command.SetAction(parseResult =>
        {
            string? topic = parseResult.GetValue(topicArgument);
            Environment.ExitCode = RunExplain(topic);
        });

        return command;
    }

    #endregion

    #region Core Logic

    /// <summary>
    /// Dispatches to the appropriate topic handler, or shows the topic list if no topic is given.
    /// </summary>
    /// <param name="topic">The topic name, or null/empty for the topic list.</param>
    /// <returns>Process exit code.</returns>
    internal static int RunExplain(string? topic)
    {
        return topic?.ToLowerInvariant() switch
        {
            "lock-files"   => ExplainLockFiles(),
            "psm"          => ExplainPsm(),
            "exec-content" => ExplainExecContent(),
            null or ""     => ShowTopicList(),
            _              => ShowUnknownTopic(topic)
        };
    }

    #endregion

    #region Topic List

    /// <summary>
    /// Prints the list of available explain topics.
    /// </summary>
    private static int ShowTopicList()
    {
        Console.WriteLine();
        AnsiConsole.MarkupLine("[cyan bold]nuget-audit explain  -- available topics[/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine("[white]  lock-files[/]    [grey]Lock file enforcement and RestoreLockedMode[/]");
        AnsiConsole.MarkupLine("[white]  psm[/]           [grey]Package Source Mapping and dependency confusion[/]");
        AnsiConsole.MarkupLine("[white]  exec-content[/]  [grey]Executable content in NuGet packages[/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine("[grey]Usage: [bold]nuget-audit explain <topic>[/][/]");
        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// Reports an unrecognized topic and shows the topic list.
    /// </summary>
    /// <param name="topic">The unrecognized topic name.</param>
    private static int ShowUnknownTopic(string topic)
    {
        AnsiConsole.MarkupLine($"[red]Unknown topic: {Markup.Escape(topic)}[/]");
        Console.WriteLine();
        ShowTopicList();
        return 1;
    }

    #endregion

    #region Topics

    /// <summary>
    /// Explains lock file enforcement and RestoreLockedMode in the context of the zero-trust workflow.
    /// </summary>
    private static int ExplainLockFiles()
    {
        Console.WriteLine();
        AnsiConsole.MarkupLine("[cyan bold]Lock File Enforcement[/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine("[white]Role in the zero-trust workflow:[/]");
        AnsiConsole.MarkupLine("[grey]  The zero-trust workflow audits packages before they are used. But if dotnet[/]");
        AnsiConsole.MarkupLine("[grey]  restore can pull in different versions between runs, an audit is only a[/]");
        AnsiConsole.MarkupLine("[grey]  snapshot  -- not a guarantee. Lock file enforcement makes restores[/]");
        AnsiConsole.MarkupLine("[grey]  deterministic, so the packages you audited are the packages you build with.[/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine("[white]What packages.lock.json does:[/]");
        AnsiConsole.MarkupLine("[grey]  Pins the exact resolved version of every package in the graph. Once created[/]");
        AnsiConsole.MarkupLine("[grey]  and committed, dotnet restore reproduces the same graph on every machine.[/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine("[white]What RestorePackagesWithLockFile=true does:[/]");
        AnsiConsole.MarkupLine("[grey]  Tells NuGet to generate packages.lock.json and use it on every restore.[/]");
        AnsiConsole.MarkupLine("[grey]  Without this, no lock file is created  -- even if RestoreLockedMode is set.[/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine("[white]What RestoreLockedMode=true does:[/]");
        AnsiConsole.MarkupLine("[grey]  Tells dotnet restore to fail if the lock file is out of date rather than[/]");
        AnsiConsole.MarkupLine("[grey]  silently updating it. Any graph change becomes deliberate and visible.[/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine("[white]How to enable:[/]");
        AnsiConsole.MarkupLine("[grey]  1. Add both properties to Directory.Build.props:[/]");
        AnsiConsole.MarkupLine("[grey]       <PropertyGroup>[/]");
        AnsiConsole.MarkupLine("[grey]         <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>[/]");
        AnsiConsole.MarkupLine("[grey]         <RestoreLockedMode>true</RestoreLockedMode>[/]");
        AnsiConsole.MarkupLine("[grey]       </PropertyGroup>[/]");
        AnsiConsole.MarkupLine("[grey]  2. Run: [bold white]dotnet restore --force-evaluate[/][/]");
        AnsiConsole.MarkupLine("[grey]     Generates packages.lock.json for every project in the solution.[/]");
        AnsiConsole.MarkupLine("[grey]  3. Commit both files:[/]");
        AnsiConsole.MarkupLine("[grey]       git add packages.lock.json Directory.Build.props[/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine("[grey]Full zero-trust workflow: [white]https://jzuras.github.io/nuget-audit[/][/]");
        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// Explains Package Source Mapping and dependency confusion in the context of the zero-trust workflow.
    /// </summary>
    private static int ExplainPsm()
    {
        Console.WriteLine();
        AnsiConsole.MarkupLine("[cyan bold]Package Source Mapping[/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine("[white]Role in the zero-trust workflow:[/]");
        AnsiConsole.MarkupLine("[grey]  nuget-audit audits packages against nuget.org. But if NuGet can resolve a[/]");
        AnsiConsole.MarkupLine("[grey]  package from any configured feed, an attacker can publish a malicious package[/]");
        AnsiConsole.MarkupLine("[grey]  with the same name as one of your private packages  -- and NuGet may pick it[/]");
        AnsiConsole.MarkupLine("[grey]  up from nuget.org instead of your private feed. This is a dependency[/]");
        AnsiConsole.MarkupLine("[grey]  confusion attack. Package Source Mapping prevents it by binding each package[/]");
        AnsiConsole.MarkupLine("[grey]  ID to the feed it is allowed to come from.[/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine("[white]What Package Source Mapping does:[/]");
        AnsiConsole.MarkupLine("[grey]  Configured in NuGet.Config, it declares which feeds are allowed to provide[/]");
        AnsiConsole.MarkupLine("[grey]  each package ID or prefix. NuGet will not resolve a package from a feed[/]");
        AnsiConsole.MarkupLine("[grey]  that is not mapped for that package.[/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine("[white]When it applies:[/]");
        AnsiConsole.MarkupLine("[grey]  Only relevant if you have more than one package source configured. If all[/]");
        AnsiConsole.MarkupLine("[grey]  packages come from nuget.org only, PSM is not needed.[/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine("[white]How to configure:[/]");
        AnsiConsole.MarkupLine("[grey]  Add a <packageSourceMapping> section to NuGet.Config.[/]");
        AnsiConsole.MarkupLine("[grey]  https://learn.microsoft.com/en-us/nuget/consume-packages/package-source-mapping[/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine("[grey]Full zero-trust workflow: [white]https://jzuras.github.io/nuget-audit[/][/]");
        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// Explains executable content in NuGet packages in the context of the zero-trust workflow.
    /// </summary>
    private static int ExplainExecContent()
    {
        Console.WriteLine();
        AnsiConsole.MarkupLine("[cyan bold]Executable Content in NuGet Packages[/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine("[white]Role in the zero-trust workflow:[/]");
        AnsiConsole.MarkupLine("[grey]  The zero-trust workflow is about knowing what you are running. Most NuGet[/]");
        AnsiConsole.MarkupLine("[grey]  packages are passive libraries  -- they run only when your code calls them.[/]");
        AnsiConsole.MarkupLine("[grey]  Packages with executable content are different: they run automatically[/]");
        AnsiConsole.MarkupLine("[grey]  during your build, independent of your code. This expands the trust surface[/]");
        AnsiConsole.MarkupLine("[grey]  significantly and warrants extra scrutiny.[/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine("[white]Types of executable content:[/]");
        AnsiConsole.MarkupLine("[grey]  MSBld  MSBuild .targets/.props files  -- run during build with full access[/]");
        AnsiConsole.MarkupLine("[grey]         to your source tree, environment variables, and network.[/]");
        AnsiConsole.MarkupLine("[grey]  Alyzr  Roslyn analyzers  -- run inside the compiler during every build.[/]");
        AnsiConsole.MarkupLine("[grey]  Tools  .NET CLI tools bundled with the package.[/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine("[white]What to do when you see this flag:[/]");
        AnsiConsole.MarkupLine("[grey]  Executable content is not inherently malicious  -- most build tooling packages[/]");
        AnsiConsole.MarkupLine("[grey]  legitimately include it. But a compromised package with exec content has[/]");
        AnsiConsole.MarkupLine("[grey]  code execution during your build. Treat these packages with extra care:[/]");
        AnsiConsole.MarkupLine("[grey]  verify the trust status, confirm the publisher, and consider whether the[/]");
        AnsiConsole.MarkupLine("[grey]  package is truly necessary.[/]");
        Console.WriteLine();
        AnsiConsole.MarkupLine("[grey]Full zero-trust workflow: [white]https://jzuras.github.io/nuget-audit[/][/]");
        Console.WriteLine();
        return 0;
    }

    #endregion
}
