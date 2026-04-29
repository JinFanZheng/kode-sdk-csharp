using FluentAssertions;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using Xunit;

namespace KodaClaw.UnitTests.Runtime;

/// <summary>
/// KC-1601: Channel and Automation sessions must never have mid-turn tool approval gates.
/// RequireApprovalTools must be empty to prevent the agent turn from pausing on tool calls.
/// </summary>
public sealed class ChannelSessionOptionsTests
{
    [Fact]
    public void ChannelSessionOptions_permissions_mode_should_be_auto()
    {
        var options = new ChannelSessionOptions();

        options.Permissions.Mode.Should().Be("auto");
    }

    [Fact]
    public void ChannelSessionOptions_require_approval_tools_should_be_empty()
    {
        var options = new ChannelSessionOptions();

        options.Permissions.RequireApprovalTools.Should().BeEmpty(
            "channel sessions must not pause mid-turn for tool approval; delivery approval is the only gate");
    }

    [Fact]
    public void AutomationSessionOptions_permissions_mode_should_be_auto()
    {
        var options = new AutomationSessionOptions();

        options.Permissions.Mode.Should().Be("auto");
    }

    [Fact]
    public void AutomationSessionOptions_require_approval_tools_should_be_empty()
    {
        var options = new AutomationSessionOptions();

        options.Permissions.RequireApprovalTools.Should().BeEmpty(
            "automation sessions run headless and must not pause mid-turn for tool approval");
    }
}
