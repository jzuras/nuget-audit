using System.Globalization;
using NugetAudit.Core.Models;

namespace NugetAudit.Cli.Output;

/// <summary>
/// Formats audit results as CSV, either to stdout or to a file.
/// </summary>
internal static class CsvFormatter
{
    /// <summary>
    /// Gets the CSV header line.
    /// </summary>
    private static string Header { get; } =
        "DependencyType,PackageId,Version,Authors,Owners,Verified,TrustStatus,LicenseExpression,LicenseUrl,IsDeprecated,HasVulnerabilities,Published,ProjectUrl,ExecutableContent";

    /// <summary>
    /// Writes audit results as CSV. When <paramref name="outputFile"/> is provided,
    /// writes to the file; otherwise writes to stdout.
    /// </summary>
    /// <param name="packages">The packages to format.</param>
    /// <param name="outputFile">Optional output file path. Null writes to stdout.</param>
    public static void Write(IEnumerable<PackageInfo> packages, string? outputFile)
    {
        var sorted = packages
            .OrderBy(p => p.TrustStatus)
            .ThenBy(p => p.DependencyType)
            .ThenBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase);

        if (outputFile is not null)
        {
            string resolvedPath = Path.GetFullPath(outputFile);
            string? parentDir = Path.GetDirectoryName(resolvedPath);

            if (parentDir is not null && !Directory.Exists(parentDir))
            {
                throw new DirectoryNotFoundException(
                    $"Output directory does not exist: {parentDir}");
            }

            using var writer = new StreamWriter(resolvedPath);
            writer.WriteLine(CsvFormatter.Header);

            foreach (var pkg in sorted)
            {
                writer.WriteLine(ToCsvLine(pkg));
            }

            Console.WriteLine($"CSV output saved to: {resolvedPath}");
        }
        else
        {
            Console.WriteLine(CsvFormatter.Header);

            foreach (var pkg in sorted)
            {
                Console.WriteLine(ToCsvLine(pkg));
            }
        }
    }

    /// <summary>
    /// Converts a single <see cref="PackageInfo"/> to a CSV line.
    /// Fields containing commas or quotes are quoted and escaped.
    /// </summary>
    /// <param name="pkg">The package to format.</param>
    /// <returns>A CSV-formatted string for the package.</returns>
    private static string ToCsvLine(PackageInfo pkg)
    {
        string owners = string.Join("|", pkg.Owners);
        string published = pkg.Published?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) ?? string.Empty;

        string executableContent = pkg.ExecutableContent is null
            ? "?"
            : pkg.ExecutableContent.Length == 0
                ? "-"
                : string.Join("|", pkg.ExecutableContent);

        return string.Join(",",
            CsvEscape(pkg.DependencyType.ToString()),
            CsvEscape(pkg.PackageId),
            CsvEscape(pkg.Version),
            CsvEscape(pkg.Authors),
            CsvEscape(owners),
            pkg.Verified?.ToString() ?? string.Empty,
            CsvEscape(pkg.TrustStatus.ToString()),
            CsvEscape(pkg.LicenseExpression ?? string.Empty),
            CsvEscape(pkg.LicenseUrl ?? string.Empty),
            pkg.IsDeprecated.ToString(),
            pkg.HasVulnerabilities.ToString(),
            CsvEscape(published),
            CsvEscape(pkg.ProjectUrl ?? string.Empty),
            CsvEscape(executableContent));
    }

    /// <summary>
    /// Escapes a value for CSV by wrapping in quotes when it contains a comma, quote, or newline.
    /// </summary>
    /// <param name="value">The value to escape.</param>
    /// <returns>The CSV-safe value.</returns>
    private static string CsvEscape(string value)
    {
        if (value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal))
        {
            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return value;
    }
}
