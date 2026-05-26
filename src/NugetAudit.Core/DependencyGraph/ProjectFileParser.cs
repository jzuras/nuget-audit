using System.Text.RegularExpressions;
using System.Xml.Linq;
using NugetAudit.Core.Models;
using NugetAudit.Core.Services;

namespace NugetAudit.Core.DependencyGraph;

/// <summary>
/// Parses PackageReference entries from .csproj, .sln, and .slnx files without requiring
/// a prior dotnet restore. Supports both attribute-form and child-element-form references,
/// and Central Package Management via Directory.Packages.props.
/// </summary>
public class ProjectFileParser : IProjectFileParser
{
    /// <inheritdoc />
    public IReadOnlyList<PackageRef> ParsePackageReferences(string path, IList<string>? parseWarnings = null)
    {
        var projectFiles = ResolveProjectFiles(path);

        if (projectFiles.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<PackageRef>();

        foreach (var csprojPath in projectFiles)
        {
            XDocument doc;

            try
            {
                doc = XDocument.Load(csprojPath);
            }
            catch (Exception ex) when (projectFiles.Count > 1)
            {
                // Multi-project solution: skip any project file that cannot be parsed rather than
                // aborting the entire run. For a single directly-provided file, let the exception
                // propagate so the caller can surface a meaningful error message.
                parseWarnings?.Add(
                    $"Could not parse '{Path.GetFileName(csprojPath)}': {ex.Message}");
                continue;
            }

            // Lazy-load CPM versions only if at least one reference lacks a version attribute.
            Dictionary<string, string>? cpmVersions = null;

            foreach (var refElement in doc.Descendants("PackageReference"))
            {
                string? id = refElement.Attribute("Include")?.Value;

                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                // Version: attribute form first, then child element form.
                string? version = refElement.Attribute("Version")?.Value
                    ?? refElement.Element("Version")?.Value;

                // CPM mode — no version on the reference; look up in Directory.Packages.props.
                if (string.IsNullOrWhiteSpace(version))
                {
                    cpmVersions ??= LoadCpmVersions(
                        Path.GetDirectoryName(csprojPath) ?? csprojPath);

                    cpmVersions.TryGetValue(id.ToLowerInvariant(), out version);
                }

                if (string.IsNullOrWhiteSpace(version))
                {
                    continue;
                }

                string key = $"{id.ToLowerInvariant()}|{version}";

                if (seen.Add(key))
                {
                    results.Add(new PackageRef(id, version!, csprojPath));
                }
            }
        }

        return results;
    }

    /// <inheritdoc />
    public string? DetectTargetFramework(string path)
    {
        var projectFiles = ResolveProjectFiles(path);

        foreach (var csprojPath in projectFiles)
        {
            XDocument doc;

            try
            {
                doc = XDocument.Load(csprojPath);
            }
            catch when (projectFiles.Count > 1)
            {
                continue;
            }

            // Single TFM: <TargetFramework>net10.0</TargetFramework>
            string? single = doc.Descendants("TargetFramework").FirstOrDefault()?.Value?.Trim();

            if (!string.IsNullOrWhiteSpace(single))
            {
                return single;
            }

            // Multi-TFM: <TargetFrameworks>net10.0;net8.0</TargetFrameworks> — take the first.
            string? multi = doc.Descendants("TargetFrameworks").FirstOrDefault()?.Value?.Trim();

            if (!string.IsNullOrWhiteSpace(multi))
            {
                string first = multi.Split(';')[0].Trim();

                if (!string.IsNullOrWhiteSpace(first))
                {
                    return first;
                }
            }
        }

        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetDeclaredTargetFrameworks(string path)
    {
        var projectFiles = ResolveProjectFiles(path);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();

        foreach (var csprojPath in projectFiles)
        {
            XDocument doc;

            try
            {
                doc = XDocument.Load(csprojPath);
            }
            catch when (projectFiles.Count > 1)
            {
                continue;
            }

            // Single TFM: <TargetFramework>net10.0</TargetFramework>
            string? single = doc.Descendants("TargetFramework").FirstOrDefault()?.Value?.Trim();

            if (!string.IsNullOrWhiteSpace(single) && seen.Add(single))
            {
                results.Add(single);
            }

            // Multi-TFM: <TargetFrameworks>net10.0;net8.0</TargetFrameworks>
            string? multi = doc.Descendants("TargetFrameworks").FirstOrDefault()?.Value?.Trim();

            if (!string.IsNullOrWhiteSpace(multi))
            {
                foreach (string part in multi.Split(';'))
                {
                    string tfm = part.Trim();

                    if (!string.IsNullOrWhiteSpace(tfm) && seen.Add(tfm))
                    {
                        results.Add(tfm);
                    }
                }
            }
        }

        return results;
    }

    #region Private Helpers

    /// <summary>
    /// Resolves a path (file or directory) to an ordered list of absolute .csproj file paths.
    /// Directory resolution prefers .slnx, then .sln, then .csproj (first found).
    /// </summary>
    /// <param name="path">Input path: a .csproj, .sln, .slnx file, or directory.</param>
    /// <returns>Ordered list of absolute .csproj paths to parse.</returns>
    private static List<string> ResolveProjectFiles(string path)
    {
        string fullPath = Path.GetFullPath(path);

        if (File.Exists(fullPath))
        {
            return Path.GetExtension(fullPath).ToLowerInvariant() switch
            {
                ".csproj" => [fullPath],
                ".sln" => ParseSlnProjects(fullPath),
                ".slnx" => ParseSlnxProjects(fullPath),
                _ => [],
            };
        }

        if (Directory.Exists(fullPath))
        {
            var slnx = Directory
                .EnumerateFiles(fullPath, "*.slnx", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();

            if (slnx is not null)
            {
                return ParseSlnxProjects(slnx);
            }

            var sln = Directory
                .EnumerateFiles(fullPath, "*.sln", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();

            if (sln is not null)
            {
                return ParseSlnProjects(sln);
            }

            var csproj = Directory
                .EnumerateFiles(fullPath, "*.csproj", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();

            if (csproj is not null)
            {
                return [csproj];
            }
        }

        return [];
    }

    /// <summary>
    /// Parses .csproj file paths from a .sln file using the standard project reference regex.
    /// </summary>
    /// <param name="slnPath">Absolute path to the .sln file.</param>
    /// <returns>Ordered list of existing .csproj absolute paths.</returns>
    private static List<string> ParseSlnProjects(string slnPath)
    {
        string slnDir = Path.GetDirectoryName(slnPath) ?? slnPath;

        string content = File.ReadAllText(slnPath);

        // A valid .sln file always contains at least one Project( entry; if it doesn't,
        // the file was not actually a solution file (e.g. it's a JSON or binary file).
        if (!content.Contains("Project(\"", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"'{slnPath}' does not appear to be a valid Visual Studio solution file.");
        }

        var matches = Regex.Matches(
            content,
            @"Project\(""[^""]*""\)\s*=\s*""[^""]*"",\s*""([^""]*\.csproj)""");

        var result = new List<string>();

        foreach (Match m in matches)
        {
            string relPath = m.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(slnDir, relPath));

            if (File.Exists(fullPath))
            {
                result.Add(fullPath);
            }
        }

        return result;
    }

    /// <summary>
    /// Parses .csproj file paths from a .slnx file.
    /// Note: .slnx XML uses forward-slash path separators even on Windows.
    /// </summary>
    /// <param name="slnxPath">Absolute path to the .slnx file.</param>
    /// <returns>Ordered list of existing .csproj absolute paths.</returns>
    private static List<string> ParseSlnxProjects(string slnxPath)
    {
        string slnxDir = Path.GetDirectoryName(slnxPath) ?? slnxPath;
        var result = new List<string>();

        var doc = XDocument.Load(slnxPath);

        foreach (var projElement in doc.Descendants("Project"))
        {
            string? relPath = projElement.Attribute("Path")?.Value;

            if (string.IsNullOrWhiteSpace(relPath))
            {
                continue;
            }

            if (!relPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // .slnx uses forward slashes even on Windows — normalize to platform separator.
            string normalized = relPath.Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(slnxDir, normalized));

            if (File.Exists(fullPath))
            {
                result.Add(fullPath);
            }
        }

        return result;
    }

    /// <summary>
    /// Walks up the directory tree from <paramref name="startDir"/> to locate the nearest
    /// Directory.Packages.props and returns a case-insensitive map of lowercase package ID → version.
    /// Returns an empty dictionary if no file is found.
    /// </summary>
    /// <param name="startDir">Directory to begin the upward search from.</param>
    /// <returns>Case-insensitive map of lowercase package ID to version string.</returns>
    private static Dictionary<string, string> LoadCpmVersions(string startDir)
    {
        string? current = startDir;

        while (current is not null)
        {
            string propsPath = Path.Combine(current, "Directory.Packages.props");

            if (File.Exists(propsPath))
            {
                try
                {
                    var doc = XDocument.Load(propsPath);
                    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var element in doc.Descendants("PackageVersion"))
                    {
                        string? id = element.Attribute("Include")?.Value;
                        string? version = element.Attribute("Version")?.Value;

                        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(version))
                        {
                            map[id.ToLowerInvariant()] = version!;
                        }
                    }

                    return map;
                }
                catch
                {
                    // Malformed Directory.Packages.props — skip this level and continue walking up.
                }
            }

            string? parent = Path.GetDirectoryName(current);

            if (parent == current)
            {
                break;
            }

            current = parent;
        }

        return [];
    }

    #endregion
}
