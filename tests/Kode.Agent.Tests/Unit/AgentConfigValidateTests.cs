using FluentAssertions;
using Kode.Agent.Sdk.Core.Types;
using Xunit;

namespace Kode.Agent.Tests.Unit;

public sealed class AgentConfigValidateTests
{
    [Fact]
    public void Validate_AcceptsDefaults()
    {
        var act = () => new AgentConfig().Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_AcceptsTypicalRealWorldConfig()
    {
        var cfg = new AgentConfig
        {
            Model = "claude-sonnet-4-6",
            MaxIterations = 20,
            MaxTokens = 4096,
            Temperature = 0.7,
            ThinkingBudget = 2048,
            MaxToolConcurrency = 4,
            ToolTimeout = TimeSpan.FromMinutes(2),
            SessionType = "channel",
            AgentRole = "sub-agent"
        };
        var act = () => cfg.Validate();
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_RejectsNonPositiveMaxIterations(int value)
    {
        var cfg = new AgentConfig { MaxIterations = value };
        cfg.Invoking(c => c.Validate())
            .Should().Throw<ArgumentOutOfRangeException>()
            .And.ParamName.Should().Be(nameof(AgentConfig.MaxIterations));
    }

    [Fact]
    public void Validate_RejectsNonPositiveMaxTokens()
    {
        var cfg = new AgentConfig { MaxTokens = 0 };
        cfg.Invoking(c => c.Validate())
            .Should().Throw<ArgumentOutOfRangeException>()
            .And.ParamName.Should().Be(nameof(AgentConfig.MaxTokens));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(2.1)]
    public void Validate_RejectsTemperatureOutOfRange(double value)
    {
        var cfg = new AgentConfig { Temperature = value };
        cfg.Invoking(c => c.Validate())
            .Should().Throw<ArgumentOutOfRangeException>()
            .And.ParamName.Should().Be(nameof(AgentConfig.Temperature));
    }

    [Fact]
    public void Validate_RejectsNonPositiveThinkingBudget()
    {
        var cfg = new AgentConfig { ThinkingBudget = 0 };
        cfg.Invoking(c => c.Validate())
            .Should().Throw<ArgumentOutOfRangeException>()
            .And.ParamName.Should().Be(nameof(AgentConfig.ThinkingBudget));
    }

    [Fact]
    public void Validate_RejectsNonPositiveMaxToolConcurrency()
    {
        var cfg = new AgentConfig { MaxToolConcurrency = 0 };
        cfg.Invoking(c => c.Validate())
            .Should().Throw<ArgumentOutOfRangeException>()
            .And.ParamName.Should().Be(nameof(AgentConfig.MaxToolConcurrency));
    }

    [Fact]
    public void Validate_RejectsNonPositiveToolTimeout()
    {
        var cfg = new AgentConfig { ToolTimeout = TimeSpan.Zero };
        cfg.Invoking(c => c.Validate())
            .Should().Throw<ArgumentOutOfRangeException>()
            .And.ParamName.Should().Be(nameof(AgentConfig.ToolTimeout));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("channel")]
    [InlineData("automation")]
    [InlineData("cli")]
    [InlineData("api")]
    [InlineData("anything-goes")]
    public void Validate_AcceptsAnySessionType(string value)
    {
        new AgentConfig { SessionType = value }
            .Invoking(c => c.Validate()).Should().NotThrow();
    }

    [Theory]
    [InlineData("primary")]
    [InlineData("sub-agent")]
    [InlineData("reviewer")]
    [InlineData("worker")]
    public void Validate_AcceptsAnyAgentRole(string value)
    {
        new AgentConfig { AgentRole = value }
            .Invoking(c => c.Validate()).Should().NotThrow();
    }
}
