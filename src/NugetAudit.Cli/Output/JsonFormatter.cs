using System.Globalization;
using System.Text.Json;
using NugetAudit.Core.Models;

namespace NugetAudit.Cli.Output;

/// <summary>
/// Formats audit results as JSON, either to stdout or to a file.
/// </summary>
internal static class JsonFormatter
{
    /// <summary>
    /// Gets the JSON serialization options.
    /// </summary>
    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Writes audit results as JSON. When <paramref name="outputFile"/> is provided,
    /// writes to the file; otherwise writes to stdout.
    /// </summary>
    /// <param name="packages">The packages to format.</param>
    /// <param name="outputFile">Optional output file path. Null writes to stdout.</param>
    public static void Write(IEnumerable<PackageInfo> packages, string? outputFile)
    {
        var sorted = packages
            .OrderBy(p => p.TrustStatus)
            .ThenBy(p => p.DependencyType)
            .ThenBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
            .Select(ToDto)
            .ToArray();

        string json = JsonSerializer.Serialize(sorted, JsonFormatter.JsonOptions);

        if (outputFile is not null)
        {
            string resolvedPath = Path.GetFullPath(outputFile);
            string? parentDir = Path.GetDirectoryName(resolvedPath);

            if (parentDir is not null && !Directory.Exists(parentDir))
            {
                throw new DirectoryNotFoundException(
                    $"Output directory does not exist: {parentDir}");
            }

            File.WriteAllText(resolvedPath, json);
            Console.WriteLine($"JSON output saved to: {resolvedPath}");
        }
        else
        {
            Console.WriteLine(json);
        }
    }

    /// <summary>
    /// Projects a <see cref="PackageInfo"/> into an anonymous DTO for JSON serialization.
    /// </summary>
    /// <param name="pkg">The package to project.</param>
    /// <returns>An anonymous object suitable for JSON output.</returns>
    private static object ToDto(PackageInfo pkg)
    {
        return new
        {
            dependencyType = pkg.DependencyType.ToString(),
            packageId = pkg.PackageId,
            version = pkg.Version,
            authors = pkg.Authors,
            owners = pkg.Owners,
            verified = pkg.Verified,
            trustStatus = pkg.TrustStatus.ToString(),
            licenseExpression = pkg.LicenseExpression,
            licenseUrl = pkg.LicenseUrl,
            isDeprecated = pkg.IsDeprecated,
            hasVulnerabilities = pkg.HasVulnerabilities,
            published = pkg.Published?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            projectUrl = pkg.ProjectUrl,
            executableContent = pkg.ExecutableContent,
        };
    }
}
