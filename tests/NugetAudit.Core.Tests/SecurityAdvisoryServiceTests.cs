using NugetAudit.Core.Models;
using NugetAudit.Core.Security;

namespace NugetAudit.Core.Tests;

/// <summary>
/// Unit tests for <see cref="SecurityAdvisoryService"/> using temporary file-system fixtures.
/// Each test creates its own temp directory, written as needed, and cleans up on dispose.
/// </summary>
public sealed class SecurityAdvisoryServiceTests : IDisposable
{
    #region Properties

    /// <summary>Gets the <see cref="SecurityAdvisoryService"/> under test.</summary>
    private SecurityAdvisoryService Sut { get; }

    /// <summary>Gets the root of the temporary directory used for file-system fixtures in this test run.</summary>
    private string TempRoot { get; }

    #endregion

    #region Setup / Teardown

    /// <summary>
    /// Initializes a new instance of <see cref="SecurityAdvisoryServiceTests"/>.
    /// Creates a unique temp directory for file-system fixtures.
    /// </summary>
    public SecurityAdvisoryServiceTests()
    {
        this.Sut = new SecurityAdvisoryService();
        this.TempRoot = Path.Combine(Path.GetTempPath(), $"nuget-audit-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.TempRoot);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(this.TempRoot))
        {
            Directory.Delete(this.TempRoot, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    #endregion

    #region CheckPackageSourceMapping

    [Fact]
    public void CheckPackageSourceMapping_SingleSource_NoPsm_ReturnsConfigured()
    {
        // One feed configured — dependency confusion is impossible, so advisory is suppressed
        // even though PSM is absent. Uses <clear /> to isolate from the global NuGet.Config.
        string nugetConfig = Path.Combine(this.TempRoot, "NuGet.Config");

        File.WriteAllText(nugetConfig, """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """);

        var status = this.Sut.CheckPackageSourceMapping(this.TempRoot);

        Assert.Equal(PackageSourceMappingStatus.Configured, status);
    }

    [Fact]
    public void CheckPackageSourceMapping_MultipleSources_WithPsm_ReturnsConfigured()
    {
        string nugetConfig = Path.Combine(this.TempRoot, "NuGet.Config");

        File.WriteAllText(nugetConfig, """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                <add key="private" value="https://pkgs.example.com/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="nuget.org">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        var status = this.Sut.CheckPackageSourceMapping(this.TempRoot);

        Assert.Equal(PackageSourceMappingStatus.Configured, status);
    }

    [Fact]
    public void CheckPackageSourceMapping_MultipleSources_NoPsmElement_ReturnsNotConfigured()
    {
        // Two feeds, no PSM — dependency confusion is possible, advisory must fire.
        string nugetConfig = Path.Combine(this.TempRoot, "NuGet.Config");

        File.WriteAllText(nugetConfig, """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                <add key="private" value="https://pkgs.example.com/v3/index.json" />
              </packageSources>
            </configuration>
            """);

        var status = this.Sut.CheckPackageSourceMapping(this.TempRoot);

        Assert.Equal(PackageSourceMappingStatus.NotConfigured, status);
    }

    [Fact]
    public void CheckPackageSourceMapping_MultipleSources_EmptyPsmElement_ReturnsNotConfigured()
    {
        // Two feeds, empty <packageSourceMapping /> — HasElements is false, advisory fires.
        string nugetConfig = Path.Combine(this.TempRoot, "NuGet.Config");

        File.WriteAllText(nugetConfig, """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                <add key="private" value="https://pkgs.example.com/v3/index.json" />
              </packageSources>
              <packageSourceMapping />
            </configuration>
            """);

        var status = this.Sut.CheckPackageSourceMapping(this.TempRoot);

        Assert.Equal(PackageSourceMappingStatus.NotConfigured, status);
    }

    [Fact]
    public void CheckPackageSourceMapping_ClearInLocalConfig_ResetsAncestorSources_ReturnsConfigured()
    {
        // Parent dir has two sources; child dir clears them and adds only one.
        // After clear, only one source is in scope → advisory suppressed.
        string parentConfig = Path.Combine(this.TempRoot, "NuGet.Config");
        string childDir = Path.Combine(this.TempRoot, "child");
        Directory.CreateDirectory(childDir);
        string childConfig = Path.Combine(childDir, "NuGet.Config");

        File.WriteAllText(parentConfig, """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                <add key="private" value="https://pkgs.example.com/v3/index.json" />
              </packageSources>
            </configuration>
            """);

        File.WriteAllText(childConfig, """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """);

        var status = this.Sut.CheckPackageSourceMapping(childDir);

        Assert.Equal(PackageSourceMappingStatus.Configured, status);
    }

    #endregion

    #region CheckLockFile

    [Fact]
    public void CheckLockFile_NoLockFile_ReturnsNoLockFile()
    {
        var status = this.Sut.CheckLockFile(this.TempRoot);

        Assert.Equal(LockFileStatus.NoLockFile, status);
    }

    [Fact]
    public void CheckLockFile_LockFilePresentButNoLockedMode_ReturnsNoEnforcement()
    {
        // Create a packages.lock.json but no csproj with RestoreLockedMode.
        string projDir = Path.Combine(this.TempRoot, "MyProject");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "packages.lock.json"), "{}");
        File.WriteAllText(Path.Combine(projDir, "MyProject.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var status = this.Sut.CheckLockFile(this.TempRoot);

        Assert.Equal(LockFileStatus.LockFileNoEnforcement, status);
    }

    [Fact]
    public void CheckLockFile_LockFileWithRestoreLockedModeInCsproj_ReturnsLockedAndEnforced()
    {
        string projDir = Path.Combine(this.TempRoot, "MyProject");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "packages.lock.json"), "{}");
        File.WriteAllText(Path.Combine(projDir, "MyProject.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <RestoreLockedMode>true</RestoreLockedMode>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(this.TempRoot, "Directory.Build.targets"), """
            <Project>
              <Target Name="AuditIfRestoreChanged" BeforeTargets="Build">
                <Exec Command="nuget-audit audit --check" />
              </Target>
            </Project>
            """);

        var status = this.Sut.CheckLockFile(this.TempRoot);

        Assert.Equal(LockFileStatus.LockedAndEnforced, status);
    }

    [Fact]
    public void CheckLockFile_LockFileWithRestoreLockedModeInBuildProps_ReturnsLockedAndEnforced()
    {
        string projDir = Path.Combine(this.TempRoot, "src");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "packages.lock.json"), "{}");
        File.WriteAllText(Path.Combine(this.TempRoot, "Directory.Build.props"), """
            <Project>
              <PropertyGroup>
                <RestoreLockedMode>true</RestoreLockedMode>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(this.TempRoot, "Directory.Build.targets"), """
            <Project>
              <Target Name="AuditIfRestoreChanged" BeforeTargets="Build">
                <Exec Command="nuget-audit audit --check" />
              </Target>
            </Project>
            """);

        var status = this.Sut.CheckLockFile(this.TempRoot);

        Assert.Equal(LockFileStatus.LockedAndEnforced, status);
    }

    [Fact]
    public void CheckLockFile_RestoreLockedModeFalse_ReturnsNoEnforcement()
    {
        string projDir = Path.Combine(this.TempRoot, "MyProject");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "packages.lock.json"), "{}");
        File.WriteAllText(Path.Combine(projDir, "MyProject.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <RestoreLockedMode>false</RestoreLockedMode>
              </PropertyGroup>
            </Project>
            """);

        var status = this.Sut.CheckLockFile(this.TempRoot);

        Assert.Equal(LockFileStatus.LockFileNoEnforcement, status);
    }

    [Fact]
    public void CheckLockFile_LockedModeSetButNoBuildTargets_ReturnsLockedEnforcedNoBuildTarget()
    {
        // RestoreLockedMode=true is configured, but no Directory.Build.targets exists.
        string projDir = Path.Combine(this.TempRoot, "MyProject");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "packages.lock.json"), "{}");
        File.WriteAllText(Path.Combine(projDir, "MyProject.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <RestoreLockedMode>true</RestoreLockedMode>
              </PropertyGroup>
            </Project>
            """);

        var status = this.Sut.CheckLockFile(this.TempRoot);

        Assert.Equal(LockFileStatus.LockedEnforcedNoBuildTarget, status);
    }

    [Fact]
    public void CheckLockFile_LockedModeSetWithBuildTargetsButNoNugetAudit_ReturnsLockedEnforcedNoBuildTarget()
    {
        // Directory.Build.targets exists but does not invoke nuget-audit.
        string projDir = Path.Combine(this.TempRoot, "MyProject");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(projDir, "packages.lock.json"), "{}");
        File.WriteAllText(Path.Combine(projDir, "MyProject.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <RestoreLockedMode>true</RestoreLockedMode>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(this.TempRoot, "Directory.Build.targets"), """
            <Project>
              <Target Name="SomeOtherTarget" BeforeTargets="Build">
                <Exec Command="echo hello" />
              </Target>
            </Project>
            """);

        var status = this.Sut.CheckLockFile(this.TempRoot);

        Assert.Equal(LockFileStatus.LockedEnforcedNoBuildTarget, status);
    }

    #endregion

    #region GetExecutableContent

    [Fact]
    public void GetExecutableContent_PackageNotInCache_ReturnsNull()
    {
        var result = this.Sut.GetExecutableContent("SomePackage", "1.0.0", this.TempRoot);

        Assert.Null(result);
    }

    [Fact]
    public void GetExecutableContent_NoBuildOrAnalyzersOrTools_ReturnsEmptyArray()
    {
        // Package directory exists but has only a lib/ folder.
        string pkgDir = Path.Combine(this.TempRoot, "somepackage", "1.0.0");
        Directory.CreateDirectory(Path.Combine(pkgDir, "lib", "net10.0"));

        var result = this.Sut.GetExecutableContent("SomePackage", "1.0.0", this.TempRoot);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void GetExecutableContent_BuildFolderWithTargetsFile_ReturnsMSBld()
    {
        string pkgDir = Path.Combine(this.TempRoot, "somepackage", "1.0.0");
        string buildDir = Path.Combine(pkgDir, "build");
        Directory.CreateDirectory(buildDir);
        File.WriteAllText(Path.Combine(buildDir, "SomePackage.targets"), "<Project />");

        var result = this.Sut.GetExecutableContent("SomePackage", "1.0.0", this.TempRoot);

        Assert.NotNull(result);
        Assert.Contains("MSBld", result);
    }

    [Fact]
    public void GetExecutableContent_BuildTransitiveFolderWithPropsFile_ReturnsMSBld()
    {
        string pkgDir = Path.Combine(this.TempRoot, "somepackage", "1.0.0");
        string buildTransitiveDir = Path.Combine(pkgDir, "buildTransitive");
        Directory.CreateDirectory(buildTransitiveDir);
        File.WriteAllText(Path.Combine(buildTransitiveDir, "SomePackage.props"), "<Project />");

        var result = this.Sut.GetExecutableContent("SomePackage", "1.0.0", this.TempRoot);

        Assert.NotNull(result);
        Assert.Contains("MSBld", result);
    }

    [Fact]
    public void GetExecutableContent_AnalyzersFolder_ReturnsAlyzr()
    {
        string pkgDir = Path.Combine(this.TempRoot, "somepackage", "1.0.0");
        Directory.CreateDirectory(Path.Combine(pkgDir, "analyzers", "dotnet"));

        var result = this.Sut.GetExecutableContent("SomePackage", "1.0.0", this.TempRoot);

        Assert.NotNull(result);
        Assert.Contains("Alyzr", result);
    }

    [Fact]
    public void GetExecutableContent_ToolsFolder_ReturnsTools()
    {
        string pkgDir = Path.Combine(this.TempRoot, "somepackage", "1.0.0");
        Directory.CreateDirectory(Path.Combine(pkgDir, "tools"));

        var result = this.Sut.GetExecutableContent("SomePackage", "1.0.0", this.TempRoot);

        Assert.NotNull(result);
        Assert.Contains("Tools", result);
    }

    [Fact]
    public void GetExecutableContent_AllThreePresent_ReturnsAllLabels()
    {
        string pkgDir = Path.Combine(this.TempRoot, "somepackage", "1.0.0");
        string buildDir = Path.Combine(pkgDir, "build");
        Directory.CreateDirectory(buildDir);
        File.WriteAllText(Path.Combine(buildDir, "SomePackage.targets"), "<Project />");
        Directory.CreateDirectory(Path.Combine(pkgDir, "analyzers"));
        Directory.CreateDirectory(Path.Combine(pkgDir, "tools"));

        var result = this.Sut.GetExecutableContent("SomePackage", "1.0.0", this.TempRoot);

        Assert.NotNull(result);
        Assert.Contains("MSBld", result);
        Assert.Contains("Alyzr", result);
        Assert.Contains("Tools", result);
    }

    [Fact]
    public void GetExecutableContent_PackageIdIsLowercased_FindsDirectory()
    {
        // Cache stores dirs in lowercase; ID passed in all-caps should still find it.
        string pkgDir = Path.Combine(this.TempRoot, "somepackage", "1.0.0");
        Directory.CreateDirectory(Path.Combine(pkgDir, "tools"));

        // Pass "SOMEPACKAGE" (all caps) — service should lowercase to "somepackage".
        var result = this.Sut.GetExecutableContent("SOMEPACKAGE", "1.0.0", this.TempRoot);

        Assert.NotNull(result);
        Assert.Contains("Tools", result);
    }

    [Fact]
    public void GetExecutableContent_VersionCaseIsPreserved_FindsDirectory()
    {
        // Version is NOT lowercased in the cache path. Use an uppercase version to verify.
        string pkgDir = Path.Combine(this.TempRoot, "somepackage", "1.0.0-Beta1");
        Directory.CreateDirectory(Path.Combine(pkgDir, "tools"));

        var result = this.Sut.GetExecutableContent("somepackage", "1.0.0-Beta1", this.TempRoot);

        Assert.NotNull(result);
        Assert.Contains("Tools", result);
    }

    [Fact]
    public void GetExecutableContent_BuildFolderExistsButNoTargetsOrProps_DoesNotReturnMSBld()
    {
        // build/ folder exists but only has a dll — should not trigger MSBld.
        string pkgDir = Path.Combine(this.TempRoot, "somepackage", "1.0.0");
        string buildDir = Path.Combine(pkgDir, "build");
        Directory.CreateDirectory(buildDir);
        File.WriteAllText(Path.Combine(buildDir, "SomePackage.dll"), string.Empty);

        var result = this.Sut.GetExecutableContent("SomePackage", "1.0.0", this.TempRoot);

        Assert.NotNull(result);
        Assert.DoesNotContain("MSBld", result);
    }

    #endregion
}
