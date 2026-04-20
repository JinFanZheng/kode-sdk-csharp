using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using KodaClaw.ChannelHub.Common;
using KodaClaw.Contracts;

namespace KodaClaw.ChannelHub.Connectors.Feishu;

/// <summary>
/// 飞书 WebSocket 长连接客户端（pbbp2 protobuf 二进制帧协议）。
///
/// 连接流程：
///   1. POST /callback/ws/endpoint → 获取动态 wss:// URL（含 ticket 认证）
///   2. ws.ConnectAsync(url)，无需额外 Authorization 头
///   3. 收到 method=1 事件帧 → 立即 ACK（3s 内）→ fire-and-forget 业务回调
///   4. 按服务端配置的 PingInterval 发心跳 ping 帧
///   5. 断线后指数退避重连
///
/// 帧格式（protobuf field numbers）：
///   service(3,varint), method(4,varint), headers(5,repeated msg),
///   payload(8,bytes)；Header: key(1,string), value(2,string)
/// method=0 → 控制帧（ping/pong），method=1 → 数据帧（event）
/// </summary>
internal sealed class FeishuWebSocketClient : IAsyncDisposable
{
    // ── protobuf field numbers ────────────────────────────────────────────
    private const int FnSeqId   = 1;
    private const int FnLogId   = 2;
    private const int FnService = 3;
    private const int FnMethod  = 4;
    private const int FnHeaders = 5;
    private const int FnPayload = 8;
    private const int FnHdrKey  = 1;
    private const int FnHdrVal  = 2;

    // protobuf wire types
    private const int WtVarint          = 0;
    private const int WtLengthDelimited = 2;

    // frame method constants
    private const int MethodControl = 0; // ping / pong
    private const int MethodData    = 1; // event

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IFeishuApiClient _apiClient;
    private readonly string _appId;
    private readonly string _appSecret;
    private readonly Func<FeishuWsEventEnvelope, string, CancellationToken, Task> _onEvent;
    private readonly FeishuConnectorOptions _options;
    private readonly IDiagnosticsService? _diagnosticsService;
    private readonly EventDedupeTracker _dedupeTracker = new();
    private readonly ConcurrentDictionary<string, ChunkBuffer> _chunkBuffers = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private Task _loopTask = Task.CompletedTask;

    public FeishuWebSocketClient(
        IFeishuApiClient apiClient,
        string appId,
        string appSecret,
        Func<FeishuWsEventEnvelope, string, CancellationToken, Task> onEvent,
        FeishuConnectorOptions options,
        IDiagnosticsService? diagnosticsService = null)
    {
        _apiClient  = apiClient  ?? throw new ArgumentNullException(nameof(apiClient));
        _appId      = appId      ?? throw new ArgumentNullException(nameof(appId));
        _appSecret  = appSecret  ?? throw new ArgumentNullException(nameof(appSecret));
        _onEvent    = onEvent    ?? throw new ArgumentNullException(nameof(onEvent));
        _options    = options    ?? throw new ArgumentNullException(nameof(options));
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
                    "feishu.ws.reconnecting", "info",
                    $"app={_appId} connection closed, reconnecting (attempt #{nextAttempt})..."),
                OnConnectionFailed = (attempt, ex) => RecordDiagnosticEvent(
                    "feishu.ws.connection_failed", "error",
                    $"app={_appId} connection failed (attempt #{attempt}): {ex.Message}"),
            },
            cancellationToken);

    private async Task RunSingleConnectionAsync(CancellationToken cancellationToken)
    {
        var endpoint = await _apiClient
            .GetWsEndpointAsync(_appId, _appSecret, cancellationToken)
            .ConfigureAwait(false);

        var serviceId = ParseServiceId(endpoint.Url);

        using var ws = new ClientWebSocket();
        // 认证通过 URL 中的 ticket 完成，无需额外 Authorization 头
        await ws.ConnectAsync(new Uri(endpoint.Url), cancellationToken).ConfigureAwait(false);
        RecordDiagnosticEvent("feishu.ws.connected", "info",
            $"app={_appId} connected (service_id={serviceId})");

        var pingInterval = TimeSpan.FromSeconds(
            endpoint.PingIntervalSeconds > 0 ? endpoint.PingIntervalSeconds : 90);

        // ClientWebSocket.SendAsync 不支持并发调用，用 sendLock 序列化 heartbeat 和 ACK 的发送
        using var sendLock = new SemaphoreSlim(1, 1);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = RunHeartbeatAsync(ws, serviceId, pingInterval, sendLock, heartbeatCts.Token);

        try
        {
            await ReceiveLoopAsync(ws, serviceId, sendLock, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeatTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    // ── 心跳 ─────────────────────────────────────────────────────────────

    private async Task RunHeartbeatAsync(
        ClientWebSocket ws,
        int serviceId,
        TimeSpan interval,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                var ping = BuildFrame(serviceId, MethodControl,
                    [("type", "ping")],
                    payload: null);
                await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await ws.SendAsync(ping, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    sendLock.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                RecordDiagnosticEvent("feishu.ws.heartbeat_error", "warning",
                    $"app={_appId} heartbeat error: {ex.Message}");
                return;
            }
        }
    }

    // ── 接收循环 ──────────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(
        ClientWebSocket ws,
        int serviceId,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];

        while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var (rawBytes, closed) = await ReceiveBinaryAsync(ws, buffer, cancellationToken)
                .ConfigureAwait(false);

            if (closed || rawBytes is null) break;

            var frame = DecodeFrame(rawBytes);
            if (frame is null)
            {
                RecordDiagnosticEvent("feishu.ws.decode_failed", "warning",
                    $"app={_appId} decode frame returned null");
                continue;
            }

            var frameType = GetHeader(frame.Headers, "type");
            if (frameType == "pong") continue;

            if (frame.Method == MethodData)
            {
                await HandleEventFrameAsync(ws, frame, serviceId, sendLock, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (frame.Method != MethodControl)
            {
                // 飞书服务端推送的内部协议帧（如 method=125 的投递回执确认），无需处理
                RecordDiagnosticEvent("feishu.ws.unknown_method", "info",
                    $"app={_appId} ignoring frame: method={frame.Method} service={frame.Service} type={frameType}");
            }
        }
    }

    private static async Task<(byte[]? Data, bool Closed)> ReceiveBinaryAsync(
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

            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return (ms.ToArray(), false);
    }

    // ── 事件帧处理（3 秒 ACK + 分块重组 + fire-and-forget 业务逻辑）─────

    private async Task HandleEventFrameAsync(
        ClientWebSocket ws,
        FeishuProtoFrame frame,
        int serviceId,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        var frameReceivedAt = Stopwatch.GetTimestamp();

        // 仅处理 type=event 的帧（控制帧 type=ping/pong 已在调用处过滤）
        var frameType = GetHeader(frame.Headers, "type");
        if (!string.IsNullOrEmpty(frameType) &&
            !string.Equals(frameType, "event", StringComparison.Ordinal))
        {
            return;
        }

        // 立即 ACK，满足飞书 3s 要求（通过 sendLock 避免与 heartbeat 并发写 ws）
        await AckFrameAsync(ws, frame, frameReceivedAt, sendLock, cancellationToken).ConfigureAwait(false);

        if (frame.Payload is null || frame.Payload.Length == 0) return;

        // ── 分块重组（官方 SDK DataCache.mergeData 逻辑）──────────────────
        // 每帧 headers 含 message_id、sum（总分块数）、seq（0-based 序号）
        var msgId = GetHeader(frame.Headers, "message_id");
        var sumStr = GetHeader(frame.Headers, "sum");
        var seqStr = GetHeader(frame.Headers, "seq");

        byte[] fullPayload;

        if (!string.IsNullOrEmpty(sumStr) &&
            int.TryParse(sumStr, out var sum) && sum > 1 &&
            !string.IsNullOrEmpty(msgId))
        {
            // 多块消息：累积直到全部到齐
            int.TryParse(seqStr, out var seq);
            var buf = _chunkBuffers.GetOrAdd(msgId, _ => new ChunkBuffer(sum));
            buf.Set(seq, frame.Payload);

            if (!buf.IsComplete)
                return; // 等待剩余分块

            fullPayload = buf.Assemble();
            _chunkBuffers.TryRemove(msgId, out _);
        }
        else
        {
            // 单块消息（sum==1 或无分块头）
            fullPayload = frame.Payload;
        }

        FeishuWsEventEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<FeishuWsEventEnvelope>(fullPayload, JsonOptions);
        }
        catch (JsonException ex)
        {
            RecordDiagnosticEvent("feishu.ws.deserialize_failed", "warning",
                $"app={_appId} deserialize failed: {ex.Message}");
            return;
        }

        if (envelope is null) return;

        // 过滤飞书重投的陈旧事件（飞书 ACK 失败后会在 5 分钟 / 6 小时等时间点重投，
        // 重投时 eventId 不同，普通去重无法拦截，用 createTime 兜底）
        var eventAge = DateTimeOffset.UtcNow - ParseEventTimestamp(envelope.Header?.CreateTime);
        if (eventAge > TimeSpan.FromMinutes(10))
        {
            RecordDiagnosticEvent("feishu.ws.stale_event_dropped", "info",
                $"app={_appId} dropped stale event: age={eventAge.TotalMinutes:F1}min eventId={envelope.Header?.EventId}");
            return;
        }

        var eventId = envelope.Header?.EventId ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(eventId) && !_dedupeTracker.TryTrack(eventId)) return;

        // 飞书重投时 eventId 不同但底层 message_id 不变，追加 message_id 去重兜底
        var messageId = envelope.Event?.Message?.MessageId;
        if (!string.IsNullOrWhiteSpace(messageId) && !_dedupeTracker.TryTrack(messageId)) return;

        // fire-and-forget：业务处理不阻塞 WS 接收循环
        _ = Task.Run(async () =>
        {
            try
            {
                await _onEvent(envelope, eventId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RecordDiagnosticEvent("feishu.ws.event_handler_error", "error",
                    $"app={_appId} event handler error: {ex.Message}");
            }
        }, CancellationToken.None);
    }

    private static async Task AckFrameAsync(
        ClientWebSocket ws,
        FeishuProtoFrame frame,
        long frameReceivedTimestamp,
        SemaphoreSlim sendLock,
        CancellationToken cancellationToken)
    {
        var msgId = GetHeader(frame.Headers, "message_id");
        if (string.IsNullOrWhiteSpace(msgId)) return;

        // biz_rt = 收到帧到发出 ACK 的耗时（毫秒），对齐官方 SDK 行为
        var elapsedMs = (long)((Stopwatch.GetTimestamp() - frameReceivedTimestamp)
            * 1000.0 / Stopwatch.Frequency);

        // ACK = 复用原帧 headers + 追加 biz_rt，payload = {"code":200}
        var ackHeaders = frame.Headers
            .Append(("biz_rt", elapsedMs.ToString()))
            .ToArray();
        var ackPayload = Encoding.UTF8.GetBytes("{\"code\":200}");
        var ackBytes = BuildFrame(frame.Service, frame.Method, ackHeaders, ackPayload);

        await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(ackBytes, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    // ── Protobuf 编码 / 解码 ──────────────────────────────────────────────

    private static ArraySegment<byte> BuildFrame(
        int service,
        int method,
        (string Key, string Value)[] headers,
        byte[]? payload)
    {
        var buf = new List<byte>(256);

        WriteTag(buf, FnService, WtVarint);  WriteVarint(buf, (ulong)service);
        WriteTag(buf, FnMethod,  WtVarint);  WriteVarint(buf, (ulong)method);

        foreach (var (k, v) in headers)
        {
            var hbuf = new List<byte>();
            WriteTag(hbuf, FnHdrKey, WtLengthDelimited); WriteString(hbuf, k);
            WriteTag(hbuf, FnHdrVal, WtLengthDelimited); WriteString(hbuf, v);

            WriteTag(buf, FnHeaders, WtLengthDelimited);
            WriteVarint(buf, (ulong)hbuf.Count);
            buf.AddRange(hbuf);
        }

        if (payload is { Length: > 0 })
        {
            WriteTag(buf, FnPayload, WtLengthDelimited);
            WriteVarint(buf, (ulong)payload.Length);
            buf.AddRange(payload);
        }

        return new ArraySegment<byte>(buf.ToArray());
    }

    private static FeishuProtoFrame? DecodeFrame(byte[] data)
    {
        long service = 0;
        long method  = 0;
        var headers  = new List<(string Key, string Value)>();
        byte[]? payload = null;

        var pos = 0;

        try
        {
            while (pos < data.Length)
            {
                var tag = ReadVarint(data, ref pos);
                var fn  = (int)(tag >> 3);
                var wt  = (int)(tag & 0x07);

                if (pos > data.Length) break;

                switch (fn)
                {
                    case 1: case 2: // SeqId, LogId
                    case 3: // Service
                        if (fn == 3) service = (long)ReadVarint(data, ref pos);
                        else ReadVarint(data, ref pos);
                        break;
                    case 4: // Method
                        method = (long)ReadVarint(data, ref pos);
                        break;
                    case 5: // Headers (repeated)
                    {
                        var len = (int)ReadVarint(data, ref pos);
                        if (pos + len > data.Length) break;
                        headers.Add(DecodeHeader(data, pos, len));
                        pos += len;
                        break;
                    }
                    case 8: // Payload
                    {
                        var len = (int)ReadVarint(data, ref pos);
                        if (pos + len > data.Length) break;
                        payload = new byte[len];
                        Array.Copy(data, pos, payload, 0, len);
                        pos += len;
                        break;
                    }
                    default:
                    {
                        // 飞书额外字段 6,7,9,10,11,12,13,14 — 按 wireType 跳过
                        if (wt == 0) // varint
                        {
                            ReadVarint(data, ref pos);
                        }
                        else if (wt == 2) // length-delimited
                        {
                            var len = (int)ReadVarint(data, ref pos);
                            pos += len;
                        }
                        else if (wt == 1) // 64-bit
                        {
                            pos += 8;
                        }
                        else if (wt == 5) // 32-bit
                        {
                            pos += 4;
                        }
                        else // wt 3,4,6,7 — skip 1 byte safety
                        {
                            pos++;
                        }
                        break;
                    }
                }
            }

            return new FeishuProtoFrame((int)service, (int)method, headers, payload);
        }
        catch
        {
            return null;
        }
    }

    private static (string Key, string Value) DecodeHeader(byte[] data, int start, int length)
    {
        var pos = start;
        var end = start + length;
        string key = "", value = "";

        while (pos < end)
        {
            var tag = (int)ReadVarint(data, ref pos);
            var fieldNumber = tag >> 3;
            var len = (int)ReadVarint(data, ref pos);
            var s = Encoding.UTF8.GetString(data, pos, len);
            pos += len;

            if      (fieldNumber == FnHdrKey) key   = s;
            else if (fieldNumber == FnHdrVal) value = s;
        }

        return (key, value);
    }

    private static ulong ReadVarint(byte[] data, ref int pos)
    {
        ulong result = 0;
        int   shift  = 0;

        while (pos < data.Length)
        {
            var b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift >= 64) break;
        }

        return result;
    }

    private static void SkipField(byte[] data, ref int pos, int wireType)
    {
        switch (wireType)
        {
            case WtVarint:          ReadVarint(data, ref pos); break;
            case WtLengthDelimited: pos += (int)ReadVarint(data, ref pos); break;
            case 1:                 pos += 8; break; // 64-bit fixed
            case 5:                 pos += 4; break; // 32-bit fixed
            case 3:                 SkipGroup(data, ref pos); break; // start group
            case 4:                 break; // end group
        }
    }

    /// <summary>
    /// 跳过 protobuf group（wireType=3/4，deprecated 嵌套格式）。
    /// 递归读取直到匹配的 end group tag。
    /// </summary>
    private static void SkipGroup(byte[] data, ref int pos)
    {
        if (pos >= data.Length) return;
        var startTag = (int)ReadVarint(data, ref pos);
        var groupField = startTag >> 3;
        while (pos < data.Length)
        {
            var tag = (int)ReadVarint(data, ref pos);
            var fn = tag >> 3;
            var wt = tag & 0x07;
            if (fn == groupField && wt == 4) return;
            SkipField(data, ref pos, wt);
        }
    }

    private static void WriteTag(List<byte> buf, int fieldNumber, int wireType)
        => WriteVarint(buf, (ulong)((fieldNumber << 3) | wireType));

    private static void WriteVarint(List<byte> buf, ulong value)
    {
        while (value > 0x7F)
        {
            buf.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        buf.Add((byte)value);
    }

    private static void WriteString(List<byte> buf, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        WriteVarint(buf, (ulong)bytes.Length);
        buf.AddRange(bytes);
    }

    // ── 辅助方法 ──────────────────────────────────────────────────────────

    private static string GetHeader(IReadOnlyList<(string Key, string Value)> headers, string key)
        => headers.FirstOrDefault(h => string.Equals(h.Key, key, StringComparison.Ordinal)).Value
           ?? string.Empty;

    /// <summary>从 WS URL query string 中提取 service_id 参数。</summary>
    private static int ParseServiceId(string url)
    {
        try
        {
            const string marker = "service_id=";
            var idx = url.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return 0;

            idx += marker.Length;
            var end = url.IndexOf('&', idx);
            var s = end < 0 ? url[idx..] : url[idx..end];
            return int.TryParse(s, out var id) ? id : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static DateTimeOffset ParseEventTimestamp(string? createTime)
    {
        if (string.IsNullOrWhiteSpace(createTime)) return DateTimeOffset.UtcNow;
        return long.TryParse(createTime, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : DateTimeOffset.UtcNow;
    }

    // ── 诊断 ──────────────────────────────────────────────────────────────

    private void RecordDiagnosticEvent(string eventType, string level, string message)
    {
        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: "feishu.websocket",
            EventType: eventType,
            Level: level,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow));
    }
}

/// <summary>飞书分块消息缓冲区（对应官方 SDK DataCache.mergeData）。</summary>
internal sealed class ChunkBuffer
{
    private readonly byte[]?[] _chunks;
    private int _received;

    public ChunkBuffer(int total) => _chunks = new byte[total][];

    public void Set(int seq, byte[] data)
    {
        if (seq < 0 || seq >= _chunks.Length) return;
        if (_chunks[seq] is not null) return; // 已收到
        _chunks[seq] = data;
        Interlocked.Increment(ref _received);
    }

    public bool IsComplete => _received >= _chunks.Length;

    public byte[] Assemble()
    {
        var total = _chunks.Sum(c => c?.Length ?? 0);
        var result = new byte[total];
        var offset = 0;
        foreach (var chunk in _chunks)
        {
            if (chunk is null) continue;
            chunk.CopyTo(result, offset);
            offset += chunk.Length;
        }
        return result;
    }
}

/// <summary>飞书 protobuf 帧解码结果（内部使用）。</summary>
internal sealed class FeishuProtoFrame
{
    public FeishuProtoFrame(
        int service,
        int method,
        List<(string Key, string Value)> headers,
        byte[]? payload)
    {
        Service = service;
        Method  = method;
        Headers = headers;
        Payload = payload;
    }

    public int Service { get; }
    public int Method  { get; }
    public IReadOnlyList<(string Key, string Value)> Headers { get; }
    public byte[]? Payload { get; }
}
