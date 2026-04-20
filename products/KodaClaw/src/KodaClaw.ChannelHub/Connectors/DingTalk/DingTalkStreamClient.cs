using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using KodaClaw.ChannelHub.Common;
using KodaClaw.Contracts;

namespace KodaClaw.ChannelHub.Connectors.DingTalk;

/// <summary>
/// 钉钉 Stream 模式 WebSocket 长连接客户端（纯 JSON，无 protobuf）。
///
/// 连接流程：
///   1. POST /v1.0/gateway/connections/open → 获取动态 wss:// endpoint
///   2. ws.ConnectAsync(endpoint)
///   3. 收到 JSON 消息 → 立即 ACK → fire-and-forget 业务回调
///   4. 标准 WebSocket Ping/Pong 保活
///   5. 断线后指数退避重连
/// </summary>
internal sealed class DingTalkStreamClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string BotMessageTopic = "/v1.0/im/bot/messages/get";

    private readonly IDingTalkApiClient _apiClient;
    private readonly string _appKey;
    private readonly string _appSecret;
    private readonly Func<DingTalkStreamEventData, string, CancellationToken, Task> _onEvent;
    private readonly DingTalkConnectorOptions _options;
    private readonly IDiagnosticsService? _diagnosticsService;
    private readonly EventDedupeTracker _dedupeTracker = new();
    private CancellationTokenSource? _cts;
    private Task _loopTask = Task.CompletedTask;

    public DingTalkStreamClient(
        IDingTalkApiClient apiClient,
        string appKey,
        string appSecret,
        Func<DingTalkStreamEventData, string, CancellationToken, Task> onEvent,
        DingTalkConnectorOptions options,
        IDiagnosticsService? diagnosticsService = null)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _appKey = appKey ?? throw new ArgumentNullException(nameof(appKey));
        _appSecret = appSecret ?? throw new ArgumentNullException(nameof(appSecret));
        _onEvent = onEvent ?? throw new ArgumentNullException(nameof(onEvent));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _diagnosticsService = diagnosticsService;
    }

    public void Start(CancellationToken externalCancellation)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        _loopTask = Task.Run(() => RunConnectionLoopAsync(_cts.Token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { await _loopTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // ── 连接主循环（断线自动重连）────────────────────────────────────────

    private Task RunConnectionLoopAsync(CancellationToken cancellationToken) =>
        WebSocketReconnectLoop.RunAsync(
            new WebSocketReconnectLoopOptions
            {
                BaseDelay = _options.ReconnectBaseDelay,
                MaxDelay = _options.ReconnectMaxDelay,
                RunSingleConnectionAsync = RunSingleConnectionAsync,
                OnReconnecting = nextAttempt => RecordDiagnosticEvent(
                    "dingtalk.ws.reconnecting", "info",
                    $"appKey={_appKey} connection closed, reconnecting (attempt #{nextAttempt})..."),
                OnConnectionFailed = (attempt, ex) => RecordDiagnosticEvent(
                    "dingtalk.ws.connection_failed", "error",
                    $"appKey={_appKey} connection failed (attempt #{attempt}): {ex.Message}"),
            },
            cancellationToken);

    private async Task RunSingleConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionInfo = await _apiClient
            .OpenStreamConnectionAsync(_appKey, _appSecret, cancellationToken)
            .ConfigureAwait(false);

        using var ws = new ClientWebSocket();
        var wsUrl = connectionInfo.Endpoint!.Contains('?')
            ? connectionInfo.Endpoint + "&ticket=" + connectionInfo.Ticket
            : connectionInfo.Endpoint + "?ticket=" + connectionInfo.Ticket;
        await ws.ConnectAsync(new Uri(wsUrl), cancellationToken)
            .ConfigureAwait(false);

        RecordDiagnosticEvent("dingtalk.ws.connected", "info",
            $"appKey={_appKey} connected to stream endpoint");

        await ReceiveLoopAsync(ws, cancellationToken).ConfigureAwait(false);
    }

    // ── 接收循环 ──────────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(
        ClientWebSocket ws,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];

        while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var (text, closed) = await ReceiveTextAsync(ws, buffer, cancellationToken)
                .ConfigureAwait(false);

            if (closed || text is null) break;

            await HandleMessageAsync(ws, text, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<(string? Text, bool Closed)> ReceiveTextAsync(
        ClientWebSocket ws,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var ms = new System.IO.MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                .ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
                return (null, true);

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // 跳过二进制帧（如标准 Ping 帧的响应）
                if (!result.EndOfMessage)
                {
                    // 消费完整帧
                    while (!result.EndOfMessage)
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                return (null, false);
            }

            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return (Encoding.UTF8.GetString(ms.ToArray()), false);
    }

    // ── 消息处理 ──────────────────────────────────────────────────────────

    private async Task HandleMessageAsync(
        ClientWebSocket ws,
        string text,
        CancellationToken cancellationToken)
    {
        DingTalkStreamEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<DingTalkStreamEnvelope>(text, JsonOptions);
        }
        catch (JsonException ex)
        {
            RecordDiagnosticEvent("dingtalk.ws.deserialize_failed", "warning",
                $"appKey={_appKey} deserialize failed: {ex.Message}");
            return;
        }

        if (envelope is null) return;

        var messageId = envelope.Headers?.MessageId ?? string.Empty;

        // 立即 ACK
        await SendAckAsync(ws, messageId, cancellationToken).ConfigureAwait(false);

        // 只处理 IM 消息事件
        var topic = envelope.Headers?.Topic;
        if (!string.Equals(topic, BotMessageTopic, StringComparison.Ordinal))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(envelope.Data)) return;

        // 过滤陈旧事件
        var eventBornTimeStr = envelope.Headers?.EventBornTime ?? envelope.Headers?.Time;
        if (!string.IsNullOrWhiteSpace(eventBornTimeStr)
            && long.TryParse(eventBornTimeStr, out var eventBornMs))
        {
            var eventAge = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(eventBornMs);
            if (eventAge > TimeSpan.FromMinutes(10))
            {
                RecordDiagnosticEvent("dingtalk.ws.stale_event_dropped", "info",
                    $"appKey={_appKey} dropped stale event: age={eventAge.TotalMinutes:F1}min messageId={messageId}");
                return;
            }
        }

        // 事件去重
        if (!string.IsNullOrWhiteSpace(messageId) && !_dedupeTracker.TryTrack(messageId)) return;

        DingTalkStreamEventData? eventData;
        try
        {
            eventData = JsonSerializer.Deserialize<DingTalkStreamEventData>(envelope.Data, JsonOptions);
        }
        catch (JsonException ex)
        {
            RecordDiagnosticEvent("dingtalk.ws.event_data_deserialize_failed", "warning",
                $"appKey={_appKey} event data deserialize failed: {ex.Message}");
            return;
        }

        if (eventData is null) return;

        // msgId 去重兜底
        if (!string.IsNullOrWhiteSpace(eventData.MsgId) && !_dedupeTracker.TryTrack(eventData.MsgId)) return;

        // fire-and-forget：业务处理不阻塞 WS 接收循环
        _ = Task.Run(async () =>
        {
            try
            {
                await _onEvent(eventData, messageId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RecordDiagnosticEvent("dingtalk.ws.event_handler_error", "error",
                    $"appKey={_appKey} event handler error: {ex.Message}");
            }
        }, CancellationToken.None);
    }

    private static async Task SendAckAsync(
        ClientWebSocket ws,
        string messageId,
        CancellationToken cancellationToken)
    {
        try
        {
            var ack = JsonSerializer.Serialize(new
            {
                code = 200,
                headers = new { messageId },
                message = "OK",
                data = "OK",
            }, JsonOptions);

            var bytes = Encoding.UTF8.GetBytes(ack);
            await ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // ACK 失败不影响接收循环，钉钉会重投
        }
    }

    // ── 诊断 ──────────────────────────────────────────────────────────────

    private void RecordDiagnosticEvent(string eventType, string level, string message)
    {
        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: "dingtalk.stream",
            EventType: eventType,
            Level: level,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow));
    }
}
