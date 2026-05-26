using NugetAudit.Core.Models;
using Spectre.Console;

namespace NugetAudit.Cli.Commands;

/// <summary>
/// Shared utility methods and constants used across CLI command classes.
/// </summary>
internal static class CommandHelpers
{
    /// <summary>
    /// Gets the set of trust statuses that require user review.
    /// </summary>
    internal static HashSet<TrustStatus> NeedsReview { get; } =
    [
        TrustStatus.Untrusted,
        TrustStatus.VersionChanged,
        TrustStatus.VerifiedUnknownOwner,
    ];


    /// <summary>
    /// Resolves the full path to TrustConfig.json.
    /// Prefers an explicit --trust-config value; falls back to deriving from --path.
    /// </summary>
    /// <param name="path">The --path option value.</param>
    /// <param name="trustConfigPath">The --trust-config option value, or null.</param>
    /// <returns>The absolute path to TrustConfig.json.</returns>
    internal static string ResolveTrustConfigPath(string path, string? trustConfigPath)
    {
        if (trustConfigPath is not null)
        {
            string fullTrust = Path.GetFullPath(trustConfigPath.TrimEnd());

            if (Directory.Exists(fullTrust))
            {
                return Path.Combine(fullTrust, "TrustConfig.json");
            }

            return fullTrust;
        }

        string fullPath = Path.GetFullPath(path.TrimEnd());

        if (File.Exists(fullPath))
        {
            string dir = Path.GetDirectoryName(fullPath) ?? fullPath;
            return Path.Combine(dir, "TrustConfig.json");
        }

        return Path.Combine(fullPath, "TrustConfig.json");
    }

    /// <summary>
    /// Returns true when a path value contains a quote character, which indicates the
    /// Windows trailing-backslash quoting problem ("C:\path\" escapes the closing quote).
    /// </summary>
    /// <param name="path">The path string to inspect; null returns false.</param>
    /// <returns><see langword="true"/> when <paramref name="path"/> contains a double-quote character.</returns>
    internal static bool HasQuotedTrailingBackslash(string? path) =>
        path is not null && path.Contains('"', StringComparison.Ordinal);

    /// <summary>
    /// Renders the error message for the Windows trailing-backslash quoting problem.
    /// </summary>
    internal static void RenderTrailingBackslashError()
    {
        AnsiConsole.MarkupLine("[red]Error: A path argument contains an unexpected quote character.[/]");
        AnsiConsole.MarkupLine(
            "[yellow]  On Windows, a trailing backslash in a quoted path (\"C:\\path\\\") escapes the closing[/]");
        AnsiConsole.MarkupLine(
            "[yellow]  quote and corrupts subsequent arguments. Use \"C:\\path\" or \"C:\\path\\\\\" instead.[/]");
    }
}
