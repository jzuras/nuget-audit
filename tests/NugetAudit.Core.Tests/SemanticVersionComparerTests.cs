using NugetAudit.Core.Trust;

namespace NugetAudit.Core.Tests;

/// <summary>
/// Unit tests for <see cref="SemanticVersionComparer"/>, covering multi-digit components,
/// pre-release suffixes, and ordinal fallback.
/// </summary>
public class SemanticVersionComparerTests
{
    #region Ordering

    /// <summary>
    /// Multi-digit minor version: 9.0.3 is less than 9.0.10.
    /// This is the primary motivation for the comparer — lexicographic ordering gives the wrong result here.
    /// </summary>
    [Fact]
    public void Compare_MultiDigitMinorVersion_ReturnsCorrectOrder()
    {
        int result = SemanticVersionComparer.Compare("9.0.3", "9.0.10");

        Assert.True(result < 0, "9.0.3 should be less than 9.0.10");
    }

    /// <summary>
    /// Reversed multi-digit minor version: 9.0.10 is greater than 9.0.3.
    /// </summary>
    [Fact]
    public void Compare_MultiDigitMinorVersionReversed_ReturnsCorrectOrder()
    {
        int result = SemanticVersionComparer.Compare("9.0.10", "9.0.3");

        Assert.True(result > 0, "9.0.10 should be greater than 9.0.3");
    }

    /// <summary>
    /// Major version difference: 1.0.0 is less than 2.0.0.
    /// </summary>
    [Fact]
    public void Compare_MajorVersionDifference_ReturnsLessThan()
    {
        int result = SemanticVersionComparer.Compare("1.0.0", "2.0.0");

        Assert.True(result < 0);
    }

    /// <summary>
    /// Major version difference reversed: 2.0.0 is greater than 1.0.0.
    /// </summary>
    [Fact]
    public void Compare_MajorVersionDifferenceReversed_ReturnsGreaterThan()
    {
        int result = SemanticVersionComparer.Compare("2.0.0", "1.0.0");

        Assert.True(result > 0);
    }

    #endregion

    #region Equality

    /// <summary>
    /// Identical version strings return 0.
    /// </summary>
    [Fact]
    public void Compare_IdenticalVersions_ReturnsZero()
    {
        int result = SemanticVersionComparer.Compare("1.2.3", "1.2.3");

        Assert.Equal(0, result);
    }

    #endregion

    #region Pre-release Suffix Stripping

    /// <summary>
    /// Pre-release suffix is stripped before comparison: 1.0.0-alpha vs 1.0.0 compares numerics (1.0.0 == 1.0.0)
    /// but since 1.0.0-alpha strips to 1.0.0, it equals the stable release numerically.
    /// </summary>
    [Fact]
    public void Compare_PreReleaseSuffix_StrippedBeforeComparison()
    {
        // Both strip to 1.0.0 → equal numerically
        int result = SemanticVersionComparer.Compare("1.0.0-alpha", "1.0.0");

        Assert.Equal(0, result);
    }

    /// <summary>
    /// Build metadata suffix (+) is stripped before comparison.
    /// </summary>
    [Fact]
    public void Compare_BuildMetadataSuffix_StrippedBeforeComparison()
    {
        int result = SemanticVersionComparer.Compare("1.0.0+build.1", "1.0.0");

        Assert.Equal(0, result);
    }

    /// <summary>
    /// Two pre-release versions that differ only by label compare equal after stripping (both become 1.0.0).
    /// </summary>
    [Fact]
    public void Compare_TwoPreReleaseVersionsSameBase_ReturnZero()
    {
        int result = SemanticVersionComparer.Compare("1.0.0-alpha", "1.0.0-beta");

        Assert.Equal(0, result);
    }

    #endregion

    #region Range Boundary Checks (used by NuGetRegistrationClient)

    /// <summary>
    /// A version at the lower bound of a range is considered in-range.
    /// </summary>
    [Fact]
    public void Compare_VersionAtLowerBound_IsInRange()
    {
        // version >= lower
        Assert.True(SemanticVersionComparer.Compare("1.0.0", "1.0.0") >= 0);
    }

    /// <summary>
    /// A version at the upper bound of a range is considered in-range.
    /// </summary>
    [Fact]
    public void Compare_VersionAtUpperBound_IsInRange()
    {
        // version <= upper
        Assert.True(SemanticVersionComparer.Compare("2.0.0", "2.0.0") <= 0);
    }

    /// <summary>
    /// A version below the lower bound is out of range.
    /// </summary>
    [Fact]
    public void Compare_VersionBelowLowerBound_IsOutOfRange()
    {
        // version < lower → not in range
        Assert.True(SemanticVersionComparer.Compare("0.9.0", "1.0.0") < 0);
    }

    #endregion
}
