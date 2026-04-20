using System.Threading.Channels;
using FluentAssertions;
using Kode.Agent.Sdk.Core.Abstractions;
using KodaClaw.ChannelHub.Turn;
using KodaClaw.Contracts;
using Xunit;

namespace KodaClaw.UnitTests.ChannelHub;

/// <summary>
/// KC-7202 L1: ChannelProgressIndicator 渲染 + 节流 + 降级行为。
/// </summary>
public sealed class ChannelProgressIndicatorTests
{
    [Fact]
    public async Task Run_edits_on_each_state_change_then_emits_done()
    {
        var harness = new IndicatorHarness(stepCount: 3);
        harness.QueueBreakpoint(BreakpointState.PreModel);
        harness.QueueToolStart("fs_read");
        harness.QueueBreakpoint(BreakpointState.ToolExecuting);
        harness.QueueDone(step: 5, reason: "completed");
        harness.CompleteEvents();

        var result = await harness.RunAsync(new ChannelProgressIndicator.Options(
            Style: ChannelProgressIndicator.StyleVerbose,
            MinInterval: TimeSpan.Zero,
            HeartbeatInterval: TimeSpan.FromMinutes(10)));

        result.InitialSent.Should().BeTrue();
        result.ReachedDone.Should().BeTrue();
        result.Degraded.Should().BeFalse();
        result.EditsSucceeded.Should().BeGreaterThan(0);
        harness.Edits.Last().Text.Should().Contain("✓ 完成");
        // DoneEvent 改用 getStepCount()（harness stepCount=3），不再使用 done.Step 绝对值。
        harness.Edits.Last().Text.Should().Contain("3 步");
    }

    [Fact]
    public async Task Run_applies_min_interval_throttling()
    {
        var harness = new IndicatorHarness(stepCount: 1);
        harness.QueueBreakpoint(BreakpointState.PreModel);
        harness.QueueBreakpoint(BreakpointState.ToolPending);
        harness.QueueBreakpoint(BreakpointState.ToolExecuting);
        harness.QueueDone(step: 2, reason: "completed");
        harness.CompleteEvents();

        // MinInterval > 1h + frozen time: first edit passes the "lastEditAt == MinValue" gate,
        // subsequent edits within the same frozen instant are throttled, Done is force-rendered.
        var result = await harness.RunAsync(new ChannelProgressIndicator.Options(
            Style: ChannelProgressIndicator.StyleVerbose,
            MinInterval: TimeSpan.FromHours(1),
            HeartbeatInterval: TimeSpan.FromHours(1)));

        result.EditsSucceeded.Should().Be(2);
        harness.Edits.Should().HaveCount(2);
        harness.Edits[0].Text.Should().Contain("思考中");
        harness.Edits[1].Text.Should().Contain("✓ 完成");
    }

    [Fact]
    public async Task Run_deduplicates_consecutive_identical_renders()
    {
        var harness = new IndicatorHarness(stepCount: 2);
        harness.QueueBreakpoint(BreakpointState.PreModel);
        harness.QueueBreakpoint(BreakpointState.PreModel);
        harness.QueueBreakpoint(BreakpointState.PreModel);
        harness.QueueDone(step: 2, reason: "completed");
        harness.CompleteEvents();

        var result = await harness.RunAsync(new ChannelProgressIndicator.Options(
            MinInterval: TimeSpan.Zero,
            HeartbeatInterval: TimeSpan.FromHours(1)));

        // First PreModel renders, next two identical are deduped, Done forces a final edit.
        result.EditsSucceeded.Should().Be(2);
        harness.Edits.Should().HaveCount(2);
    }

    [Fact]
    public async Task Run_caps_edits_at_max_edits_but_still_forces_final_render()
    {
        var harness = new IndicatorHarness(stepCount: 3);
        // Start with ToolExecuting breakpoint so each ToolStart renders a distinct tool-name text.
        harness.QueueBreakpoint(BreakpointState.ToolExecuting);
        for (var i = 0; i < 50; i++)
        {
            harness.QueueToolStart($"tool_{i}");
        }
        harness.QueueDone(step: 50, reason: "completed");
        harness.CompleteEvents();

        var result = await harness.RunAsync(new ChannelProgressIndicator.Options(
            MinInterval: TimeSpan.Zero,
            HeartbeatInterval: TimeSpan.FromHours(1),
            MaxEdits: 3));

        // Non-forced edits capped at 3, plus the forced Done edit.
        result.EditsSucceeded.Should().Be(4);
        harness.Edits.Last().Text.Should().Contain("✓ 完成");
    }

    [Fact]
    public async Task Run_degrades_after_consecutive_failures_and_skips_terminal()
    {
        var harness = new IndicatorHarness(stepCount: 1) { FailEditsAlways = true };
        harness.QueueBreakpoint(BreakpointState.PreModel);
        harness.QueueBreakpoint(BreakpointState.ToolExecuting);
        harness.QueueBreakpoint(BreakpointState.PostTool);
        harness.QueueDone(step: 2, reason: "completed");
        harness.CompleteEvents();

        var result = await harness.RunAsync(new ChannelProgressIndicator.Options(
            MinInterval: TimeSpan.Zero,
            HeartbeatInterval: TimeSpan.FromHours(1),
            MaxConsecutiveFailures: 2));

        result.Degraded.Should().BeTrue();
        result.EditsSucceeded.Should().Be(0);
        result.EditsAttempted.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Run_returns_degraded_when_initial_send_fails()
    {
        var harness = new IndicatorHarness(stepCount: 0) { FailInitialSend = true };
        harness.QueueDone(step: 0, reason: "completed");
        harness.CompleteEvents();

        var result = await harness.RunAsync(new ChannelProgressIndicator.Options(
            MinInterval: TimeSpan.Zero));

        result.InitialSent.Should().BeFalse();
        result.Degraded.Should().BeTrue();
        result.EditsAttempted.Should().Be(0);
        harness.Edits.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_returns_degraded_when_initial_send_receipt_has_no_message_id()
    {
        var harness = new IndicatorHarness(stepCount: 0) { InitialExternalMessageId = null };
        harness.QueueDone(step: 0, reason: "completed");
        harness.CompleteEvents();

        var result = await harness.RunAsync(new ChannelProgressIndicator.Options());

        result.InitialSent.Should().BeTrue();
        result.Degraded.Should().BeTrue();
        result.EditsAttempted.Should().Be(0);
    }

    [Fact]
    public async Task Run_renders_awaiting_approval_then_cancelled()
    {
        var harness = new IndicatorHarness(stepCount: 2);
        harness.QueueToolStart("bash_run");
        harness.QueueBreakpoint(BreakpointState.AwaitingApproval);
        harness.QueueDone(step: 2, reason: "cancelled");
        harness.CompleteEvents();

        var result = await harness.RunAsync(new ChannelProgressIndicator.Options(
            MinInterval: TimeSpan.Zero,
            HeartbeatInterval: TimeSpan.FromHours(1)));

        result.ReachedDone.Should().BeTrue();
        harness.Edits.Should().Contain(e => e.Text.Contains("⏸ 等待审批"));
        harness.Edits.Last().Text.Should().Be("✗ 已取消");
    }

    [Fact]
    public void Render_compact_style_produces_short_text()
    {
        var text = ChannelProgressIndicatorTestProxy.Render(
            BreakpointState.ToolExecuting, "fs_read", 3, TimeSpan.FromSeconds(8),
            cancelled: false, reachedDone: false, style: ChannelProgressIndicator.StyleCompact);
        text.Should().Be("🔄 思考中…");
    }

    [Fact]
    public void Render_verbose_includes_tool_localized_name_and_step_without_elapsed()
    {
        var text = ChannelProgressIndicatorTestProxy.Render(
            BreakpointState.ToolExecuting, "fs_read", 3, TimeSpan.FromSeconds(8),
            cancelled: false, reachedDone: false, style: ChannelProgressIndicator.StyleVerbose);
        text.Should().Contain("读取文件");
        text.Should().NotContain("fs_read");
        text.Should().Contain("3 步");
        text.Should().NotContain("8s");
    }

    [Fact]
    public void Render_verbose_done_includes_step_and_total_elapsed()
    {
        var text = ChannelProgressIndicatorTestProxy.Render(
            BreakpointState.Ready, null, 5, TimeSpan.FromSeconds(12),
            cancelled: false, reachedDone: true, style: ChannelProgressIndicator.StyleVerbose);
        text.Should().Be("✓ 完成 · 5 步 / 12s");
    }

    // ── Harness ──────────────────────────────────────────────────────────────────

    private sealed class IndicatorHarness
    {
        public FrozenTimeProvider TimeProvider { get; } = new(new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero));
        public DateTimeOffset TurnStartedAt { get; set; } = new(2026, 4, 20, 12, 0, 0, TimeSpan.Zero);
        public List<(string MessageId, string Text)> Edits { get; } = new();
        public bool FailInitialSend { get; set; }
        public bool FailEditsAlways { get; set; }
        public string? InitialExternalMessageId { get; set; } = "msg-1";

        private readonly Channel<EventEnvelope> _events = Channel.CreateUnbounded<EventEnvelope>();
        private long _cursor;
        private readonly int _stepCount;

        public IndicatorHarness(int stepCount)
        {
            _stepCount = stepCount;
        }

        public void QueueBreakpoint(BreakpointState state) => _events.Writer.TryWrite(
            Wrap(new BreakpointChangedEvent
            {
                Channel = "monitor",
                Type = "breakpoint_changed",
                Previous = BreakpointState.Ready,
                Current = state,
                Timestamp = TimeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            }));

        public void QueueToolStart(string toolName) => _events.Writer.TryWrite(
            Wrap(new ToolStartEvent
            {
                Channel = "progress",
                Type = "tool:start",
                Call = new ToolCallSnapshot
                {
                    Id = $"call-{toolName}",
                    Name = toolName,
                    State = ToolCallState.Executing,
                    Approval = new ToolCallApproval { Required = false },
                },
            }));

        public void QueueDone(int step, string reason) => _events.Writer.TryWrite(
            Wrap(new DoneEvent
            {
                Channel = "progress",
                Type = "done",
                Step = step,
                Reason = reason,
            }));

        public void CompleteEvents() => _events.Writer.TryComplete();

        public async Task<ChannelProgressIndicatorResult> RunAsync(ChannelProgressIndicator.Options options)
        {
            return await ChannelProgressIndicator.RunAsync(
                events: _events.Reader.ReadAllAsync(),
                sendInitial: (_, _) =>
                {
                    if (FailInitialSend)
                    {
                        throw new InvalidOperationException("initial send failed");
                    }
                    return Task.FromResult(new ChannelSendReceipt(InitialExternalMessageId, TimeProvider.GetUtcNow()));
                },
                edit: (id, text, _) =>
                {
                    if (FailEditsAlways)
                    {
                        throw new InvalidOperationException("edit failed");
                    }
                    Edits.Add((id, text));
                    return Task.CompletedTask;
                },
                getStepCount: () => _stepCount,
                turnStartedAt: TurnStartedAt,
                options: options,
                timeProvider: TimeProvider,
                cancellationToken: CancellationToken.None);
        }

        private EventEnvelope Wrap(AgentEvent ev)
        {
            var seq = Interlocked.Increment(ref _cursor);
            return new EventEnvelope
            {
                Cursor = seq,
                Bookmark = new Bookmark
                {
                    Seq = seq,
                    Timestamp = TimeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                },
                Event = ev,
            };
        }
    }
}

internal sealed class FrozenTimeProvider : TimeProvider
{
    private long _ticks;

    public FrozenTimeProvider(DateTimeOffset now)
    {
        _ticks = now.UtcTicks;
    }

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => new NoopTimer();

    private sealed class NoopTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Dispose() { }
    }
}

internal static class ChannelProgressIndicatorTestProxy
{
    public static string Render(
        BreakpointState breakpoint,
        string? toolName,
        int stepCount,
        TimeSpan elapsed,
        bool cancelled,
        bool reachedDone,
        string style)
    {
        // Reflection into the internal static Render method.
        var stateType = typeof(ChannelProgressIndicator).GetNestedType(
            "IndicatorState",
            System.Reflection.BindingFlags.NonPublic)!;
        var method = stateType.GetMethod(
            "Render",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (string)method.Invoke(null, new object?[]
        {
            breakpoint, toolName, stepCount, elapsed, cancelled, reachedDone, style,
        })!;
    }
}
