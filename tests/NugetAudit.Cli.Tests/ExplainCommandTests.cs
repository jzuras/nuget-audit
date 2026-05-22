using NugetAudit.Cli.Commands;

namespace NugetAudit.Cli.Tests;

/// <summary>
/// Unit tests for <see cref="ExplainCommand"/>.
/// </summary>
public class ExplainCommandTests
{
    #region Known topics — exit 0

    /// <summary>
    /// lock-files topic returns exit code 0.
    /// </summary>
    [Fact]
    public void RunExplain_LockFiles_ReturnsZero()
    {
        int exitCode = ExplainCommand.RunExplain("lock-files");

        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// psm topic returns exit code 0.
    /// </summary>
    [Fact]
    public void RunExplain_Psm_ReturnsZero()
    {
        int exitCode = ExplainCommand.RunExplain("psm");

        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// exec-content topic returns exit code 0.
    /// </summary>
    [Fact]
    public void RunExplain_ExecContent_ReturnsZero()
    {
        int exitCode = ExplainCommand.RunExplain("exec-content");

        Assert.Equal(0, exitCode);
    }

    #endregion

    #region Case insensitivity

    /// <summary>
    /// Topic matching is case-insensitive — uppercase input resolves to the correct handler.
    /// </summary>
    [Fact]
    public void RunExplain_LockFilesUpperCase_ReturnsZero()
    {
        int exitCode = ExplainCommand.RunExplain("LOCK-FILES");

        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// Topic matching is case-insensitive — mixed case input resolves to the correct handler.
    /// </summary>
    [Fact]
    public void RunExplain_PsmMixedCase_ReturnsZero()
    {
        int exitCode = ExplainCommand.RunExplain("PSM");

        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// Topic matching is case-insensitive — mixed case exec-content resolves to the correct handler.
    /// </summary>
    [Fact]
    public void RunExplain_ExecContentMixedCase_ReturnsZero()
    {
        int exitCode = ExplainCommand.RunExplain("Exec-Content");

        Assert.Equal(0, exitCode);
    }

    #endregion

    #region Topic list — exit 0

    /// <summary>
    /// Null topic shows the topic list and returns exit code 0.
    /// </summary>
    [Fact]
    public void RunExplain_NullTopic_ReturnsZero()
    {
        int exitCode = ExplainCommand.RunExplain(null);

        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// Empty string topic shows the topic list and returns exit code 0.
    /// </summary>
    [Fact]
    public void RunExplain_EmptyTopic_ReturnsZero()
    {
        int exitCode = ExplainCommand.RunExplain(string.Empty);

        Assert.Equal(0, exitCode);
    }

    #endregion

    #region Unknown topic — exit 1

    /// <summary>
    /// An unrecognized topic returns exit code 1.
    /// </summary>
    [Fact]
    public void RunExplain_UnknownTopic_ReturnsOne()
    {
        int exitCode = ExplainCommand.RunExplain("not-a-topic");

        Assert.Equal(1, exitCode);
    }

    /// <summary>
    /// A whitespace-only topic is treated as unrecognized and returns exit code 1.
    /// </summary>
    [Fact]
    public void RunExplain_WhitespaceTopic_ReturnsOne()
    {
        int exitCode = ExplainCommand.RunExplain("   ");

        Assert.Equal(1, exitCode);
    }

    #endregion
}
