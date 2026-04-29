using FluentAssertions;
using KodaClaw.Automation;
using KodaClaw.Automation.Scheduler;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using Xunit;

namespace KodaClaw.UnitTests.Automation;

public sealed class AutoDreamPostProcessingTests
{
    [Fact]
    public void IsMemoryConsolidation_TitleContainsMemoryConsolidation_ReturnsTrue()
    {
        var definition = BuildDefinition("Memory Consolidation");

        AutomationScheduler.IsMemoryConsolidation(definition).Should().BeTrue();
    }

    [Fact]
    public void IsMemoryConsolidation_TitleContains_记忆整合_ReturnsTrue()
    {
        var definition = BuildDefinition("每日记忆整合任务");

        AutomationScheduler.IsMemoryConsolidation(definition).Should().BeTrue();
    }

    [Fact]
    public void IsMemoryConsolidation_CaseInsensitive_ReturnsTrue()
    {
        var definition = BuildDefinition("nightly memory consolidation");

        AutomationScheduler.IsMemoryConsolidation(definition).Should().BeTrue();
    }

    [Fact]
    public void IsMemoryConsolidation_UnrelatedTitle_ReturnsFalse()
    {
        var definition = BuildDefinition("Daily Inbox Digest");

        AutomationScheduler.IsMemoryConsolidation(definition).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsMemoryConsolidation_NullOrEmptyTitle_ReturnsFalse(string? title)
    {
        var definition = BuildDefinition(title!);

        AutomationScheduler.IsMemoryConsolidation(definition).Should().BeFalse();
    }

    [Fact]
    public void IsMemoryConsolidation_DailyInboxDigest_ReturnsFalse()
    {
        var definition = BuildDefinition("Daily Inbox Digest");

        AutomationScheduler.IsMemoryConsolidation(definition).Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static AutomationDefinition BuildDefinition(string title)
    {
        var now = new DateTimeOffset(2026, 3, 26, 2, 0, 0, TimeSpan.Zero);
        return new AutomationDefinition(
            Id: "test-auto-dream",
            Title: title,
            Prompt: "Run memory consolidation.",
            Source: AutomationDefinitionSource.Heartbeat,
            SourcePath: null,
            CronExpression: "0 2 * * *",
            Enabled: true,
            InputPaths: null,
            ModelId: null,
            NotificationChannels: null,
            NotifyMode: AutomationNotifyMode.None,
            CreatedAt: now,
            UpdatedAt: now,
            LastRunAt: null,
            NextRunAt: null,
            LastRunStatus: null,
            LastError: null);
    }
}
