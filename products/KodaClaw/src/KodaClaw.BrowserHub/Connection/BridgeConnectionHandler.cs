using System.Text.Json;
using System.Text.Json.Serialization;
using KodaClaw.BrowserHub.Device;
using KodaClaw.Contracts.Browser;

namespace KodaClaw.BrowserHub.Connection;

/// <summary>
/// Processes inbound WebSocket messages from browser extension devices and
/// builds outbound <see cref="BridgeRequest"/> payloads.
///
/// <para>
/// Inbound routing:
/// <list type="bullet">
///   <item><description><see cref="BridgeResponse"/> — matched to a pending request via correlation ID
///     and delivered through <see cref="OnResponse"/>.</description></item>
///   <item><description><see cref="BridgeEvent"/> type <c>heartbeat</c> — updates the device
///     last-seen timestamp via <see cref="BrowserDeviceStore"/>.</description></item>
///   <item><description><see cref="BridgeAuthRequest"/> — routed to <see cref="DeviceAuthService"/>
///     and a session-token reply is returned to the caller.</description></item>
/// </list>
/// </para>
///
/// <para>
/// Outbound: <see cref="BuildRequestJson"/> serialises a <see cref="BridgeRequest"/> to the
/// wire format understood by the extension.
/// </para>
/// </summary>
public sealed class BridgeConnectionHandler
{
    private const string ProtocolVersion = "1.0";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly BrowserDeviceStore _deviceStore;
    private readonly DeviceAuthService _authService;
    private readonly BridgeConnectionManager _connectionManager;

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised when a <see cref="BridgeResponse"/> is received from a device.
    /// Arguments are the device identifier and the deserialized response.
    /// </summary>
    public event Action<string, BridgeResponse>? OnResponse;

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the handler and subscribes to incoming messages from
    /// <paramref name="connectionManager"/>.
    /// </summary>
    /// <param name="connectionManager">The active connection manager.</param>
    /// <param name="deviceStore">Persistent device store.</param>
    /// <param name="authService">HMAC reconnect auth service.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any parameter is <c>null</c>.
    /// </exception>
    public BridgeConnectionHandler(
        BridgeConnectionManager connectionManager,
        BrowserDeviceStore deviceStore,
        DeviceAuthService authService)
    {
        ArgumentNullException.ThrowIfNull(connectionManager);
        ArgumentNullException.ThrowIfNull(deviceStore);
        ArgumentNullException.ThrowIfNull(authService);

        _connectionManager = connectionManager;
        _deviceStore = deviceStore;
        _authService = authService;

        _connectionManager.OnMessageReceived += HandleMessageAsync;
    }

    // ── Outbound helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Serialises a <see cref="BridgeRequest"/> to the JSON wire format.
    /// </summary>
    /// <param name="action">The action name (e.g. <c>navigate</c>, <c>screenshot</c>).</param>
    /// <param name="sessionId">The active Agent session identifier.</param>
    /// <param name="payload">Action-specific parameters object.</param>
    /// <param name="tabId">Optional target tab ID; <c>null</c> means auto-assign.</param>
    /// <param name="timeoutMs">Client-declared timeout in milliseconds.</param>
    /// <returns>
    /// A tuple of the request ID (UUID v4) and the serialised JSON string.
    /// </returns>
    public (string RequestId, string Json) BuildRequestJson(
        string action,
        string sessionId,
        object? payload = null,
        string? tabId = null,
        int timeoutMs = 30000)
    {
        var requestId = Guid.NewGuid().ToString();
        var request = new BridgeRequest(
            Version: ProtocolVersion,
            Id: requestId,
            SessionId: sessionId,
            Action: action,
            TabId: tabId,
            Payload: payload,
            TimeoutMs: timeoutMs);

        var json = JsonSerializer.Serialize(request, JsonOptions);
        return (requestId, json);
    }

    // ── Inbound dispatch ──────────────────────────────────────────────────────

    /// <summary>
    /// Dispatches a raw JSON message received from a device.
    /// Called by <see cref="BridgeConnectionManager.OnMessageReceived"/>.
    /// Returns a non-null reply JSON only when an immediate response must be
    /// sent back (e.g. for auth handshake); returns <c>null</c> otherwise.
    /// </summary>
    /// <param name="deviceId">The originating device identifier.</param>
    /// <param name="rawJson">The raw JSON text message.</param>
    private async Task HandleMessageAsync(string deviceId, string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            // Discriminate message type by presence of unique fields:
            //   BridgeAuthRequest: has "hmac"
            //   BridgeEvent:       has "eventId"
            //   BridgeResponse:    has "ok"

            if (root.TryGetProperty("hmac", out _))
            {
                var authRequest = JsonSerializer.Deserialize<BridgeAuthRequest>(rawJson, JsonOptions);
                if (authRequest is not null)
                {
                    await HandleAuthAsync(deviceId, authRequest).ConfigureAwait(false);
                }
            }
            else if (root.TryGetProperty("eventId", out _))
            {
                var bridgeEvent = JsonSerializer.Deserialize<BridgeEvent>(rawJson, JsonOptions);
                if (bridgeEvent is not null)
                {
                    await HandleEventAsync(deviceId, bridgeEvent).ConfigureAwait(false);
                }
            }
            else if (root.TryGetProperty("ok", out _))
            {
                var response = JsonSerializer.Deserialize<BridgeResponse>(rawJson, JsonOptions);
                if (response is not null)
                {
                    HandleResponse(deviceId, response);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Malformed or unrecognised message — discard silently
        }
    }

    // ── BridgeAuthRequest handling ────────────────────────────────────────────

    /// <summary>
    /// Authenticates the device using HMAC-SHA256 and sends the session token
    /// (or an error) back over the WebSocket.
    /// </summary>
    private async Task HandleAuthAsync(string deviceId, BridgeAuthRequest request)
    {
        var result = await _authService
            .AuthenticateReconnectAsync(request)
            .ConfigureAwait(false);

        if (result.Success && result.SessionToken is not null)
        {
            var ok = new BridgeResponse(
                Version: ProtocolVersion,
                Id: request.Nonce,
                Ok: true,
                Data: new { sessionToken = result.SessionToken });
            var replyJson = JsonSerializer.Serialize(ok, JsonOptions);
            await _connectionManager.SendToDeviceAsync(deviceId, replyJson).ConfigureAwait(false);
        }
        else
        {
            var fail = new BridgeResponse(
                Version: ProtocolVersion,
                Id: request.Nonce,
                Ok: false,
                Error: $"{result.ErrorCode}: {result.Error}");
            var replyJson = JsonSerializer.Serialize(fail, JsonOptions);
            await _connectionManager.SendToDeviceAsync(deviceId, replyJson).ConfigureAwait(false);
        }
    }

    // ── BridgeEvent handling ──────────────────────────────────────────────────

    /// <summary>
    /// Handles pushed events from the extension.
    /// Currently processes the <c>heartbeat</c> event by refreshing the device
    /// last-seen timestamp.
    /// </summary>
    private async Task HandleEventAsync(string deviceId, BridgeEvent bridgeEvent)
    {
        if (bridgeEvent.Type == "heartbeat")
        {
            try
            {
                await _deviceStore
                    .UpdateLastSeenAsync(deviceId)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Non-fatal — heartbeat update failure should not disrupt the connection
            }
        }
    }

    // ── BridgeResponse handling ───────────────────────────────────────────────

    /// <summary>
    /// Forwards a received <see cref="BridgeResponse"/> to subscribers of
    /// <see cref="OnResponse"/> so that pending request tasks can be completed.
    /// </summary>
    private void HandleResponse(string deviceId, BridgeResponse response)
    {
        OnResponse?.Invoke(deviceId, response);
    }

    // ── JSON options ──────────────────────────────────────────────────────────

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
