using NugetAudit.Core.DependencyGraph;
using NugetAudit.Core.Models;
using NugetAudit.Core.Services;

namespace NugetAudit.Core.Tests;

/// <summary>
/// Unit tests for <see cref="VersionRangeResolver"/>, covering exact versions, non-exact ranges,
/// wildcard/empty input, fallback behavior, and unparseable expressions.
/// </summary>
public class VersionRangeResolverTests
{
    #region Helpers

    /// <summary>
    /// Creates a stub <see cref="INuGetSearchClient"/> that returns a fixed latest version.
    /// </summary>
    /// <param name="latestVersion">The version string to return, or null to simulate not found.</param>
    /// <returns>A stub search client instance.</returns>
#pragma warning disable CA1859 // StubSearchClient is file-local; cannot appear in method signature
    private static INuGetSearchClient StubSearch(string? latestVersion)
        => new StubSearchClient(latestVersion);
#pragma warning restore CA1859

    private static VersionRangeResolver MakeResolver(string? latestVersion)
        => new(StubSearch(latestVersion));

    #endregion

    #region Exact version

    /// <summary>
    /// An exact bracket notation version [1.0.0] is returned as-is and not flagged as approximate.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_ExactBracketNotation_ReturnsExactVersionNotApproximate()
    {
        var resolver = MakeResolver("9.9.9");

        var result = await resolver.ResolveAsync("TestPackage", "[1.0.0]", CancellationToken.None);

        Assert.Equal("1.0.0", result.Version);
        Assert.False(result.IsApproximate);
    }

    /// <summary>
    /// Exact version matching is case-insensitive on the normalized form.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_ExactBracketNotation_VersionIsNormalized()
    {
        var resolver = MakeResolver(null);

        var result = await resolver.ResolveAsync("TestPackage", "[1.0.0.0]", CancellationToken.None);

        // NuGet normalizes 1.0.0.0 → 1.0.0
        Assert.Equal("1.0.0", result.Version);
        Assert.False(result.IsApproximate);
    }

    #endregion

    #region Wildcard / empty

    /// <summary>
    /// An empty range expression resolves to latest stable and is flagged as approximate.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_EmptyRange_ReturnsLatestApproximate()
    {
        var resolver = MakeResolver("5.0.0");

        var result = await resolver.ResolveAsync("TestPackage", "", CancellationToken.None);

        Assert.Equal("5.0.0", result.Version);
        Assert.True(result.IsApproximate);
    }

    /// <summary>
    /// A whitespace-only range expression resolves to latest stable and is flagged as approximate.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_WhitespaceRange_ReturnsLatestApproximate()
    {
        var resolver = MakeResolver("5.0.0");

        var result = await resolver.ResolveAsync("TestPackage", "   ", CancellationToken.None);

        Assert.Equal("5.0.0", result.Version);
        Assert.True(result.IsApproximate);
    }

    /// <summary>
    /// A wildcard (*) range expression resolves to latest stable and is flagged as approximate.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_WildcardRange_ReturnsLatestApproximate()
    {
        var resolver = MakeResolver("5.0.0");

        var result = await resolver.ResolveAsync("TestPackage", "*", CancellationToken.None);

        Assert.Equal("5.0.0", result.Version);
        Assert.True(result.IsApproximate);
    }

    /// <summary>
    /// When latest is not found for an empty range, returns "0.0.0" as approximate.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_EmptyRange_LatestNotFound_ReturnsFallbackVersion()
    {
        var resolver = MakeResolver(null);

        var result = await resolver.ResolveAsync("TestPackage", "", CancellationToken.None);

        Assert.Equal("0.0.0", result.Version);
        Assert.True(result.IsApproximate);
    }

    #endregion

    #region Non-exact ranges

    /// <summary>
    /// A minimum-inclusive range [2.0.0, ) where latest satisfies it returns latest as approximate.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_MinInclusiveRange_LatestSatisfies_ReturnsLatestApproximate()
    {
        var resolver = MakeResolver("3.0.0");

        var result = await resolver.ResolveAsync("TestPackage", "[2.0.0, )", CancellationToken.None);

        Assert.Equal("3.0.0", result.Version);
        Assert.True(result.IsApproximate);
    }

    /// <summary>
    /// A minimum-inclusive range where latest does NOT satisfy falls back to the minimum version.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_MinInclusiveRange_LatestDoesNotSatisfy_ReturnsFallbackToMin()
    {
        // Latest is 1.0.0 but range requires >= 2.0.0 — latest doesn't satisfy.
        var resolver = MakeResolver("1.0.0");

        var result = await resolver.ResolveAsync("TestPackage", "[2.0.0, )", CancellationToken.None);

        Assert.Equal("2.0.0", result.Version);
        Assert.True(result.IsApproximate);
    }

    /// <summary>
    /// A bounded range [1.0.0, 3.0.0) where latest satisfies returns latest as approximate.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_BoundedRange_LatestSatisfies_ReturnsLatestApproximate()
    {
        var resolver = MakeResolver("2.5.0");

        var result = await resolver.ResolveAsync("TestPackage", "[1.0.0, 3.0.0)", CancellationToken.None);

        Assert.Equal("2.5.0", result.Version);
        Assert.True(result.IsApproximate);
    }

    /// <summary>
    /// A bounded range where latest is not found falls back to the minimum version.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_BoundedRange_LatestNotFound_ReturnsFallbackToMin()
    {
        var resolver = MakeResolver(null);

        var result = await resolver.ResolveAsync("TestPackage", "[1.0.0, 3.0.0)", CancellationToken.None);

        Assert.Equal("1.0.0", result.Version);
        Assert.True(result.IsApproximate);
    }

    #endregion

    #region Fallback / unparseable

    /// <summary>
    /// An unparseable range expression falls back to the regex-stripped version string.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_UnparseableRange_ReturnsStrippedFallback()
    {
        var resolver = MakeResolver(null);

        // Completely invalid — NuGet.Versioning will fail to parse this.
        var result = await resolver.ResolveAsync("TestPackage", "garbage!!!", CancellationToken.None);

        Assert.True(result.IsApproximate);
        Assert.False(string.IsNullOrWhiteSpace(result.Version));
    }

    #endregion
}

/// <summary>
/// Minimal stub for <see cref="INuGetSearchClient"/> that returns a fixed latest version.
/// </summary>
file sealed class StubSearchClient(string? latestVersion) : INuGetSearchClient
{
    public Task<SearchResult?> SearchPackageAsync(string packageId, CancellationToken ct)
        => Task.FromResult<SearchResult?>(null);

    public Task<string?> GetLatestVersionAsync(string packageId, CancellationToken ct)
        => Task.FromResult(latestVersion);
}
