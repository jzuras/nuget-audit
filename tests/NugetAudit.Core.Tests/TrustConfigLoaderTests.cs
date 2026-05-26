using NugetAudit.Core.Configuration;
using NugetAudit.Core.Models;

namespace NugetAudit.Core.Tests;

/// <summary>
/// Unit tests for <see cref="TrustConfigLoader"/>.
/// </summary>
public sealed class TrustConfigLoaderTests : IDisposable
{
    #region Setup / Teardown

    /// <summary>
    /// Gets the temporary directory path used by each test. Cleaned up in Dispose.
    /// </summary>
    private string TempDir { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="TrustConfigLoaderTests"/> class.
    /// Creates a unique temp directory per test.
    /// </summary>
    public TrustConfigLoaderTests()
    {
        this.TempDir = Path.Combine(Path.GetTempPath(), $"nuget-audit-tests-{Guid.NewGuid():N}");
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

    #region Load

    /// <summary>
    /// Loads a valid TrustConfig.json and verifies all fields are populated.
    /// </summary>
    [Fact]
    public void Load_ValidJson_ReturnsCorrectConfig()
    {
        string json = """
            {
              "trustedOwners": ["Microsoft", "serilog"],
              "trustedPackages": [
                { "id": "Newtonsoft.Json", "version": "13.0.3" }
              ],
              "recentDaysThreshold": 7
            }
            """;

        string path = this.WriteTempConfig(json);
        var loader = new TrustConfigLoader();

        TrustConfig config = loader.Load(path);

        Assert.Equal(["Microsoft", "serilog"], config.TrustedOwners);
        Assert.Single(config.TrustedPackages);
        Assert.Equal("Newtonsoft.Json", config.TrustedPackages[0].Id);
        Assert.Equal("13.0.3", config.TrustedPackages[0].Version);
        Assert.Equal(7, config.RecentDaysThreshold);
    }

    /// <summary>
    /// Throws FileNotFoundException when the file does not exist.
    /// </summary>
    [Fact]
    public void Load_FileNotFound_ThrowsFileNotFoundException()
    {
        string missingPath = Path.Combine(this.TempDir, "DoesNotExist.json");
        var loader = new TrustConfigLoader();

        Assert.Throws<FileNotFoundException>(() => loader.Load(missingPath));
    }

    /// <summary>
    /// An empty trustedPackages array is deserialized without error.
    /// </summary>
    [Fact]
    public void Load_EmptyTrustedPackages_ReturnsEmptyArray()
    {
        string json = """
            {
              "trustedOwners": ["Microsoft"],
              "trustedPackages": [],
              "recentDaysThreshold": 14
            }
            """;

        string path = this.WriteTempConfig(json);
        var loader = new TrustConfigLoader();

        TrustConfig config = loader.Load(path);

        Assert.Empty(config.TrustedPackages);
    }

    #endregion

    #region LoadOrDefault

    /// <summary>
    /// Returns an empty config and FileFound=false when the file does not exist.
    /// </summary>
    [Fact]
    public void LoadOrDefault_FileNotFound_ReturnsEmptyConfigAndFalse()
    {
        string missingPath = Path.Combine(this.TempDir, "DoesNotExist.json");
        var loader = new TrustConfigLoader();

        var (config, fileFound) = loader.LoadOrDefault(missingPath);

        Assert.False(fileFound);
        Assert.Empty(config.TrustedOwners);
        Assert.Empty(config.TrustedPackages);
        Assert.Equal(14, config.RecentDaysThreshold);
    }

    /// <summary>
    /// Returns the file contents and FileFound=true when the file exists.
    /// </summary>
    [Fact]
    public void LoadOrDefault_FileExists_ReturnsFileContentsAndTrue()
    {
        string json = """
            {
              "trustedOwners": ["CustomOrg"],
              "trustedPackages": [],
              "recentDaysThreshold": 30
            }
            """;

        string path = this.WriteTempConfig(json);
        var loader = new TrustConfigLoader();

        var (config, fileFound) = loader.LoadOrDefault(path);

        Assert.True(fileFound);
        Assert.Equal(["CustomOrg"], config.TrustedOwners);
        Assert.Equal(30, config.RecentDaysThreshold);
    }

    #endregion

    #region Round-Trip (Loader + Saver)

    /// <summary>
    /// A config saved via TrustConfigSaver can be loaded back via TrustConfigLoader with identical values.
    /// </summary>
    [Fact]
    public void RoundTrip_SaveThenLoad_PreservesAllFields()
    {
        string path = Path.Combine(this.TempDir, "TrustConfig.json");

        var original = new TrustConfig(
            ["Microsoft", "serilog"],
            [new TrustedPackageEntry("SomePackage", "2.0.0")],
            21);

        var saver = new TrustConfigSaver();
        saver.Save(original, path);

        var loader = new TrustConfigLoader();
        TrustConfig loaded = loader.Load(path);

        Assert.Equal(original.TrustedOwners, loaded.TrustedOwners);
        Assert.Single(loaded.TrustedPackages);
        Assert.Equal("SomePackage", loaded.TrustedPackages[0].Id);
        Assert.Equal("2.0.0", loaded.TrustedPackages[0].Version);
        Assert.Equal(original.RecentDaysThreshold, loaded.RecentDaysThreshold);
    }

    #endregion
}
