using NugetAudit.Cli.Commands;
using NugetAudit.Core.Configuration;

namespace NugetAudit.Cli.Tests;

/// <summary>
/// Unit tests for <see cref="TrustPackageCommand"/>.
/// </summary>
public sealed class TrustPackageCommandTests : IDisposable
{
    #region Setup / Teardown

    /// <summary>
    /// Gets the temporary directory path used by each test. Cleaned up in Dispose.
    /// </summary>
    private string TempDir { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="TrustPackageCommandTests"/> class.
    /// Creates a unique temp directory per test.
    /// </summary>
    public TrustPackageCommandTests()
    {
        this.TempDir = Path.Combine(Path.GetTempPath(), $"nuget-audit-trust-package-tests-{Guid.NewGuid():N}");
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

    /// <summary>
    /// Writes a TrustConfig.json to the temp directory and returns its path.
    /// </summary>
    /// <param name="json">The JSON content to write.</param>
    /// <returns>The full path to the written file.</returns>
    private string WriteTempConfig(string json)
    {
        string path = Path.Combine(this.TempDir, "TrustConfig.json");
        File.WriteAllText(path, json);

        return path;
    }

    #endregion

    #region Add new package

    /// <summary>
    /// A new package ID+version not already in the list is appended and the file is saved.
    /// </summary>
    [Fact]
    public void RunTrustPackage_NewPackage_AddsToConfig()
    {
        this.WriteTempConfig("""
            {
              "trustedOwners": [],
              "trustedPackages": [],
              "recentDaysThreshold": 14
            }
            """);

        int exitCode = TrustPackageCommand.RunTrustPackage("Foo.Bar", "2.0.0", this.TempDir, null);

        Assert.Equal(0, exitCode);

        var config = new TrustConfigLoader().Load(Path.Combine(this.TempDir, "TrustConfig.json"));
        Assert.Single(config.TrustedPackages);
        Assert.Equal("Foo.Bar", config.TrustedPackages[0].Id);
        Assert.Equal("2.0.0", config.TrustedPackages[0].Version);
    }

    /// <summary>
    /// Existing fields other than trustedPackages are preserved after save.
    /// </summary>
    [Fact]
    public void RunTrustPackage_NewPackage_PreservesOtherFields()
    {
        this.WriteTempConfig("""
            {
              "trustedOwners": ["Microsoft"],
              "trustedPackages": [],
              "recentDaysThreshold": 7
            }
            """);

        TrustPackageCommand.RunTrustPackage("Foo.Bar", "2.0.0", this.TempDir, null);

        var config = new TrustConfigLoader().Load(Path.Combine(this.TempDir, "TrustConfig.json"));
        Assert.Equal(["Microsoft"], config.TrustedOwners);
        Assert.Equal(7, config.RecentDaysThreshold);
    }

    #endregion

    #region Duplicate detection

    /// <summary>
    /// An exact ID+version match returns exit code 0 and makes no change.
    /// </summary>
    [Fact]
    public void RunTrustPackage_ExactMatch_ReturnsZeroWithNoChange()
    {
        string configPath = this.WriteTempConfig("""
            {
              "trustedOwners": [],
              "trustedPackages": [{ "id": "Foo.Bar", "version": "2.0.0" }],
              "recentDaysThreshold": 14
            }
            """);

        int exitCode = TrustPackageCommand.RunTrustPackage("Foo.Bar", "2.0.0", this.TempDir, null);

        Assert.Equal(0, exitCode);

        var config = new TrustConfigLoader().Load(configPath);
        Assert.Single(config.TrustedPackages);
    }

    #endregion

    #region Version update (VersionChanged scenario)

    /// <summary>
    /// When the same package ID exists with a different version, the version is updated in place.
    /// </summary>
    [Fact]
    public void RunTrustPackage_VersionChanged_UpdatesVersionInPlace()
    {
        string configPath = this.WriteTempConfig("""
            {
              "trustedOwners": [],
              "trustedPackages": [{ "id": "Foo.Bar", "version": "1.0.0" }],
              "recentDaysThreshold": 14
            }
            """);

        int exitCode = TrustPackageCommand.RunTrustPackage("Foo.Bar", "2.0.0", this.TempDir, null);

        Assert.Equal(0, exitCode);

        var config = new TrustConfigLoader().Load(configPath);
        Assert.Single(config.TrustedPackages);
        Assert.Equal("Foo.Bar", config.TrustedPackages[0].Id);
        Assert.Equal("2.0.0", config.TrustedPackages[0].Version);
    }

    /// <summary>
    /// Version update is case-insensitive on the package ID — the original casing in the file is preserved.
    /// </summary>
    [Fact]
    public void RunTrustPackage_CaseInsensitiveId_UpdatesVersionInPlace()
    {
        string configPath = this.WriteTempConfig("""
            {
              "trustedOwners": [],
              "trustedPackages": [{ "id": "Foo.Bar", "version": "1.0.0" }],
              "recentDaysThreshold": 14
            }
            """);

        int exitCode = TrustPackageCommand.RunTrustPackage("foo.bar", "2.0.0", this.TempDir, null);

        Assert.Equal(0, exitCode);

        var config = new TrustConfigLoader().Load(configPath);
        Assert.Single(config.TrustedPackages);
        Assert.Equal("Foo.Bar", config.TrustedPackages[0].Id);
        Assert.Equal("2.0.0", config.TrustedPackages[0].Version);
    }

    /// <summary>
    /// Other entries in trustedPackages are not affected when one entry is updated.
    /// </summary>
    [Fact]
    public void RunTrustPackage_VersionChanged_LeavesOtherEntriesUntouched()
    {
        string configPath = this.WriteTempConfig("""
            {
              "trustedOwners": [],
              "trustedPackages": [
                { "id": "Foo.Bar", "version": "1.0.0" },
                { "id": "Other.Pkg", "version": "3.0.0" }
              ],
              "recentDaysThreshold": 14
            }
            """);

        TrustPackageCommand.RunTrustPackage("Foo.Bar", "2.0.0", this.TempDir, null);

        var config = new TrustConfigLoader().Load(configPath);
        Assert.Equal(2, config.TrustedPackages.Length);
        Assert.Equal("Other.Pkg", config.TrustedPackages[1].Id);
        Assert.Equal("3.0.0", config.TrustedPackages[1].Version);
    }

    #endregion

    #region Error handling

    /// <summary>
    /// Returns exit code 1 when TrustConfig.json does not exist.
    /// </summary>
    [Fact]
    public void RunTrustPackage_FileNotFound_ReturnsOne()
    {
        int exitCode = TrustPackageCommand.RunTrustPackage("Foo.Bar", "2.0.0", this.TempDir, null);

        Assert.Equal(1, exitCode);
    }

    #endregion
}
