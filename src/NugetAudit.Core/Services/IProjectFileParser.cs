using NugetAudit.Core.Models;

namespace NugetAudit.Core.Services;

/// <summary>
/// Parses package references from project files (.csproj), solution files (.sln, .slnx), or directories.
/// Supports both attribute-form and child-element-form PackageReference, and Central Package Management.
/// </summary>
public interface IProjectFileParser
{
    /// <summary>
    /// Parses all PackageReference entries from the specified path.
    /// </summary>
    /// <param name="path">
    /// Path to a .csproj file, .sln file, .slnx file, or directory.
    /// For .sln and .slnx files, all referenced .csproj files are parsed.
    /// For directories, the directory is searched for a .sln, .slnx, or .csproj file.
    /// </param>
    /// <param name="parseWarnings">
    /// Optional list to collect non-fatal warnings. When a project file in a multi-project
    /// solution cannot be parsed, its error is appended here rather than aborting the run.
    /// Pass <c>null</c> to discard warnings silently.
    /// </param>
    /// <returns>All package references found, deduplicated by package ID.</returns>
    IReadOnlyList<PackageRef> ParsePackageReferences(string path, IList<string>? parseWarnings = null);

    /// <summary>
    /// Detects the target framework from the first .csproj reachable from <paramref name="path"/>.
    /// Reads <c>&lt;TargetFramework&gt;</c> first; falls back to the first entry in
    /// <c>&lt;TargetFrameworks&gt;</c> for multi-targeting projects.
    /// </summary>
    /// <param name="path">Path to a .csproj file, .sln file, .slnx file, or directory.</param>
    /// <returns>The TFM string (e.g. "net10.0"), or <c>null</c> if it cannot be determined.</returns>
    string? DetectTargetFramework(string path);

    /// <summary>
    /// Returns all target framework monikers (TFMs) declared across the project files reachable
    /// from <paramref name="path"/>. Reads <c>&lt;TargetFramework&gt;</c> and all
    /// semicolon-delimited entries from <c>&lt;TargetFrameworks&gt;</c>.
    /// Results are deduplicated (case-insensitive) and preserve declaration order.
    /// </summary>
    /// <param name="path">Path to a .csproj file, .sln file, .slnx file, or directory.</param>
    /// <returns>Ordered, deduplicated list of TFM strings. Empty list if none can be determined.</returns>
    IReadOnlyList<string> GetDeclaredTargetFrameworks(string path);
}
