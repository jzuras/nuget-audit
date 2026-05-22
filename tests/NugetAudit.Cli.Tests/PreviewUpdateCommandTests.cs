using NugetAudit.Cli.Commands;
using NugetAudit.Core.Models;

namespace NugetAudit.Cli.Tests;

/// <summary>
/// Unit tests for <see cref="PreviewUpdateCommand"/>.
/// </summary>
public class PreviewUpdateCommandTests
{
    #region IsHighSeverityTransition — red alert statuses

    /// <summary>
    /// Untrusted packages (no prefix reservation) trigger a red supply chain alert.
    /// </summary>
    [Fact]
    public void IsHighSeverityTransition_Untrusted_ReturnsTrue()
    {
        bool result = PreviewUpdateCommand.IsHighSeverityTransition(TrustStatus.Untrusted);

        Assert.True(result);
    }

    /// <summary>
    /// VerifiedUnknownOwner packages (prefix-reserved but not in user's trusted list) trigger a red alert.
    /// </summary>
    [Fact]
    public void IsHighSeverityTransition_VerifiedUnknownOwner_ReturnsTrue()
    {
        bool result = PreviewUpdateCommand.IsHighSeverityTransition(TrustStatus.VerifiedUnknownOwner);

        Assert.True(result);
    }

    #endregion

    #region IsHighSeverityTransition — yellow notice statuses

    /// <summary>
    /// Verified packages (prefix-reserved, trusted owner) produce a yellow informational notice, not a red alert.
    /// </summary>
    [Fact]
    public void IsHighSeverityTransition_Verified_ReturnsFalse()
    {
        bool result = PreviewUpdateCommand.IsHighSeverityTransition(TrustStatus.Verified);

        Assert.False(result);
    }

    /// <summary>
    /// TrustedPackage status produces a yellow informational notice, not a red alert.
    /// </summary>
    [Fact]
    public void IsHighSeverityTransition_TrustedPackage_ReturnsFalse()
    {
        bool result = PreviewUpdateCommand.IsHighSeverityTransition(TrustStatus.TrustedPackage);

        Assert.False(result);
    }

    /// <summary>
    /// VersionChanged status produces a yellow informational notice, not a red alert.
    /// </summary>
    [Fact]
    public void IsHighSeverityTransition_VersionChanged_ReturnsFalse()
    {
        bool result = PreviewUpdateCommand.IsHighSeverityTransition(TrustStatus.VersionChanged);

        Assert.False(result);
    }

    /// <summary>
    /// PrivateFeed is an edge case: if somehow flagged as a transition target with PrivateFeed status,
    /// it should not trigger a red alert.
    /// </summary>
    [Fact]
    public void IsHighSeverityTransition_PrivateFeed_ReturnsFalse()
    {
        bool result = PreviewUpdateCommand.IsHighSeverityTransition(TrustStatus.PrivateFeed);

        Assert.False(result);
    }

    #endregion
}
