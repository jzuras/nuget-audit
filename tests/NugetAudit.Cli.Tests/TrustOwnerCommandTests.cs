using NugetAudit.Cli.Commands;
using NugetAudit.Core.Configuration;

namespace NugetAudit.Cli.Tests;

/// <summary>
/// Unit tests for <see cref="TrustOwnerCommand"/>.
/// </summary>
public class TrustOwnerCommandTests : IDisposable
{
    #region Setup / Teardown

    /// <summary>
    /// Gets the temporary directory path used by each test. Cleaned up in Dispose.
    /// </summary>
    private string TempDir { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="TrustOwnerCommandTests"/> class.
    /// Creates a unique temp directory per test.
    /// </summary>
    public TrustOwnerCommandTests()
    {
        this.TempDir = Path.Combine(Path.GetTempPath(), $"nuget-audit-trust-owner-tests-{Guid.NewGuid():N}");
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

    #region Add new owner

    /// <summary>
    /// A new owner not already in the list is appended and the file is saved.
    /// </summary>
    [Fact]
    public void RunTrustOwner_NewOwner_AddsToConfig()
    {
        this.WriteTempConfig("""
            {
              "trustedOwners": ["Microsoft"],
              "trustedPackages": [],
              "recentDaysThreshold": 14
            }
            """);

        int exitCode = TrustOwnerCommand.RunTrustOwner("serilog", this.TempDir, null);

        Assert.Equal(0, exitCode);

        var config = new TrustConfigLoader().Load(Path.Combine(this.TempDir, "TrustConfig.json"));
        Assert.Equal(["Microsoft", "serilog"], config.TrustedOwners);
    }

    /// <summary>
    /// Existing fields other than trustedOwners are preserved after save.
    /// </summary>
    [Fact]
    public void RunTrustOwner_NewOwner_PreservesOtherFields()
    {
        this.WriteTempConfig("""
            {
              "trustedOwners": ["Microsoft"],
              "trustedPackages": [{ "id": "Foo.Bar", "version": "1.0.0" }],
              "recentDaysThreshold": 7
            }
            """);

        TrustOwnerCommand.RunTrustOwner("serilog", this.TempDir, null);

        var config = new TrustConfigLoader().Load(Path.Combine(this.TempDir, "TrustConfig.json"));
        Assert.Single(config.TrustedPackages);
        Assert.Equal("Foo.Bar", config.TrustedPackages[0].Id);
        Assert.Equal(7, config.RecentDaysThreshold);
    }

    #endregion

    #region Duplicate detection

    /// <summary>
    /// An owner already present (exact case) returns exit code 0 and makes no change.
    /// </summary>
    [Fact]
    public void RunTrustOwner_ExactDuplicate_ReturnsZeroWithNoChange()
    {
        string configPath = this.WriteTempConfig("""
            {
              "trustedOwners": ["Microsoft", "serilog"],
              "trustedPackages": [],
              "recentDaysThreshold": 14
            }
            """);

        int exitCode = TrustOwnerCommand.RunTrustOwner("serilog", this.TempDir, null);

        Assert.Equal(0, exitCode);

        var config = new TrustConfigLoader().Load(configPath);
        Assert.Equal(2, config.TrustedOwners.Length);
    }

    /// <summary>
    /// An owner already present with different casing returns exit code 0 and makes no change.
    /// </summary>
    [Fact]
    public void RunTrustOwner_CaseInsensitiveDuplicate_ReturnsZeroWithNoChange()
    {
        string configPath = this.WriteTempConfig("""
            {
              "trustedOwners": ["Microsoft"],
              "trustedPackages": [],
              "recentDaysThreshold": 14
            }
            """);

        int exitCode = TrustOwnerCommand.RunTrustOwner("microsoft", this.TempDir, null);

        Assert.Equal(0, exitCode);

        var config = new TrustConfigLoader().Load(configPath);
        Assert.Single(config.TrustedOwners);
    }

    #endregion

    #region Error handling

    /// <summary>
    /// Returns exit code 1 when TrustConfig.json does not exist.
    /// </summary>
    [Fact]
    public void RunTrustOwner_FileNotFound_ReturnsOne()
    {
        int exitCode = TrustOwnerCommand.RunTrustOwner("serilog", this.TempDir, null);

        Assert.Equal(1, exitCode);
    }

    /// <summary>
    /// An explicit --trust-config path pointing to a valid file is used instead of deriving from --path.
    /// </summary>
    [Fact]
    public void RunTrustOwner_ExplicitTrustConfigPath_UsesSpecifiedFile()
    {
        string subDir = Path.Combine(this.TempDir, "sub");
        Directory.CreateDirectory(subDir);

        string configPath = Path.Combine(subDir, "TrustConfig.json");
        File.WriteAllText(configPath, """
            {
              "trustedOwners": ["Microsoft"],
              "trustedPackages": [],
              "recentDaysThreshold": 14
            }
            """);

        int exitCode = TrustOwnerCommand.RunTrustOwner("serilog", this.TempDir, subDir);

        Assert.Equal(0, exitCode);

        var config = new TrustConfigLoader().Load(configPath);
        Assert.Equal(["Microsoft", "serilog"], config.TrustedOwners);
    }

    #endregion
}
