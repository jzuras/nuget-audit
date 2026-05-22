using NugetAudit.Core.Models;
using NugetAudit.Core.Trust;

namespace NugetAudit.Core.Tests;

/// <summary>
/// Unit tests for <see cref="TrustEvaluator"/>, covering all six trust status outcomes.
/// </summary>
public class TrustEvaluatorTests
{
    #region Helpers

    /// <summary>
    /// Creates a minimal <see cref="TrustConfig"/> for test scenarios.
    /// </summary>
    /// <param name="trustedOwners">Trusted owner names; defaults to ["Microsoft"].</param>
    /// <param name="trustedPackages">Explicitly trusted packages; defaults to empty.</param>
    /// <returns>A configured <see cref="TrustConfig"/> instance.</returns>
    private static TrustConfig MakeConfig(
        string[]? trustedOwners = null,
        TrustedPackageEntry[]? trustedPackages = null)
    {
        return new TrustConfig(
            trustedOwners ?? ["Microsoft"],
            trustedPackages ?? [],
            14);
    }

    /// <summary>
    /// Creates a minimal <see cref="PackageRegistrationData"/> for test scenarios.
    /// </summary>
    /// <param name="packageId">The package identifier; defaults to "TestPackage".</param>
    /// <param name="version">The package version; defaults to "1.0.0".</param>
    /// <returns>A minimal <see cref="PackageRegistrationData"/> instance.</returns>
    private static PackageRegistrationData MakeData(
        string packageId = "TestPackage",
        string version = "1.0.0")
    {
        return new PackageRegistrationData(
            packageId, version, null, null, null, null, null, null, false, false, false);
    }

    #endregion

    #region Verified

    /// <summary>
    /// Verified publisher with an owner in the TrustedOwners list returns Verified.
    /// </summary>
    [Fact]
    public void Evaluate_VerifiedPackageWithTrustedOwner_ReturnsVerified()
    {
        var evaluator = new TrustEvaluator();
        var config = MakeConfig(trustedOwners: ["Microsoft"]);
        var data = MakeData();
        var search = new SearchResult(Verified: true, Owners: ["Microsoft"], TotalDownloads: 1_000_000);

        var result = evaluator.Evaluate(data, search, config);

        Assert.Equal(TrustStatus.Verified, result);
    }

    /// <summary>
    /// Owner matching is case-insensitive.
    /// </summary>
    [Fact]
    public void Evaluate_OwnerMatchIsCaseInsensitive_ReturnsVerified()
    {
        var evaluator = new TrustEvaluator();
        var config = MakeConfig(trustedOwners: ["microsoft"]);
        var data = MakeData();
        var search = new SearchResult(Verified: true, Owners: ["Microsoft"], TotalDownloads: 100);

        var result = evaluator.Evaluate(data, search, config);

        Assert.Equal(TrustStatus.Verified, result);
    }

    /// <summary>
    /// Verified publisher whose owner is NOT in TrustedOwners returns VerifiedUnknownOwner.
    /// </summary>
    [Fact]
    public void Evaluate_VerifiedPackageWithUnknownOwner_ReturnsVerifiedUnknownOwner()
    {
        var evaluator = new TrustEvaluator();
        var config = MakeConfig(trustedOwners: ["Microsoft"]);
        var data = MakeData();
        var search = new SearchResult(Verified: true, Owners: ["UnknownOrg"], TotalDownloads: 100);

        var result = evaluator.Evaluate(data, search, config);

        Assert.Equal(TrustStatus.VerifiedUnknownOwner, result);
    }

    #endregion

    #region VerifiedUnknownOwner + TrustedPackages

    /// <summary>
    /// Verified package with unknown owner that is explicitly in TrustedPackages (exact version) returns TrustedPackage.
    /// </summary>
    [Fact]
    public void Evaluate_VerifiedUnknownOwner_PackageInTrustedList_ReturnsTrustedPackage()
    {
        var evaluator = new TrustEvaluator();
        var config = MakeConfig(
            trustedOwners: ["Microsoft"],
            trustedPackages: [new TrustedPackageEntry("TestPackage", "1.0.0")]);
        var data = MakeData();
        var search = new SearchResult(Verified: true, Owners: ["UnknownOrg"], TotalDownloads: 100);

        var result = evaluator.Evaluate(data, search, config);

        Assert.Equal(TrustStatus.TrustedPackage, result);
    }

    /// <summary>
    /// Verified package with unknown owner whose ID is in TrustedPackages but version differs returns VersionChanged.
    /// </summary>
    [Fact]
    public void Evaluate_VerifiedUnknownOwner_PackageIdInListDifferentVersion_ReturnsVersionChanged()
    {
        var evaluator = new TrustEvaluator();
        var config = MakeConfig(
            trustedOwners: ["Microsoft"],
            trustedPackages: [new TrustedPackageEntry("TestPackage", "1.0.0")]);
        var data = MakeData(version: "2.0.0");
        var search = new SearchResult(Verified: true, Owners: ["UnknownOrg"], TotalDownloads: 100);

        var result = evaluator.Evaluate(data, search, config);

        Assert.Equal(TrustStatus.VersionChanged, result);
    }

    #endregion

    #region TrustedPackage / VersionChanged / Untrusted

    /// <summary>
    /// When the search result is null and the package ID+version is in TrustedPackages, returns TrustedPackage.
    /// </summary>
    [Fact]
    public void Evaluate_NullSearch_PackageInTrustedList_ReturnsTrustedPackage()
    {
        var evaluator = new TrustEvaluator();
        var config = MakeConfig(
            trustedPackages: [new TrustedPackageEntry("TestPackage", "1.0.0")]);
        var data = MakeData();

        var result = evaluator.Evaluate(data, null, config);

        Assert.Equal(TrustStatus.TrustedPackage, result);
    }

    /// <summary>
    /// Package ID matching is case-insensitive.
    /// </summary>
    [Fact]
    public void Evaluate_NullSearch_PackageIdMatchIsCaseInsensitive_ReturnsTrustedPackage()
    {
        var evaluator = new TrustEvaluator();
        var config = MakeConfig(
            trustedPackages: [new TrustedPackageEntry("TESTPACKAGE", "1.0.0")]);
        var data = MakeData(packageId: "TestPackage", version: "1.0.0");

        var result = evaluator.Evaluate(data, null, config);

        Assert.Equal(TrustStatus.TrustedPackage, result);
    }

    /// <summary>
    /// When the package ID is in TrustedPackages but the version differs, returns VersionChanged.
    /// </summary>
    [Fact]
    public void Evaluate_NullSearch_PackageIdInListDifferentVersion_ReturnsVersionChanged()
    {
        var evaluator = new TrustEvaluator();
        var config = MakeConfig(
            trustedPackages: [new TrustedPackageEntry("TestPackage", "1.0.0")]);
        var data = MakeData(version: "2.0.0");

        var result = evaluator.Evaluate(data, null, config);

        Assert.Equal(TrustStatus.VersionChanged, result);
    }

    /// <summary>
    /// When the search result is null and the package is not in TrustedPackages, returns Untrusted.
    /// </summary>
    [Fact]
    public void Evaluate_NullSearch_PackageNotInTrustedList_ReturnsUntrusted()
    {
        var evaluator = new TrustEvaluator();
        var config = MakeConfig();
        var data = MakeData();

        var result = evaluator.Evaluate(data, null, config);

        Assert.Equal(TrustStatus.Untrusted, result);
    }

    /// <summary>
    /// An unverified package not in TrustedPackages returns Untrusted.
    /// </summary>
    [Fact]
    public void Evaluate_NotVerifiedPackageNotInTrustedList_ReturnsUntrusted()
    {
        var evaluator = new TrustEvaluator();
        var config = MakeConfig();
        var data = MakeData();
        var search = new SearchResult(Verified: false, Owners: ["SomeOrg"], TotalDownloads: 100);

        var result = evaluator.Evaluate(data, search, config);

        Assert.Equal(TrustStatus.Untrusted, result);
    }

    /// <summary>
    /// An unverified package that is in TrustedPackages returns TrustedPackage.
    /// </summary>
    [Fact]
    public void Evaluate_NotVerifiedPackageInTrustedList_ReturnsTrustedPackage()
    {
        var evaluator = new TrustEvaluator();
        var config = MakeConfig(
            trustedPackages: [new TrustedPackageEntry("TestPackage", "1.0.0")]);
        var data = MakeData();
        var search = new SearchResult(Verified: false, Owners: ["SomeOrg"], TotalDownloads: 100);

        var result = evaluator.Evaluate(data, search, config);

        Assert.Equal(TrustStatus.TrustedPackage, result);
    }

    #endregion
}
