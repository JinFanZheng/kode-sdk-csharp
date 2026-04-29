using FluentAssertions;
using Kode.Agent.Sdk.Core.Abstractions;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using Xunit;

namespace KodaClaw.UnitTests.Runtime;

public sealed class ChannelSessionStatsTrackerTests
{
    [Fact]
    public void Record_with_null_usage_is_noop()
    {
        var tracker = new ChannelSessionStatsTracker();

        tracker.Record("session-1", usage: null);

        tracker.Get("session-1").Should().BeNull();
    }

    [Fact]
    public void Record_with_empty_session_id_is_noop()
    {
        var tracker = new ChannelSessionStatsTracker();

        tracker.Record(sessionId: "", usage: new TokenUsage { InputTokens = 100, OutputTokens = 50 });

        tracker.Get("").Should().BeNull();
    }

    [Fact]
    public void Record_first_time_seeds_all_fields()
    {
        var tracker = new ChannelSessionStatsTracker();
        var usage = new TokenUsage { InputTokens = 1_000, OutputTokens = 200 };

        tracker.Record("session-1", usage);

        var stats = tracker.Get("session-1");
        stats.Should().NotBeNull();
        stats!.LastRunInputTokens.Should().Be(1_000);
        stats.CumulativeInputTokens.Should().Be(1_000);
        stats.CumulativeOutputTokens.Should().Be(200);
        stats.TurnCount.Should().Be(1);
    }

    [Fact]
    public void Record_subsequent_accumulates_but_replaces_last_run()
    {
        var tracker = new ChannelSessionStatsTracker();
        tracker.Record("session-1", new TokenUsage { InputTokens = 1_000, OutputTokens = 200 });
        tracker.Record("session-1", new TokenUsage { InputTokens = 1_500, OutputTokens = 300 });

        var stats = tracker.Get("session-1");
        stats!.LastRunInputTokens.Should().Be(1_500);
        stats.CumulativeInputTokens.Should().Be(2_500);
        stats.CumulativeOutputTokens.Should().Be(500);
        stats.TurnCount.Should().Be(2);
    }

    [Fact]
    public void Reset_removes_tracked_session()
    {
        var tracker = new ChannelSessionStatsTracker();
        tracker.Record("session-1", new TokenUsage { InputTokens = 1_000, OutputTokens = 200 });

        tracker.Reset("session-1");

        tracker.Get("session-1").Should().BeNull();
    }

    [Fact]
    public void Reset_on_unknown_session_is_noop()
    {
        var tracker = new ChannelSessionStatsTracker();

        // No throw, no state change
        tracker.Reset("never-seen");

        tracker.Get("never-seen").Should().BeNull();
    }

    [Fact]
    public void Sessions_are_isolated_from_each_other()
    {
        var tracker = new ChannelSessionStatsTracker();
        tracker.Record("session-a", new TokenUsage { InputTokens = 100, OutputTokens = 10 });
        tracker.Record("session-b", new TokenUsage { InputTokens = 200, OutputTokens = 20 });

        tracker.Reset("session-a");

        tracker.Get("session-a").Should().BeNull();
        tracker.Get("session-b").Should().NotBeNull();
        tracker.Get("session-b")!.CumulativeInputTokens.Should().Be(200);
    }
}
