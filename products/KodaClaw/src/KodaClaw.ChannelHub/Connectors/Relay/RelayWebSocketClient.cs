using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using KodaClaw.ChannelHub.Common;
using KodaClaw.Contracts;

namespace KodaClaw.ChannelHub.Connectors.Relay;

/// <summary>
/// Relay WebSocket 长连接客户端。
///
/// 连接流程：
///   1. ws.ConnectAsync(relayUrl)
///   2. 发送 auth 帧（accountId + sharedSecret）
///   3. 等待 auth_ok / auth_failed
///   4. 进入接收循环：event → ACK → fire-and-forget 业务回调
///   5. 双向心跳 ping/pong
///   6. 断线后指数退避重连
/// </summary>
internal sealed class RelayWebSocketClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _accountId;
    private readonly string _relayUrl;
    private readonly string? _sharedSecret;
    private readonly Func<JsonElement, CancellationToken, Task> _onEvent;
    private readonly RelayConnectorOptions _options;
    private readonly IDiagnosticsService? _diagnosticsService;
    private readonly EventDedupeTracker _dedupeTracker = new();
    private CancellationTokenSource? _cts;
    private Task _loopTask = Task.CompletedTask;

    public RelayWebSocketClient(
        string accountId,
        string relayUrl,
        string? sharedSecret,
        Func<JsonElement, CancellationToken, Task> onEvent,
        RelayConnectorOptions options,
        IDiagnosticsService? diagnosticsService = null)
    {
        _accountId = accountId ?? throw new ArgumentNullException(nameof(accountId));
        _relayUrl = relayUrl ?? throw new ArgumentNullException(nameof(relayUrl));
        _sharedSecret = sharedSecret;
        _onEvent = onEvent ?? throw new ArgumentNullException(nameof(onEvent));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _diagnosticsService = diagnosticsService;
    }

    public void Start(CancellationToken externalCancellation)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        _loopTask = Task.Run(() =>
            RunConnectionLoopAsync(_cts.Token), CancellationToken.None);
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

    public async ValueTask DisposeAsync() =>
        await StopAsync().ConfigureAwait(false);

    // ── 连接主循环（断线自动重连）──

    private Task RunConnectionLoopAsync(CancellationToken cancellationToken) =>
        WebSocketReconnectLoop.RunAsync(
            new WebSocketReconnectLoopOptions
            {
                BaseDelay = _options.ReconnectBaseDelay,
                MaxDelay = _options.ReconnectMaxDelay,
                RunSingleConnectionAsync = RunSingleConnectionAsync,
                OnReconnecting = nextAttempt => RecordDiagnosticEvent(
                    "relay.ws.reconnecting", "info",
                    $"account={_accountId} connection closed, reconnecting (attempt #{nextAttempt})..."),
                OnConnectionFailed = (attempt, ex) => RecordDiagnosticEvent(
                    "relay.ws.connection_failed", "error",
                    $"account={_accountId} connection failed (attempt #{attempt}): {ex.Message}"),
                IsTerminalException = ex => ex is AuthFailedException,
                OnTerminalException = _ => RecordDiagnosticEvent(
                    "relay.ws.auth_failed", "error",
                    $"account={_accountId} authentication failed, will NOT retry."),
            },
            cancellationToken);

    private async Task RunSingleConnectionAsync(CancellationToken cancellationToken)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(_relayUrl), cancellationToken).ConfigureAwait(false);
        RecordDiagnosticEvent("relay.ws.connected", "info",
            $"account={_accountId} connected to {_relayUrl}");

        // 认证
        await SendAuthAsync(ws, cancellationToken).ConfigureAwait(false);
        await WaitForAuthResultAsync(ws, cancellationToken).ConfigureAwait(false);

        RecordDiagnosticEvent("relay.ws.authenticated", "info",
            $"account={_accountId} authenticated successfully");

        // 心跳
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = RunHeartbeatAsync(ws, heartbeatCts.Token);

        try
        {
            await ReceiveLoopAsync(ws, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeatTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    // ── 认证 ──

    private async Task SendAuthAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        var authPayload = new { type = "auth", accountId = _accountId, sharedSecret = _sharedSecret };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(authPayload, JsonOptions));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForAuthResultAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.AuthTimeout);

        var buffer = new byte[4096];
        var sb = new StringBuilder();

        while (!timeoutCts.Token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), timeoutCts.Token).ConfigureAwait(false);
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

            if (result.EndOfMessage)
            {
                var raw = sb.ToString();
                sb.Clear();

                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    var type = doc.RootElement.GetProperty("type").GetString();

                    if (type == "auth_ok")
                    {
                        return; // 认证成功
                    }

                    if (type == "auth_failed")
                    {
                        var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : "unknown";
                        var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "";
                        throw new AuthFailedException($"Auth failed: {code} - {message}");
                    }

                    // 认证等待期间响应 ping
                    if (type == "ping")
                    {
                        var pong = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "pong" }, JsonOptions));
                        await ws.SendAsync(pong, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (JsonException)
                {
                    // 非 JSON 帧忽略
                }
            }
        }

        throw new TimeoutException("Timed out waiting for auth response.");
    }

    // ── 心跳 ──

    private async Task RunHeartbeatAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try
            {
                await Task.Delay(_options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
                var ping = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "ping" }, JsonOptions));
                await ws.SendAsync(ping, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                RecordDiagnosticEvent("relay.ws.heartbeat_error", "warning",
                    $"account={_accountId} heartbeat error: {ex.Message}");
                return;
            }
        }
    }

    // ── 接收循环 ──

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];

        while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            string raw;
            using (var ms = new MemoryStream())
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                raw = Encoding.UTF8.GetString(ms.ToArray());
            }

            JsonElement root;
            try
            {
                root = JsonSerializer.Deserialize<JsonElement>(raw, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

            switch (type)
            {
                case "ping":
                    var pong = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "pong" }, JsonOptions));
                    await ws.SendAsync(pong, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
                    break;

                case "pong":
                    break; // ignore

                case "event":
                    await HandleEventAsync(ws, root, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    RecordDiagnosticEvent("relay.ws.unknown_frame", "info",
                        $"account={_accountId} unknown frame type: {type}");
                    break;
            }
        }
    }

    // ── 事件处理 ──

    private async Task HandleEventAsync(ClientWebSocket ws, JsonElement root, CancellationToken cancellationToken)
    {
        var eventId = root.TryGetProperty("eventId", out var eidProp) ? eidProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(eventId))
        {
            RecordDiagnosticEvent("relay.ws.event_missing_id", "warning",
                $"account={_accountId} received event without eventId");
            return;
        }

        // 去重
        if (!_dedupeTracker.TryTrack(eventId)) return;

        // ACK
        var ack = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "ack", eventId }, JsonOptions));
        try
        {
            await ws.SendAsync(ack, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordDiagnosticEvent("relay.ws.ack_failed", "warning",
                $"account={_accountId} failed to ACK event {eventId}: {ex.Message}");
        }

        // 检查事件过期（超过 10 分钟丢弃）
        if (root.TryGetProperty("occurredAt", out var occurredAtProp)
            && DateTimeOffset.TryParse(occurredAtProp.GetString(), out var occurredAt))
        {
            var age = DateTimeOffset.UtcNow - occurredAt;
            if (age > TimeSpan.FromMinutes(10))
            {
                RecordDiagnosticEvent("relay.ws.stale_event_dropped", "info",
                    $"account={_accountId} dropped stale event: age={age.TotalMinutes:F1}min eventId={eventId}");
                return;
            }
        }

        RecordDiagnosticEvent("relay.ws.event_received", "info",
            $"account={_accountId} received event: eventId={eventId}");

        // fire-and-forget
        _ = Task.Run(async () =>
        {
            try
            {
                await _onEvent(root, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RecordDiagnosticEvent("relay.ws.event_handler_error", "error",
                    $"account={_accountId} event handler error for {eventId}: {ex.Message}");
            }
        }, CancellationToken.None);
    }

    // ── 辅助 ──

    private void RecordDiagnosticEvent(string eventType, string level, string message)
    {
        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: "relay.websocket",
            EventType: eventType,
            Level: level,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow));
    }

    // ── 内部异常 ──

    private sealed class AuthFailedException : Exception
    {
        public AuthFailedException(string message) : base(message) { }
    }
}
