namespace KodaClaw.Contracts.Browser;

/// <summary>
/// Request (Gateway → Extension).
/// </summary>
public sealed record BridgeRequest(
    string Version,
    string Id,
    string SessionId,
    string Action,
    string? TabId = null,
    object? Payload = null,
    int TimeoutMs = 30000);

/// <summary>
/// Response (Extension → Gateway).
/// </summary>
public sealed record BridgeResponse(
    string Version,
    string Id,
    bool Ok,
    object? Data = null,
    string? Error = null);

/// <summary>
/// Event (Extension → Gateway, pushed proactively).
/// Type: "heartbeat" | "intercept" | "error".
/// </summary>
public sealed record BridgeEvent(
    string Version,
    string EventId,
    string Type,
    long Ts,
    object? Payload = null);

/// <summary>
/// Auth request sent by the extension on first connection.
/// </summary>
public sealed record BridgeAuthRequest(
    string DeviceId,
    long Timestamp,
    string Nonce,
    string Hmac);

/// <summary>
/// Screenshot token issued by the gateway.
/// </summary>
public sealed record ScreenshotTokenPayload(
    string Token,
    long ExpiresAt);
