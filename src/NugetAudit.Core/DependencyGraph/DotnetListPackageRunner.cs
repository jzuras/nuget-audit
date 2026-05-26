using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NugetAudit.Core.Models;

namespace NugetAudit.Core.DependencyGraph;

/// <summary>
/// Shells out to <c>dotnet list package --include-transitive --format json</c> and returns
/// a deduplicated list of resolved packages with their dependency types.
/// Direct dependencies take precedence over transitives when the same ID+version appears in both.
/// </summary>
public static class DotnetListPackageRunner
{
    #region Constants

    private static JsonSerializerOptions JsonOptions => CoreJsonOptions.CaseInsensitive;

    #endregion

    /// <summary>
    /// Runs <c>dotnet list package --include-transitive --format json</c> for the given path,
    /// deserializes the output, and returns deduplicated package entries with dependency types.
    /// Direct dependencies win over transitives for the same package ID and resolved version.
    /// </summary>
    /// <param name="path">Path to a .csproj, .sln, .slnx, or directory to pass to dotnet.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A tuple of the deduplicated package list and the total number of projects found.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>dotnet list package</c> exits with a non-zero code.
    /// </exception>
    public static async Task<(IReadOnlyList<PackageListEntry> Packages, int TotalProjects)> RunAsync(
        string path,
        CancellationToken ct)
    {
        string stdout = await ExecuteDotnetListAsync(path, ct);

        var output = JsonSerializer.Deserialize<DotnetListOutput>(stdout, DotnetListPackageRunner.JsonOptions);

        if (output is null || output.Projects is null || output.Projects.Length == 0)
        {
            return (Array.Empty<PackageListEntry>(), 0);
        }

        var packages = DeduplicatePackages(output);

        return (packages, output.Projects.Length);
    }

    /// <summary>
    /// Resolves a directory path to a concrete solution or project file.
    /// Searches in order: .slnx → .sln → .csproj.
    /// Returns the original path unchanged if it is already a file.
    /// </summary>
    /// <param name="path">Path to a file or directory.</param>
    /// <returns>The resolved file path to pass to dotnet list.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when multiple files of the same type are found (ambiguous).
    /// </exception>
    internal static string ResolveProjectPath(string path)
    {
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!Directory.Exists(path))
        {
            return path;
        }

        string[] extensions = [".slnx", ".sln", ".csproj"];

        foreach (string ext in extensions)
        {
            string[] matches = Directory.GetFiles(path, $"*{ext}", SearchOption.TopDirectoryOnly);

            if (matches.Length == 1)
            {
                return matches[0];
            }

            if (matches.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Multiple {ext} files found in '{path}'. Specify one directly with --path.");
            }
        }

        return path;
    }

    /// <summary>
    /// Executes the dotnet list package command and returns the raw JSON stdout.
    /// </summary>
    /// <param name="path">The path argument for dotnet list.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The raw JSON string from stdout.</returns>
    /// <exception cref="InvalidOperationException">Thrown on non-zero exit code.</exception>
    private static async Task<string> ExecuteDotnetListAsync(string path, CancellationToken ct)
    {
        string resolvedPath = ResolveProjectPath(path);

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("list");
        psi.ArgumentList.Add(resolvedPath);
        psi.ArgumentList.Add("package");
        psi.ArgumentList.Add("--include-transitive");
        psi.ArgumentList.Add("--format");
        psi.ArgumentList.Add("json");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet process.");

        string stdout = await process.StandardOutput.ReadToEndAsync(ct);
        string stderr = await process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            // dotnet list package writes errors to stdout as JSON (problems array), not stderr.
            // Try to extract the human-readable problem text before falling back to stderr.
            string errorDetail = ExtractProblemsText(stdout)
                ?? stderr.Trim();

            throw new InvalidOperationException(
                $"dotnet list package failed (exit code {process.ExitCode}): {errorDetail}");
        }

        return stdout;
    }

    /// <summary>
    /// Extracts the human-readable error text from the JSON <c>problems</c> array that
    /// <c>dotnet list package</c> writes to stdout on non-zero exit (e.g. restore failures).
    /// Returns null if the output cannot be parsed or contains no problem text.
    /// </summary>
    /// <param name="stdout">The raw stdout from a failed <c>dotnet list package</c> invocation.</param>
    /// <returns>Concatenated problem text, or null if unavailable.</returns>
    private static string? ExtractProblemsText(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(stdout);

            if (!doc.RootElement.TryGetProperty("problems", out var problems))
            {
                return null;
            }

            var texts = new List<string>();

            foreach (var problem in problems.EnumerateArray())
            {
                if (problem.TryGetProperty("text", out var text))
                {
                    string? msg = text.GetString();

                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        texts.Add(msg);
                    }
                }
            }

            return texts.Count > 0 ? string.Join(" ", texts) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Deduplicates packages from all projects and frameworks.
    /// Key = <c>id.ToLowerInvariant()|resolvedVersion</c>.
    /// Direct dependencies take precedence over transitives across all frameworks.
    /// </summary>
    /// <param name="output">The deserialized dotnet list package output.</param>
    /// <returns>A read-only list of unique <see cref="PackageListEntry"/> records.</returns>
    internal static IReadOnlyList<PackageListEntry> DeduplicatePackages(DotnetListOutput output)
    {
        var packages = new Dictionary<string, PackageListEntry>(StringComparer.Ordinal);

        // First pass: all Direct (top-level) packages — they always win.
        foreach (var project in output.Projects)
        {
            foreach (var framework in project.Frameworks)
            {
                if (framework.TopLevelPackages is null)
                {
                    continue;
                }

                foreach (var pkg in framework.TopLevelPackages)
                {
                    string key = BuildKey(pkg.Id, pkg.ResolvedVersion);
                    packages[key] = new PackageListEntry(pkg.Id, pkg.ResolvedVersion, DependencyType.Direct);
                }
            }
        }

        // Second pass: Transitive packages — only add if not already present as Direct.
        foreach (var project in output.Projects)
        {
            foreach (var framework in project.Frameworks)
            {
                if (framework.TransitivePackages is null)
                {
                    continue;
                }

                foreach (var pkg in framework.TransitivePackages)
                {
                    string key = BuildKey(pkg.Id, pkg.ResolvedVersion);

                    if (!packages.ContainsKey(key))
                    {
                        packages[key] = new PackageListEntry(
                            pkg.Id, pkg.ResolvedVersion, DependencyType.Transitive);
                    }
                }
            }
        }

        return [.. packages.Values];
    }

    /// <summary>
    /// Builds the deduplication key for a package ID and resolved version.
    /// </summary>
    /// <param name="id">The package identifier.</param>
    /// <param name="resolvedVersion">The resolved concrete version.</param>
    /// <returns>A string key of the form <c>id_lower|version</c>.</returns>
    private static string BuildKey(string id, string resolvedVersion)
    {
        return $"{id.ToLowerInvariant()}|{resolvedVersion}";
    }
}
