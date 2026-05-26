using NugetAudit.Core.DependencyGraph;

namespace NugetAudit.Core.Tests;

/// <summary>
/// Unit tests for <see cref="ProjectFileParser"/>, covering PackageReference parsing,
/// CPM lookup, TFM detection, .sln/.slnx resolution, and deduplication.
/// Uses a temporary directory per test to avoid cross-test interference.
/// </summary>
public sealed class ProjectFileParserTests : IDisposable
{
    #region Setup / Teardown

    /// <summary>
    /// Gets the temporary directory path used by each test.
    /// </summary>
    private string TempDir { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectFileParserTests"/> class.
    /// Creates a unique temp directory per test.
    /// </summary>
    public ProjectFileParserTests()
    {
        this.TempDir = Path.Combine(Path.GetTempPath(), $"nuget-audit-parser-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.TempDir);
    }

    /// <summary>
    /// Removes the temp directory and all its contents after each test.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(this.TempDir))
        {
            Directory.Delete(this.TempDir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    #endregion

    #region Helpers

    private string WriteCsproj(string fileName, string content)
    {
        string path = Path.Combine(this.TempDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    #endregion

    #region PackageReference — attribute form

    /// <summary>
    /// A standard PackageReference with an inline Version attribute is parsed correctly.
    /// </summary>
    [Fact]
    public void ParsePackageReferences_AttributeForm_ReturnsPackageWithVersion()
    {
        string csprojPath = this.WriteCsproj("MyApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
              </ItemGroup>
            </Project>
            """);

        var parser = new ProjectFileParser();
        var refs = parser.ParsePackageReferences(csprojPath);

        Assert.Single(refs);
        Assert.Equal("Newtonsoft.Json", refs[0].Id);
        Assert.Equal("13.0.3", refs[0].Version);
    }

    /// <summary>
    /// A PackageReference with version as a child element is parsed correctly.
    /// </summary>
    [Fact]
    public void ParsePackageReferences_ChildElementForm_ReturnsPackageWithVersion()
    {
        string csprojPath = this.WriteCsproj("MyApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json">
                  <Version>13.0.3</Version>
                </PackageReference>
              </ItemGroup>
            </Project>
            """);

        var parser = new ProjectFileParser();
        var refs = parser.ParsePackageReferences(csprojPath);

        Assert.Single(refs);
        Assert.Equal("Newtonsoft.Json", refs[0].Id);
        Assert.Equal("13.0.3", refs[0].Version);
    }

    /// <summary>
    /// Multiple PackageReferences in one .csproj are all returned.
    /// </summary>
    [Fact]
    public void ParsePackageReferences_MultiplePackages_ReturnsAll()
    {
        string csprojPath = this.WriteCsproj("MyApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                <PackageReference Include="Serilog" Version="3.1.1" />
              </ItemGroup>
            </Project>
            """);

        var parser = new ProjectFileParser();
        var refs = parser.ParsePackageReferences(csprojPath);

        Assert.Equal(2, refs.Count);
    }

    /// <summary>
    /// A PackageReference without a version and no CPM file is skipped.
    /// </summary>
    [Fact]
    public void ParsePackageReferences_NoVersionNoCpm_SkipsPackage()
    {
        string csprojPath = this.WriteCsproj("MyApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" />
              </ItemGroup>
            </Project>
            """);

        var parser = new ProjectFileParser();
        var refs = parser.ParsePackageReferences(csprojPath);

        Assert.Empty(refs);
    }

    #endregion

    #region CPM (Central Package Management)

    /// <summary>
    /// A PackageReference without a version resolves its version from Directory.Packages.props.
    /// </summary>
    [Fact]
    public void ParsePackageReferences_CpmMode_ResolvesVersionFromProps()
    {
        File.WriteAllText(Path.Combine(this.TempDir, "Directory.Packages.props"), """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Newtonsoft.Json" Version="13.0.3" />
              </ItemGroup>
            </Project>
            """);

        string csprojPath = this.WriteCsproj("MyApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" />
              </ItemGroup>
            </Project>
            """);

        var parser = new ProjectFileParser();
        var refs = parser.ParsePackageReferences(csprojPath);

        Assert.Single(refs);
        Assert.Equal("Newtonsoft.Json", refs[0].Id);
        Assert.Equal("13.0.3", refs[0].Version);
    }

    /// <summary>
    /// CPM package ID lookup is case-insensitive.
    /// </summary>
    [Fact]
    public void ParsePackageReferences_CpmMode_IdMatchIsCaseInsensitive()
    {
        File.WriteAllText(Path.Combine(this.TempDir, "Directory.Packages.props"), """
            <Project>
              <ItemGroup>
                <PackageVersion Include="NEWTONSOFT.JSON" Version="13.0.3" />
              </ItemGroup>
            </Project>
            """);

        string csprojPath = this.WriteCsproj("MyApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" />
              </ItemGroup>
            </Project>
            """);

        var parser = new ProjectFileParser();
        var refs = parser.ParsePackageReferences(csprojPath);

        Assert.Single(refs);
        Assert.Equal("13.0.3", refs[0].Version);
    }

    #endregion

    #region Deduplication

    /// <summary>
    /// The same package appearing twice in one file (same id+version) is deduplicated.
    /// </summary>
    [Fact]
    public void ParsePackageReferences_DuplicateInSameFile_Deduplicates()
    {
        string csprojPath = this.WriteCsproj("MyApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
              </ItemGroup>
            </Project>
            """);

        var parser = new ProjectFileParser();
        var refs = parser.ParsePackageReferences(csprojPath);

        Assert.Single(refs);
    }

    #endregion

    #region TargetFramework detection

    /// <summary>
    /// A single TargetFramework element is detected correctly.
    /// </summary>
    [Fact]
    public void DetectTargetFramework_SingleTfm_ReturnsTfm()
    {
        string csprojPath = this.WriteCsproj("MyApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var parser = new ProjectFileParser();
        string? tfm = parser.DetectTargetFramework(csprojPath);

        Assert.Equal("net10.0", tfm);
    }

    /// <summary>
    /// A TargetFrameworks (multi) element returns the first entry.
    /// </summary>
    [Fact]
    public void DetectTargetFramework_MultiTfm_ReturnsFirstEntry()
    {
        string csprojPath = this.WriteCsproj("MyApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net10.0;net8.0</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);

        var parser = new ProjectFileParser();
        string? tfm = parser.DetectTargetFramework(csprojPath);

        Assert.Equal("net10.0", tfm);
    }

    /// <summary>
    /// A project with no TFM element returns null.
    /// </summary>
    [Fact]
    public void DetectTargetFramework_NoTfm_ReturnsNull()
    {
        string csprojPath = this.WriteCsproj("MyApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
            </Project>
            """);

        var parser = new ProjectFileParser();
        string? tfm = parser.DetectTargetFramework(csprojPath);

        Assert.Null(tfm);
    }

    #endregion

    #region GetDeclaredTargetFrameworks

    /// <summary>
    /// A single TargetFramework returns a list with one entry.
    /// </summary>
    [Fact]
    public void GetDeclaredTargetFrameworks_SingleTfm_ReturnsSingleEntry()
    {
        string csprojPath = this.WriteCsproj("MyApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var parser = new ProjectFileParser();
        var tfms = parser.GetDeclaredTargetFrameworks(csprojPath);

        Assert.Equal(["net10.0"], tfms);
    }

    /// <summary>
    /// A TargetFrameworks (multi) element returns all entries.
    /// </summary>
    [Fact]
    public void GetDeclaredTargetFrameworks_MultiTfm_ReturnsAllEntries()
    {
        string csprojPath = this.WriteCsproj("MyApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net10.0;net8.0;netstandard2.0</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);

        var parser = new ProjectFileParser();
        var tfms = parser.GetDeclaredTargetFrameworks(csprojPath);

        Assert.Equal(["net10.0", "net8.0", "netstandard2.0"], tfms);
    }

    /// <summary>
    /// A project with no TFM elements returns an empty list.
    /// </summary>
    [Fact]
    public void GetDeclaredTargetFrameworks_NoTfm_ReturnsEmpty()
    {
        string csprojPath = this.WriteCsproj("MyApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
            </Project>
            """);

        var parser = new ProjectFileParser();
        var tfms = parser.GetDeclaredTargetFrameworks(csprojPath);

        Assert.Empty(tfms);
    }

    #endregion

    #region .sln parsing

    /// <summary>
    /// A .sln file with one project reference resolves the .csproj and parses its packages.
    /// </summary>
    [Fact]
    public void ParsePackageReferences_SlnFile_ResolvesAndParsesProjects()
    {
        string projDir = Path.Combine(this.TempDir, "src", "MyApp");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "MyApp.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
              </ItemGroup>
            </Project>
            """);

        string slnPath = Path.Combine(this.TempDir, "MySolution.sln");
        File.WriteAllText(slnPath, """

            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "MyApp", "src/MyApp/MyApp.csproj", "{GUID}"
            EndProject
            """);

        var parser = new ProjectFileParser();
        var refs = parser.ParsePackageReferences(slnPath);

        Assert.Single(refs);
        Assert.Equal("Newtonsoft.Json", refs[0].Id);
    }

    #endregion

    #region .slnx parsing

    /// <summary>
    /// A .slnx file with one project reference resolves the .csproj and parses its packages.
    /// </summary>
    [Fact]
    public void ParsePackageReferences_SlnxFile_ResolvesAndParsesProjects()
    {
        string projDir = Path.Combine(this.TempDir, "src", "MyApp");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "MyApp.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Serilog" Version="3.1.1" />
              </ItemGroup>
            </Project>
            """);

        string slnxPath = Path.Combine(this.TempDir, "MySolution.slnx");
        File.WriteAllText(slnxPath, """
            <Solution>
              <Project Path="src/MyApp/MyApp.csproj" />
            </Solution>
            """);

        var parser = new ProjectFileParser();
        var refs = parser.ParsePackageReferences(slnxPath);

        Assert.Single(refs);
        Assert.Equal("Serilog", refs[0].Id);
    }

    #endregion

    #region Directory resolution

    /// <summary>
    /// Passing a directory that contains a .csproj resolves and parses that project.
    /// </summary>
    [Fact]
    public void ParsePackageReferences_DirectoryWithCsproj_ParsesCsproj()
    {
        this.WriteCsproj("MyApp.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
              </ItemGroup>
            </Project>
            """);

        var parser = new ProjectFileParser();
        var refs = parser.ParsePackageReferences(this.TempDir);

        Assert.Single(refs);
        Assert.Equal("Newtonsoft.Json", refs[0].Id);
    }

    /// <summary>
    /// Passing a path that does not exist returns an empty list.
    /// </summary>
    [Fact]
    public void ParsePackageReferences_NonExistentPath_ReturnsEmpty()
    {
        var parser = new ProjectFileParser();
        var refs = parser.ParsePackageReferences(
            Path.Combine(this.TempDir, "does-not-exist"));

        Assert.Empty(refs);
    }

    #endregion
}
