namespace KodaClaw.Contracts.System;

/// <summary>
/// Persisted configuration for the local Gateway stored in config/gateway.json.
/// Sensitive values (tokens) are stored here only as a last-resort fallback;
/// the preferred storage is OS Keychain via <c>GatewayTokenSecretRef</c>.
/// </summary>
public sealed record GatewayConfig
{
    /// <summary>
    /// Optional Bearer token for Gateway API authentication.
    /// Lowest-priority source: Keychain (via TokenSecretRef) and environment
    /// variables take precedence when present.
    /// </summary>
    public string? AccessToken { get; init; }
}
