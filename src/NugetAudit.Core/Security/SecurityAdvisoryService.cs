using System.Xml.Linq;
using NugetAudit.Core.Models;
using NugetAudit.Core.Services;

namespace NugetAudit.Core.Security;

/// <summary>
/// Provides security advisory checks for a .NET solution:
/// Package Source Mapping status, lock file enforcement, and executable content detection.
/// </summary>
public class SecurityAdvisoryService : ISecurityAdvisoryService
{
    #region PackageSourceMapping

    /// <inheritdoc />
    public PackageSourceMappingStatus CheckPackageSourceMapping(string solutionPath)
    {
        string dir = NuGetConfigWalker.GetDirectory(solutionPath);
        var configDocs = NuGetConfigWalker.WalkConfigPaths(dir)
            .Select(p => { try { return XDocument.Load(p); } catch { return null; } })
            .Where(d => d is not null)
            .Select(d => d!)
            .ToList();

        // Count distinct package sources across all in-scope configs (respecting <clear />).
        // PSM is only meaningful when multiple feeds are configured — with a single feed
        // there is no dependency confusion risk, so suppress the advisory.
        //
        // NuGet merges <packageSourceMapping> from ALL NuGet.Config files in the hierarchy
        // (unlike MSBuild, which stops at the first Directory.Build.props it finds).
        // Because NuGet merges, PSM configured at any ancestor level is legitimately active —
        // so returning Configured on first PSM match correctly reflects real NuGet behavior.
        // A fixture-level NuGet.Config without PSM cannot shield against an ancestor config
        // that has PSM; NuGet would merge both and the ancestor PSM rules would still apply.
        int sourceCount = CountPackageSources(configDocs);

        if (sourceCount <= 1)
        {
            return PackageSourceMappingStatus.Configured;
        }

        // Multiple sources: check whether PSM is configured in any of them.
        foreach (var doc in configDocs)
        {
            var psm = doc.Root?.Element("packageSourceMapping");

            if (psm is not null && psm.HasElements)
            {
                return PackageSourceMappingStatus.Configured;
            }
        }

        return PackageSourceMappingStatus.NotConfigured;
    }

    /// <summary>
    /// Counts distinct package sources across all in-scope NuGet.Config documents.
    /// Processes configs from most-global to most-local so that <c>&lt;clear /&gt;</c>
    /// in a project-level config correctly resets sources inherited from ancestor configs,
    /// mirroring NuGet's own merge semantics for <c>&lt;packageSources&gt;</c>.
    /// </summary>
    /// <param name="configDocs">Documents in local-first order (reversed internally).</param>
    /// <returns>The number of distinct package sources in scope.</returns>
    private static int CountPackageSources(List<XDocument> configDocs)
    {
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Process global-first so <clear /> in a more-local config correctly resets
        // sources inherited from less-local configs.
        foreach (var doc in Enumerable.Reverse(configDocs))
        {
            var packageSources = doc.Root?.Element("packageSources");

            if (packageSources is null)
            {
                continue;
            }

            foreach (var element in packageSources.Elements())
            {
                if (element.Name.LocalName.Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    sources.Clear();
                }
                else if (element.Name.LocalName.Equals("add", StringComparison.OrdinalIgnoreCase))
                {
                    var key = element.Attribute("key")?.Value;
                    if (key is not null)
                    {
                        sources.Add(key);
                    }
                }
                else if (element.Name.LocalName.Equals("remove", StringComparison.OrdinalIgnoreCase))
                {
                    var key = element.Attribute("key")?.Value;
                    if (key is not null)
                    {
                        sources.Remove(key);
                    }
                }
            }
        }

        return sources.Count;
    }

    #endregion

    #region LockFile

    /// <inheritdoc />
    public LockFileStatus CheckLockFile(string solutionPath)
    {
        string dir = NuGetConfigWalker.GetDirectory(solutionPath);

        // Check for packages.lock.json anywhere under the solution directory.
        bool hasLockFile = Directory
            .EnumerateFiles(dir, "packages.lock.json", SearchOption.AllDirectories)
            .Any();

        if (hasLockFile is false)
        {
            return LockFileStatus.NoLockFile;
        }

        // Check project files and Directory.Build.props for RestoreLockedMode=true.
        bool hasLockedMode = CheckRestoreLockedMode(dir);

        if (!hasLockedMode)
        {
            return LockFileStatus.LockFileNoEnforcement;
        }

        // RestoreLockedMode is enforced. Also check that a Directory.Build.targets with
        // a nuget-audit invocation exists — this is the VS pre-build enforcement layer.
        return HasBuildTarget(dir) ? LockFileStatus.LockedAndEnforced : LockFileStatus.LockedEnforcedNoBuildTarget;
    }

    /// <summary>
    /// Checks whether any .csproj or Directory.Build.props file under the directory,
    /// or any Directory.Build.props above it, contains <c>RestoreLockedMode=true</c>.
    /// </summary>
    /// <param name="dir">The root directory to search.</param>
    /// <returns>True if RestoreLockedMode is set to true in any found project or props file.</returns>
    private static bool CheckRestoreLockedMode(string dir)
    {
        // Files under the solution root.
        var candidates = Directory
            .EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(dir, "Directory.Build.props", SearchOption.AllDirectories))
            .ToList();

        // Also walk up the directory tree for Directory.Build.props outside the solution root.
        string? current = Path.GetDirectoryName(dir);

        while (current is not null)
        {
            string propsPath = Path.Combine(current, "Directory.Build.props");

            if (File.Exists(propsPath) && !candidates.Contains(propsPath))
            {
                candidates.Add(propsPath);
                break; // Stop at the first Directory.Build.props found, mirroring MSBuild walk-up behavior.
            }

            string? parent = Path.GetDirectoryName(current);

            if (parent == current)
            {
                break;
            }

            current = parent;
        }

        return candidates.Any(filePath => FileContainsRestoreLockedMode(filePath));
    }

    /// <summary>
    /// Returns true if the file contains a <c>&lt;RestoreLockedMode&gt;true&lt;/RestoreLockedMode&gt;</c> element.
    /// </summary>
    /// <param name="filePath">Path to the .csproj or .props file.</param>
    /// <returns>True if the file enables RestoreLockedMode.</returns>
    private static bool FileContainsRestoreLockedMode(string filePath)
    {
        try
        {
            string content = File.ReadAllText(filePath);

            return System.Text.RegularExpressions.Regex.IsMatch(
                content,
                @"<RestoreLockedMode\s*>\s*true\s*</RestoreLockedMode>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true if a <c>Directory.Build.targets</c> file containing a <c>nuget-audit</c>
    /// invocation exists at <paramref name="dir"/> or any ancestor directory,
    /// mirroring MSBuild's file discovery walk-up behavior.
    /// </summary>
    /// <param name="dir">The solution root directory to start from.</param>
    /// <returns>True if a qualifying Directory.Build.targets file was found.</returns>
    private static bool HasBuildTarget(string dir)
    {
        string? current = dir;

        while (current is not null)
        {
            string targetsPath = Path.Combine(current, "Directory.Build.targets");

            if (File.Exists(targetsPath))
            {
                return FileContainsNugetAudit(targetsPath);
            }

            string? parent = Path.GetDirectoryName(current);

            if (parent == current)
            {
                break;
            }

            current = parent;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the file contains a <c>nuget-audit</c> invocation.
    /// </summary>
    /// <param name="filePath">Path to the <c>Directory.Build.targets</c> file.</param>
    /// <returns>True if the file invokes nuget-audit.</returns>
    private static bool FileContainsNugetAudit(string filePath)
    {
        try
        {
            string content = File.ReadAllText(filePath);
            return content.Contains("nuget-audit", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region ExecutableContent

    /// <inheritdoc />
    public string[]? GetExecutableContent(string packageId, string version, string cachePath)
    {
        ArgumentNullException.ThrowIfNull(packageId);
        // ID is lowercased; version is NOT (matches NuGet cache layout exactly).
        string pkgPath = Path.Combine(cachePath, packageId.ToLowerInvariant(), version);

        if (!Directory.Exists(pkgPath))
        {
            // Package not in local cache — cannot check.
            return null;
        }

        var labels = new List<string>();

        // MSBuild: build/ or buildTransitive/ containing .targets or .props files.
        // NuGet auto-imports from these folders during build.
        foreach (string buildFolder in new[] { "build", "buildTransitive" })
        {
            string buildPath = Path.Combine(pkgPath, buildFolder);

            if (!Directory.Exists(buildPath))
            {
                continue;
            }

            bool hasMSBuildFiles =
                Directory.EnumerateFiles(buildPath, "*.targets", SearchOption.TopDirectoryOnly).Any()
                || Directory.EnumerateFiles(buildPath, "*.props", SearchOption.TopDirectoryOnly).Any();

            if (hasMSBuildFiles is true)
            {
                labels.Add("MSBld");
                break;
            }
        }

        // Analyzers: Roslyn analyzers loaded during compilation with full source access.
        string analyzersPath = Path.Combine(pkgPath, "analyzers");

        if (Directory.Exists(analyzersPath))
        {
            labels.Add("Alyzr");
        }

        // Tools: executables present on disk (not auto-run, but available to scripts).
        string toolsPath = Path.Combine(pkgPath, "tools");

        if (Directory.Exists(toolsPath))
        {
            labels.Add("Tools");
        }

        return [.. labels];
    }

    #endregion
}
