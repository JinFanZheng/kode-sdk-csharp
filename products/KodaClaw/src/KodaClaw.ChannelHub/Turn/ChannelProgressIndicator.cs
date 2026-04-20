using System.Globalization;
using System.Threading.Channels;
using Kode.Agent.Sdk.Core.Abstractions;
using KodaClaw.Contracts;

namespace KodaClaw.ChannelHub.Turn;

/// <summary>
/// Edits a single "progress" message on supporting channels (Telegram, Feishu) as
/// Agent state advances. Subscribes to Progress (tool:start/end, done) and Monitor
/// (breakpoint_changed) events, renders a short status string, and throttles edits
/// so the channel rate limits are not tripped.
/// </summary>
public sealed class ChannelProgressIndicator
{
    public const string StyleVerbose = "verbose";
    public const string StyleCompact = "compact";

    private static readonly TimeSpan DefaultMinInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(10);
    private const int DefaultMaxEdits = 10;
    private const int DefaultMaxConsecutiveFailures = 2;

    private static readonly IReadOnlyDictionary<string, string> ToolDisplayNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["fs_read"] = "读取文件",
            ["fs_grep"] = "搜索代码",
            ["fs_glob"] = "列举文件",
            ["fs_list"] = "列举目录",
            ["fs_write"] = "写入文件",
            ["fs_edit"] = "编辑文件",
            ["fs_rm"] = "删除文件",
            ["bash_run"] = "执行命令",
            ["bash_logs"] = "查看日志",
            ["bash_kill"] = "终止进程",
            ["todo_read"] = "读取待办",
            ["todo_write"] = "更新待办",
        };

    public sealed record Options(
        string Style = StyleVerbose,
        TimeSpan? MinInterval = null,
        TimeSpan? HeartbeatInterval = null,
        int MaxEdits = DefaultMaxEdits,
        int MaxConsecutiveFailures = DefaultMaxConsecutiveFailures);

    /// <summary>Consumes events and edits a single progress message until DoneEvent or cancellation.</summary>
    public static async Task<ChannelProgressIndicatorResult> RunAsync(
        IAsyncEnumerable<EventEnvelope> events,
        Func<string, CancellationToken, Task<ChannelSendReceipt>> sendInitial,
        Func<string, string, CancellationToken, Task> edit,
        Func<int> getStepCount,
        DateTimeOffset turnStartedAt,
        Options? options = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(sendInitial);
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(getStepCount);

        options ??= new Options();
        timeProvider ??= TimeProvider.System;
        var minInterval = options.MinInterval ?? DefaultMinInterval;
        var heartbeat = options.HeartbeatInterval ?? DefaultHeartbeatInterval;

        var initialText = BuildInitialText(options.Style);
        ChannelSendReceipt receipt;
        try
        {
            receipt = await sendInitial(initialText, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ChannelProgressIndicatorResult(
                InitialSent: false,
                Degraded: true,
                EditsAttempted: 0,
                EditsSucceeded: 0,
                ReachedDone: false,
                ExternalMessageId: null,
                FailureReason: ex.Message);
        }

        if (string.IsNullOrWhiteSpace(receipt.ExternalMessageId))
        {
            return new ChannelProgressIndicatorResult(
                InitialSent: true,
                Degraded: true,
                EditsAttempted: 0,
                EditsSucceeded: 0,
                ReachedDone: false,
                ExternalMessageId: null,
                FailureReason: "send_receipt_missing_message_id");
        }

        var state = new IndicatorState(
            MessageId: receipt.ExternalMessageId!,
            TurnStartedAt: turnStartedAt,
            Style: options.Style,
            MinInterval: minInterval,
            HeartbeatInterval: heartbeat,
            MaxEdits: options.MaxEdits,
            MaxConsecutiveFailures: options.MaxConsecutiveFailures,
            GetStepCount: getStepCount,
            Edit: edit,
            TimeProvider: timeProvider);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var eventsChannel = Channel.CreateUnbounded<EventEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        var heartbeatChannel = Channel.CreateUnbounded<HeartbeatTick>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        var eventPump = Task.Run(async () =>
        {
            try
            {
                await foreach (var envelope in events.WithCancellation(linkedCts.Token).ConfigureAwait(false))
                {
                    await eventsChannel.Writer.WriteAsync(envelope, linkedCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                eventsChannel.Writer.TryComplete();
            }
        }, linkedCts.Token);

        var heartbeatPump = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(heartbeat, timeProvider);
                while (await timer.WaitForNextTickAsync(linkedCts.Token).ConfigureAwait(false))
                {
                    await heartbeatChannel.Writer.WriteAsync(
                        new HeartbeatTick(timeProvider.GetUtcNow()),
                        linkedCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                heartbeatChannel.Writer.TryComplete();
            }
        }, linkedCts.Token);

        try
        {
            while (true)
            {
                var readEvent = eventsChannel.Reader.WaitToReadAsync(linkedCts.Token).AsTask();
                var readHeartbeat = heartbeatChannel.Reader.WaitToReadAsync(linkedCts.Token).AsTask();
                var completed = await Task.WhenAny(readEvent, readHeartbeat).ConfigureAwait(false);

                if (completed == readEvent)
                {
                    if (!await readEvent.ConfigureAwait(false))
                    {
                        break;
                    }

                    while (eventsChannel.Reader.TryRead(out var envelope))
                    {
                        var doneReached = await state.HandleEventAsync(envelope, linkedCts.Token).ConfigureAwait(false);
                        if (doneReached)
                        {
                            return state.BuildResult(reachedDone: true);
                        }
                    }
                }
                else
                {
                    if (!await readHeartbeat.ConfigureAwait(false))
                    {
                        break;
                    }

                    while (heartbeatChannel.Reader.TryRead(out var _))
                    {
                        await state.HandleHeartbeatAsync(linkedCts.Token).ConfigureAwait(false);
                    }
                }

                if (state.IsDegraded)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            linkedCts.Cancel();
            try { await eventPump.ConfigureAwait(false); } catch { }
            try { await heartbeatPump.ConfigureAwait(false); } catch { }
        }

        return state.BuildResult(reachedDone: false);
    }

    private static string BuildInitialText(string style) =>
        style == StyleCompact ? "🔄 思考中…" : "🔄 思考中…";

    private readonly record struct HeartbeatTick(DateTimeOffset At);

    private sealed class IndicatorState
    {
        private readonly Func<int> _getStepCount;
        private readonly Func<string, string, CancellationToken, Task> _edit;
        private readonly TimeProvider _timeProvider;

        private readonly string _messageId;
        private readonly DateTimeOffset _turnStartedAt;
        private readonly string _style;
        private readonly TimeSpan _minInterval;
        private readonly TimeSpan _heartbeatInterval;
        private readonly int _maxEdits;
        private readonly int _maxConsecutiveFailures;

        private BreakpointState _lastBreakpoint = BreakpointState.Ready;
        private string? _currentToolName;
        private int _lastStepCount;
        private string? _lastRenderedText;
        private DateTimeOffset _lastEditAt = DateTimeOffset.MinValue;
        private int _editsAttempted;
        private int _editsSucceeded;
        private int _consecutiveFailures;
        private bool _degraded;
        private bool _reachedDone;
        private bool _cancelled;

        public IndicatorState(
            string MessageId,
            DateTimeOffset TurnStartedAt,
            string Style,
            TimeSpan MinInterval,
            TimeSpan HeartbeatInterval,
            int MaxEdits,
            int MaxConsecutiveFailures,
            Func<int> GetStepCount,
            Func<string, string, CancellationToken, Task> Edit,
            TimeProvider TimeProvider)
        {
            _messageId = MessageId;
            _turnStartedAt = TurnStartedAt;
            _style = Style;
            _minInterval = MinInterval;
            _heartbeatInterval = HeartbeatInterval;
            _maxEdits = MaxEdits;
            _maxConsecutiveFailures = MaxConsecutiveFailures;
            _getStepCount = GetStepCount;
            _edit = Edit;
            _timeProvider = TimeProvider;
        }

        public bool IsDegraded => _degraded;

        public async Task<bool> HandleEventAsync(EventEnvelope envelope, CancellationToken ct)
        {
            switch (envelope.Event)
            {
                case BreakpointChangedEvent bp:
                    _lastBreakpoint = bp.Current;
                    if (bp.Current is BreakpointState.Ready
                        or BreakpointState.PreModel
                        or BreakpointState.StreamingModel)
                    {
                        _currentToolName = null;
                    }
                    await TryEditAsync(force: false, ct).ConfigureAwait(false);
                    return false;

                case ToolStartEvent ts:
                    _currentToolName = ts.Call?.Name;
                    await TryEditAsync(force: false, ct).ConfigureAwait(false);
                    return false;

                case ToolEndEvent:
                    _currentToolName = null;
                    return false;

                case DoneEvent done:
                    // done.Step 是 session 累计值，改用 _getStepCount()（已由上层做 per-turn 转换）。
                    _lastStepCount = Math.Max(_lastStepCount, _getStepCount());
                    var reason = done.Reason ?? string.Empty;
                    _cancelled = reason.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
                        || reason.Equals("stopped", StringComparison.OrdinalIgnoreCase);
                    _reachedDone = true;
                    await TryEditAsync(force: true, ct).ConfigureAwait(false);
                    return true;
            }

            return false;
        }

        public Task HandleHeartbeatAsync(CancellationToken ct)
            => TryEditAsync(force: false, ct);

        public ChannelProgressIndicatorResult BuildResult(bool reachedDone) => new(
            InitialSent: true,
            Degraded: _degraded,
            EditsAttempted: _editsAttempted,
            EditsSucceeded: _editsSucceeded,
            ReachedDone: reachedDone,
            ExternalMessageId: _messageId,
            FailureReason: _degraded ? "edit_failed" : null);

        private async Task TryEditAsync(bool force, CancellationToken ct)
        {
            if (_degraded)
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();
            var stepCount = Math.Max(_lastStepCount, _getStepCount());
            _lastStepCount = stepCount;
            var elapsed = now - _turnStartedAt;
            var text = Render(_lastBreakpoint, _currentToolName, stepCount, elapsed, _cancelled, _reachedDone, _style);

            if (!force)
            {
                if (_editsAttempted >= _maxEdits)
                {
                    return;
                }

                if (now - _lastEditAt < _minInterval)
                {
                    return;
                }

                if (string.Equals(text, _lastRenderedText, StringComparison.Ordinal))
                {
                    return;
                }
            }

            _editsAttempted++;
            try
            {
                await _edit(_messageId, text, ct).ConfigureAwait(false);
                _lastRenderedText = text;
                _lastEditAt = now;
                _editsSucceeded++;
                _consecutiveFailures = 0;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= _maxConsecutiveFailures)
                {
                    _degraded = true;
                }
            }
        }

        internal static string Render(
            BreakpointState breakpoint,
            string? toolName,
            int stepCount,
            TimeSpan elapsed,
            bool cancelled,
            bool reachedDone,
            string style)
        {
            var elapsedStr = FormatElapsed(elapsed);

            if (reachedDone)
            {
                if (cancelled)
                {
                    return "✗ 已取消";
                }

                return style == StyleCompact
                    ? "✓ 完成"
                    : $"✓ 完成 · {stepCount} 步 / {elapsedStr}";
            }

            if (cancelled)
            {
                return "✗ 已取消";
            }

            if (style == StyleCompact)
            {
                return breakpoint == BreakpointState.AwaitingApproval ? "⏸ 等待审批" : "🔄 思考中…";
            }

            var toolLabel = LocalizeToolName(toolName);

            // Progress 阶段不带 elapsed：消息编辑是事件驱动 + 10s 心跳，
            // 中间帧的秒数会冻结，导致用户看到"3s → 13s"的跳变观感。
            // 总耗时在 DoneEvent 终态上展示。
            return breakpoint switch
            {
                BreakpointState.AwaitingApproval when toolLabel is not null
                    => $"⏸ 等待审批 · {toolLabel}",
                BreakpointState.AwaitingApproval
                    => "⏸ 等待审批",

                BreakpointState.Ready
                    or BreakpointState.PreModel
                    => $"🔄 思考中 · 第 {stepCount} 步",
                BreakpointState.StreamingModel
                    => $"🔄 回复中 · 第 {stepCount} 步",

                BreakpointState.ToolPending
                    or BreakpointState.PreTool
                    or BreakpointState.ToolExecuting when toolLabel is not null
                    => $"🔄 {toolLabel} · 第 {stepCount} 步",
                BreakpointState.ToolPending
                    or BreakpointState.PreTool
                    or BreakpointState.ToolExecuting
                    => $"🔄 调用工具 · 第 {stepCount} 步",

                BreakpointState.PostTool
                    => $"🔄 整理结果 · 第 {stepCount} 步",

                _ => $"🔄 思考中 · 第 {stepCount} 步",
            };
        }

        private static string? LocalizeToolName(string? toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return null;
            }

            if (ToolDisplayNames.TryGetValue(toolName, out var display))
            {
                return display;
            }

            return toolName;
        }

        private static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            if (elapsed.TotalSeconds < 60)
            {
                return $"{(int)Math.Floor(elapsed.TotalSeconds)}s";
            }

            var minutes = (int)elapsed.TotalMinutes;
            var seconds = elapsed.Seconds;
            return seconds == 0
                ? $"{minutes}m"
                : string.Create(CultureInfo.InvariantCulture, $"{minutes}m{seconds:D2}s");
        }
    }
}

public sealed record ChannelProgressIndicatorResult(
    bool InitialSent,
    bool Degraded,
    int EditsAttempted,
    int EditsSucceeded,
    bool ReachedDone,
    string? ExternalMessageId,
    string? FailureReason);
