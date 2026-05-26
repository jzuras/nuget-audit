using NugetAudit.Core.DependencyGraph;
using NugetAudit.Core.Models;

namespace NugetAudit.Core.Tests;

/// <summary>
/// Unit tests for <see cref="DotnetListPackageRunner.DeduplicatePackages"/>.
/// Tests the static deduplication logic without shelling out to dotnet.
/// </summary>
public class DotnetListPackageRunnerTests
{
    #region Helpers

    /// <summary>
    /// Creates a temporary directory and deletes it on dispose.
    /// </summary>
    private sealed class TempDirectory : IDisposable
    {
        public string FullName { get; } = Directory.CreateTempSubdirectory().FullName;
        public void Dispose() => Directory.Delete(this.FullName, recursive: true);
    }

    /// <summary>
    /// Builds a <see cref="DotnetListOutput"/> from a single project and single framework.
    /// </summary>
    private static DotnetListOutput MakeOutput(
        DotnetListPackageRef[]? topLevel,
        DotnetListPackageRef[]? transitive)
    {
        var framework = new DotnetListFramework("net10.0", topLevel, transitive);
        var project = new DotnetListProject("C:\\fake\\Project.csproj", [framework]);
        return new DotnetListOutput([project]);
    }

    /// <summary>
    /// Creates a package reference with the given ID and version.
    /// </summary>
    private static DotnetListPackageRef Pkg(string id, string version)
    {
        return new DotnetListPackageRef(id, "*", version);
    }

    #endregion

    #region Basic Cases

    [Fact]
    public void DirectPackagesAreReturnedWithDirectType()
    {
        var output = MakeOutput(
            topLevel: [Pkg("Newtonsoft.Json", "13.0.3")],
            transitive: null);

        var result = DotnetListPackageRunner.DeduplicatePackages(output);

        Assert.Single(result);
        Assert.Equal("Newtonsoft.Json", result[0].Id);
        Assert.Equal("13.0.3", result[0].Version);
        Assert.Equal(DependencyType.Direct, result[0].DependencyType);
    }

    [Fact]
    public void TransitivePackagesAreReturnedWithTransitiveType()
    {
        var output = MakeOutput(
            topLevel: null,
            transitive: [Pkg("System.Text.Json", "9.0.0")]);

        var result = DotnetListPackageRunner.DeduplicatePackages(output);

        Assert.Single(result);
        Assert.Equal("System.Text.Json", result[0].Id);
        Assert.Equal(DependencyType.Transitive, result[0].DependencyType);
    }

    [Fact]
    public void DirectWinsOverTransitiveForSameIdAndVersion()
    {
        // Same package appears as both direct and transitive (different projects/frameworks).
        var output = MakeOutput(
            topLevel: [Pkg("Serilog", "4.2.0")],
            transitive: [Pkg("Serilog", "4.2.0")]);

        var result = DotnetListPackageRunner.DeduplicatePackages(output);

        Assert.Single(result);
        Assert.Equal(DependencyType.Direct, result[0].DependencyType);
    }

    [Fact]
    public void DifferentVersionsAreTreatedAsSeparateEntries()
    {
        // Same ID but different resolved versions — both survive dedup.
        var output = MakeOutput(
            topLevel: [Pkg("Serilog", "3.1.0")],
            transitive: [Pkg("Serilog", "4.2.0")]);

        var result = DotnetListPackageRunner.DeduplicatePackages(output);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void CaseInsensitiveIdDedup_DirectWins()
    {
        // "serilog" (from transitive) and "Serilog" (from direct) — same normalized key, direct wins.
        var framework1 = new DotnetListFramework(
            "net10.0",
            TopLevelPackages: [Pkg("Serilog", "4.2.0")],
            TransitivePackages: null);

        var framework2 = new DotnetListFramework(
            "net10.0",
            TopLevelPackages: null,
            TransitivePackages: [Pkg("serilog", "4.2.0")]);

        var project = new DotnetListProject("C:\\fake\\Project.csproj", [framework1, framework2]);
        var output = new DotnetListOutput([project]);

        var result = DotnetListPackageRunner.DeduplicatePackages(output);

        Assert.Single(result);
        Assert.Equal(DependencyType.Direct, result[0].DependencyType);
    }

    #endregion

    #region Multi-Project / Multi-Framework

    [Fact]
    public void PackagesAcrossMultipleProjectsAreAggregated()
    {
        var project1 = new DotnetListProject(
            "C:\\fake\\Project1.csproj",
            [new DotnetListFramework("net10.0", [Pkg("PackageA", "1.0.0")], null)]);

        var project2 = new DotnetListProject(
            "C:\\fake\\Project2.csproj",
            [new DotnetListFramework("net10.0", [Pkg("PackageB", "2.0.0")], null)]);

        var output = new DotnetListOutput([project1, project2]);

        var result = DotnetListPackageRunner.DeduplicatePackages(output);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.Id == "PackageA");
        Assert.Contains(result, p => p.Id == "PackageB");
    }

    [Fact]
    public void SamePackageInMultipleProjectsDeduplicates()
    {
        // Same package referenced in two projects — should appear once.
        var project1 = new DotnetListProject(
            "C:\\fake\\Project1.csproj",
            [new DotnetListFramework("net10.0", [Pkg("Shared", "5.0.0")], null)]);

        var project2 = new DotnetListProject(
            "C:\\fake\\Project2.csproj",
            [new DotnetListFramework("net10.0", [Pkg("Shared", "5.0.0")], null)]);

        var output = new DotnetListOutput([project1, project2]);

        var result = DotnetListPackageRunner.DeduplicatePackages(output);

        Assert.Single(result);
    }

    [Fact]
    public void DirectInOneProjectWinsOverTransitiveInAnother()
    {
        var project1 = new DotnetListProject(
            "C:\\fake\\Project1.csproj",
            [new DotnetListFramework("net10.0", null, [Pkg("Lib", "3.0.0")])]);

        var project2 = new DotnetListProject(
            "C:\\fake\\Project2.csproj",
            [new DotnetListFramework("net10.0", [Pkg("Lib", "3.0.0")], null)]);

        var output = new DotnetListOutput([project1, project2]);

        var result = DotnetListPackageRunner.DeduplicatePackages(output);

        Assert.Single(result);
        Assert.Equal(DependencyType.Direct, result[0].DependencyType);
    }

    #endregion

    #region ResolveProjectPath

    [Fact]
    public void ResolveProjectPath_FileInput_ReturnsUnchanged()
    {
        string filePath = Path.Combine(Path.GetTempPath(), "MySolution.slnx");
        string result = DotnetListPackageRunner.ResolveProjectPath(filePath);
        Assert.Equal(filePath, result);
    }

    [Fact]
    public void ResolveProjectPath_DirectoryWithSlnx_ReturnsSlnx()
    {
        using var dir = new TempDirectory();
        string slnxPath = Path.Combine(dir.FullName, "MySolution.slnx");
        File.WriteAllText(slnxPath, string.Empty);

        string result = DotnetListPackageRunner.ResolveProjectPath(dir.FullName);

        Assert.Equal(slnxPath, result);
    }

    [Fact]
    public void ResolveProjectPath_DirectoryWithSln_ReturnsSln()
    {
        using var dir = new TempDirectory();
        string slnPath = Path.Combine(dir.FullName, "MySolution.sln");
        File.WriteAllText(slnPath, string.Empty);

        string result = DotnetListPackageRunner.ResolveProjectPath(dir.FullName);

        Assert.Equal(slnPath, result);
    }

    [Fact]
    public void ResolveProjectPath_DirectoryWithCsproj_ReturnsCsproj()
    {
        using var dir = new TempDirectory();
        string csprojPath = Path.Combine(dir.FullName, "MyProject.csproj");
        File.WriteAllText(csprojPath, string.Empty);

        string result = DotnetListPackageRunner.ResolveProjectPath(dir.FullName);

        Assert.Equal(csprojPath, result);
    }

    [Fact]
    public void ResolveProjectPath_SlnxPrecedesSlnWhenBothPresent()
    {
        using var dir = new TempDirectory();
        string slnxPath = Path.Combine(dir.FullName, "MySolution.slnx");
        File.WriteAllText(slnxPath, string.Empty);
        File.WriteAllText(Path.Combine(dir.FullName, "MySolution.sln"), string.Empty);

        string result = DotnetListPackageRunner.ResolveProjectPath(dir.FullName);

        Assert.Equal(slnxPath, result);
    }

    [Fact]
    public void ResolveProjectPath_MultipleSlnx_Throws()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.FullName, "A.slnx"), string.Empty);
        File.WriteAllText(Path.Combine(dir.FullName, "B.slnx"), string.Empty);

        var ex = Assert.Throws<InvalidOperationException>(
            () => DotnetListPackageRunner.ResolveProjectPath(dir.FullName));

        Assert.Contains(".slnx", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--path", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveProjectPath_MultipleSln_Throws()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.FullName, "A.sln"), string.Empty);
        File.WriteAllText(Path.Combine(dir.FullName, "B.sln"), string.Empty);

        var ex = Assert.Throws<InvalidOperationException>(
            () => DotnetListPackageRunner.ResolveProjectPath(dir.FullName));

        Assert.Contains(".sln", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--path", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveProjectPath_MultipleCsproj_Throws()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(Path.Combine(dir.FullName, "A.csproj"), string.Empty);
        File.WriteAllText(Path.Combine(dir.FullName, "B.csproj"), string.Empty);

        var ex = Assert.Throws<InvalidOperationException>(
            () => DotnetListPackageRunner.ResolveProjectPath(dir.FullName));

        Assert.Contains(".csproj", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--path", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveProjectPath_EmptyDirectory_ReturnsDirectoryUnchanged()
    {
        using var dir = new TempDirectory();

        string result = DotnetListPackageRunner.ResolveProjectPath(dir.FullName);

        Assert.Equal(dir.FullName, result);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void EmptyProjectListReturnsEmpty()
    {
        var output = new DotnetListOutput([]);

        var result = DotnetListPackageRunner.DeduplicatePackages(output);

        Assert.Empty(result);
    }

    [Fact]
    public void NullPackageLists_DoNotThrow()
    {
        var framework = new DotnetListFramework("net10.0", null, null);
        var project = new DotnetListProject("C:\\fake\\Project.csproj", [framework]);
        var output = new DotnetListOutput([project]);

        var result = DotnetListPackageRunner.DeduplicatePackages(output);

        Assert.Empty(result);
    }

    #endregion
}
