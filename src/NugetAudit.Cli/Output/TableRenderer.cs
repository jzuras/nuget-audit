using System.Text;
using NugetAudit.Core.Models;
using Spectre.Console;

namespace NugetAudit.Cli.Output;

/// <summary>
/// Renders a NuGet audit result as a fixed-width plain-text table.
/// Column widths are computed from the data. The PackageId column is capped so that
/// the total row fits within the terminal width; IDs that exceed the cap wrap onto
/// a continuation line (matching PS Format-Table behavior).
/// Columns: Type, PackageId, Version, Owners, Verified, Trust, Depr, Vuln, Exec (conditional).
/// </summary>
internal static class TableRenderer
{
    #region Column Index Constants

    /// <summary>Column index for the dependency type (Direct/Transitive).</summary>
    private const int ColType     = 0;

    /// <summary>Column index for the package identifier.</summary>
    private const int ColId       = 1;

    /// <summary>Column index for the resolved version.</summary>
    private const int ColVersion  = 2;

    /// <summary>Column index for the package owners.</summary>
    private const int ColOwners   = 3;

    /// <summary>Column index for the NuGet verified publisher flag.</summary>
    private const int ColVerified = 4;

    /// <summary>Column index for the trust status label.</summary>
    private const int ColTrust    = 5;

    /// <summary>Column index for the deprecation flag.</summary>
    private const int ColDepr     = 6;

    /// <summary>Column index for the vulnerability flag.</summary>
    private const int ColVuln     = 7;

    /// <summary>Column index for the executable content indicator (build/analyzers/tools).</summary>
    private const int ColExec     = 8;

    #endregion

    #region Trust Color Mapping

    /// <summary>
    /// Maps a <see cref="TrustStatus"/> value to a Spectre.Console color name.
    /// </summary>
    /// <param name="status">The trust status to map.</param>
    /// <returns>A Spectre.Console color name string.</returns>
    internal static string GetTrustColor(TrustStatus status)
    {
        return status switch
        {
            TrustStatus.Verified             => "green",
            TrustStatus.TrustedPackage       => "green",
            TrustStatus.PrivateFeed          => "cyan",
            TrustStatus.VerifiedUnknownOwner => "yellow",
            TrustStatus.Untrusted            => "yellow",
            TrustStatus.VersionChanged       => "red",
            _                                => "white",
        };
    }

    #endregion

    #region Rendering

    /// <summary>
    /// Renders the given packages as a fixed-width plain-text table.
    /// The PackageId column is capped to keep the total row within the terminal width.
    /// Rows are sorted by TrustStatus, DependencyType, then PackageId.
    /// </summary>
    /// <param name="packages">The packages to display in the table.</param>
    public static void Render(IEnumerable<PackageInfo> packages)
    {
        var sorted = packages
            .OrderBy(p => p.TrustStatus)
            .ThenBy(p => p.DependencyType)
            .ThenBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sorted.Length == 0)
        {
            return;
        }

        bool hasExecData = sorted.Any(p => p.ExecutableContent is not null);

        int[] widths = ComputeWidths(sorted, hasExecData);

        WriteHeader(widths, hasExecData);
        WriteSeparator(widths, hasExecData);

        foreach (var pkg in sorted)
        {
            WriteDataRow(pkg, widths, hasExecData);
        }
    }

    #endregion

    #region Row Writers

    /// <summary>
    /// Writes the column header row using bold green markup.
    /// </summary>
    /// <param name="widths">Column widths array.</param>
    /// <param name="hasExecData">Whether to include the Exec column.</param>
    private static void WriteHeader(int[] widths, bool hasExecData)
    {
        var sb = new StringBuilder("[bold green]");
        AppendCells(sb, widths, hasExecData,
            "Type", "PackageId", "Version", "Owners", "Verified", "Trust", "Depr", "Vuln", "Exec");
        sb.Append("[/]");
        AnsiConsole.MarkupLine(sb.ToString());
    }

    /// <summary>
    /// Writes the separator line (dashes) under the header row.
    /// </summary>
    /// <param name="widths">Column widths array.</param>
    /// <param name="hasExecData">Whether to include the Exec column.</param>
    private static void WriteSeparator(int[] widths, bool hasExecData)
    {
        var parts = new List<string>
        {
            new('-', widths[ColType]),
            new('-', widths[ColId]),
            new('-', widths[ColVersion]),
            new('-', widths[ColOwners]),
            new('-', widths[ColVerified]),
            new('-', widths[ColTrust]),
            new('-', widths[ColDepr]),
            new('-', widths[ColVuln]),
        };

        if (hasExecData is true)
        {
            parts.Add(new string('-', widths[ColExec]));
        }

        Console.WriteLine(string.Join("  ", parts));
    }

    /// <summary>
    /// Writes a single data row with per-cell color markup.
    /// When the PackageId exceeds the column width, the overflow is written on
    /// subsequent continuation lines indented to the PackageId column position.
    /// Deprecated and vulnerable cells are always rendered in red.
    /// </summary>
    /// <param name="pkg">The package to render.</param>
    /// <param name="widths">Column widths array.</param>
    /// <param name="hasExecData">Whether to include the Exec column.</param>
    private static void WriteDataRow(PackageInfo pkg, int[] widths, bool hasExecData)
    {
        string rowColor  = GetTrustColor(pkg.TrustStatus);
        string deprColor = pkg.IsDeprecated ? "red" : rowColor;
        string vulnColor = pkg.HasVulnerabilities ? "red" : rowColor;

        string fullId     = pkg.PackageId;
        int idW           = widths[ColId];
        string firstLineId = fullId.Length <= idW ? fullId.PadRight(idW) : fullId[..idW];
        string idRemainder = fullId.Length > idW ? fullId[idW..] : string.Empty;

        string type     = TypeLabel(pkg).PadRight(widths[ColType]);
        string ver      = pkg.Version.PadRight(widths[ColVersion]);
        string owners   = FormatOwners(pkg.Owners).PadRight(widths[ColOwners]);
        string verified = VerifiedLabel(pkg).PadRight(widths[ColVerified]);
        string trust    = FormatTrustStatus(pkg.TrustStatus).PadRight(widths[ColTrust]);
        string depr     = (pkg.IsDeprecated ? "Yes" : "No").PadRight(widths[ColDepr]);
        string vuln     = (pkg.HasVulnerabilities ? "Yes" : "No").PadRight(widths[ColVuln]);

        var sb = new StringBuilder();
        sb.Append($"[{rowColor}]{Markup.Escape(type)}[/]");
        sb.Append($"  [{rowColor}]{Markup.Escape(firstLineId)}[/]");
        sb.Append($"  [{rowColor}]{Markup.Escape(ver)}[/]");
        sb.Append($"  [{rowColor}]{Markup.Escape(owners)}[/]");
        sb.Append($"  [{rowColor}]{Markup.Escape(verified)}[/]");
        sb.Append($"  [{rowColor}]{Markup.Escape(trust)}[/]");
        sb.Append($"  [{deprColor}]{Markup.Escape(depr)}[/]");
        sb.Append($"  [{vulnColor}]{Markup.Escape(vuln)}[/]");

        if (hasExecData is true)
        {
            string exec = ExecLabel(pkg).PadRight(widths[ColExec]);
            sb.Append($"  [{rowColor}]{Markup.Escape(exec)}[/]");
        }

        AnsiConsole.MarkupLine(sb.ToString());

        // Continuation lines for PackageId overflow — indented to the PackageId column.
        if (idRemainder.Length > 0)
        {
            string indent = new(' ', widths[ColType] + 2);
            string overflow = idRemainder;

            while (overflow.Length > 0)
            {
                int chunkLen = Math.Min(overflow.Length, idW);
                string chunk = overflow[..chunkLen];
                overflow = overflow[chunkLen..];
                AnsiConsole.MarkupLine($"[{rowColor}]{Markup.Escape(indent + chunk)}[/]");
            }
        }
    }

    #endregion

    #region Width Computation

    /// <summary>
    /// Computes the display width for each column.
    /// All columns use natural width (max of header and data values).
    /// The PackageId column is then capped so that the total row fits within the terminal width.
    /// </summary>
    /// <param name="sorted">All packages to be displayed.</param>
    /// <param name="hasExecData">Whether the Exec column will be shown.</param>
    /// <returns>An array of column widths indexed by the ColXxx constants.</returns>
    private static int[] ComputeWidths(PackageInfo[] sorted, bool hasExecData)
    {
        int typeW     = Math.Max("Type".Length,      sorted.Max(p => TypeLabel(p).Length));
        int idWNatural = Math.Max("PackageId".Length, sorted.Max(p => p.PackageId.Length));
        int verW      = Math.Max("Version".Length,   sorted.Max(p => p.Version.Length));
        int ownersW   = Math.Max("Owners".Length,    sorted.Max(p => FormatOwners(p.Owners).Length));
        int verifiedW = Math.Max("Verified".Length,  sorted.Max(p => VerifiedLabel(p).Length));
        int trustW    = Math.Max("Trust".Length,     sorted.Max(p => FormatTrustStatus(p.TrustStatus).Length));
        int deprW     = Math.Max("Depr".Length,      sorted.Max(p => (p.IsDeprecated ? "Yes" : "No").Length));
        int vulnW     = Math.Max("Vuln".Length,      sorted.Max(p => (p.HasVulnerabilities ? "Yes" : "No").Length));
        int execW     = hasExecData
            ? Math.Max("Exec".Length, sorted.Max(p => ExecLabel(p).Length))
            : 0;

        // Total width consumed by all non-PackageId columns plus 2-space separators.
        int numCols    = hasExecData ? 9 : 8;
        int separators = (numCols - 1) * 2;
        int fixedWidth = typeW + verW + ownersW + verifiedW + trustW + deprW + vulnW
            + (hasExecData ? execW : 0)
            + separators;

        // Cap PackageId to what remains within the terminal, but never below the header width.
        int available = SafeTerminalWidth() - fixedWidth;
        int idW = Math.Max("PackageId".Length, Math.Min(idWNatural, available));

        return [typeW, idW, verW, ownersW, verifiedW, trustW, deprW, vulnW, execW];
    }

    /// <summary>
    /// Returns the terminal width, falling back to 120 when output is redirected.
    /// </summary>
    /// <returns>The terminal width in characters.</returns>
    private static int SafeTerminalWidth()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch
        {
            return 120;
        }
    }

    #endregion

    #region Formatting Helpers

    /// <summary>
    /// Appends plain (uncolored) padded cell values separated by two spaces.
    /// Used for header and separator rows where a single color wraps the whole line.
    /// </summary>
    private static void AppendCells(
        StringBuilder sb, int[] widths, bool hasExecData,
        string type, string id, string ver, string owners,
        string verified, string trust, string depr, string vuln, string exec)
    {
        sb.Append(type.PadRight(widths[ColType]));
        sb.Append("  "); sb.Append(id.PadRight(widths[ColId]));
        sb.Append("  "); sb.Append(ver.PadRight(widths[ColVersion]));
        sb.Append("  "); sb.Append(owners.PadRight(widths[ColOwners]));
        sb.Append("  "); sb.Append(verified.PadRight(widths[ColVerified]));
        sb.Append("  "); sb.Append(trust.PadRight(widths[ColTrust]));
        sb.Append("  "); sb.Append(depr.PadRight(widths[ColDepr]));
        sb.Append("  "); sb.Append(vuln.PadRight(widths[ColVuln]));

        if (hasExecData is true)
        {
            sb.Append("  "); sb.Append(exec.PadRight(widths[ColExec]));
        }
    }

    /// <summary>
    /// Returns "Direct" or "Trans" based on the package's dependency type.
    /// </summary>
    /// <param name="pkg">The package to format.</param>
    /// <returns>A short dependency type label.</returns>
    private static string TypeLabel(PackageInfo pkg)
    {
        return pkg.DependencyType == DependencyType.Direct ? "Direct" : "Trans";
    }

    /// <summary>
    /// Returns "Yes", "No", or "N/A" based on the package's verified status.
    /// </summary>
    /// <param name="pkg">The package to format.</param>
    /// <returns>A display string for the Verified column.</returns>
    private static string VerifiedLabel(PackageInfo pkg)
    {
        return pkg.Verified is null ? "N/A" : (pkg.Verified.Value ? "Yes" : "No");
    }

    /// <summary>
    /// Returns a comma-joined exec content label, <c>-</c> when the package was checked and
    /// has no exec content, or <c>?</c> when the package was not in the local cache and could
    /// not be inspected.
    /// </summary>
    /// <param name="pkg">The package to format.</param>
    /// <returns>A display string for the Exec column.</returns>
    private static string ExecLabel(PackageInfo pkg)
    {
        if (pkg.ExecutableContent is null)
        {
            return "?";
        }

        if (pkg.ExecutableContent.Length == 0)
        {
            return "-";
        }

        return string.Join(",", pkg.ExecutableContent);
    }

    /// <summary>
    /// Formats an owners array as "FirstOwner +N" when there are multiple owners.
    /// </summary>
    /// <param name="owners">The raw owner names from the NuGet Search API.</param>
    /// <returns>A display string for the Owners column.</returns>
    internal static string FormatOwners(string[] owners)
    {
        if (owners.Length == 0)
        {
            return string.Empty;
        }

        if (owners.Length == 1)
        {
            return owners[0];
        }

        return $"{owners[0]} +{owners.Length - 1}";
    }

    /// <summary>
    /// Maps a <see cref="TrustStatus"/> to a short display string matching the PS tool's output.
    /// </summary>
    /// <param name="status">The trust status to format.</param>
    /// <returns>The short display string for the Trust column.</returns>
    internal static string FormatTrustStatus(TrustStatus status)
    {
        return status switch
        {
            TrustStatus.Verified             => "Verified",
            TrustStatus.PrivateFeed          => "PrivateFeed",
            TrustStatus.TrustedPackage       => "Approved",
            TrustStatus.VerifiedUnknownOwner => "UnknownOwner",
            TrustStatus.VersionChanged       => "!VerChg!",
            TrustStatus.Untrusted            => "Untrusted",
            _                                => status.ToString(),
        };
    }

    #endregion
}
