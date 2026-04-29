using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using KodaClaw.Contracts.Browser;

namespace KodaClaw.BrowserHub.Device;

/// <summary>
/// Authentication result returned by <see cref="DeviceAuthService.AuthenticateReconnectAsync"/>.
/// </summary>
public sealed record DeviceAuthResult(
    bool Success,
    string? SessionToken = null,
    string? Error = null,
    string? ErrorCode = null);

/// <summary>
/// Provides HMAC-SHA256 based reconnect authentication for paired browser devices.
/// Implements nonce anti-replay, timestamp validation, consecutive failure tracking,
/// and automatic device lockout.
/// </summary>
/// <remarks>
/// The authentication flow verifies the extension's identity using a shared key
/// derived during the initial Ed25519 pairing (X25519 ECDH + HKDF-SHA256).
///
/// Security parameters:
/// <list type="bullet">
///   <item><description>Timestamp window: ±60 seconds from server time.</description></item>
///   <item><description>Nonce expiry: 5 minutes (auto-cleaned).</description></item>
///   <item><description>Consecutive failure threshold: 5 → device lockout for 15 minutes.</description></item>
/// </list>
/// </remarks>
public sealed class DeviceAuthService
{
    // ── Security constants ──────────────────────────────────────────────────

    /// <summary>Maximum allowed timestamp drift in seconds.</summary>
    public const int TimestampWindowSeconds = 60;

    /// <summary>Duration for which a used nonce is retained to prevent replay.</summary>
    public const int NonceExpirySeconds = 300;

    /// <summary>Number of consecutive failures before device lockout.</summary>
    public const int MaxConsecutiveFailures = 5;

    /// <summary>Duration of device lockout after exceeding failure threshold.</summary>
    public const int LockoutDurationMinutes = 15;

    // ── Private state ───────────────────────────────────────────────────────

    private readonly BrowserDeviceStore _store;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _usedNonces = new();
    private readonly TimeSpan _nonceExpiry = TimeSpan.FromSeconds(NonceExpirySeconds);
    private readonly TimeSpan _lockoutDuration = TimeSpan.FromMinutes(LockoutDurationMinutes);
    private DateTimeOffset _lastNonceCleanup = DateTimeOffset.UtcNow;

    // ── Constructor ─────────────────────────────────────────────────────────

    /// <summary>
    /// Initializes a new instance of <see cref="DeviceAuthService"/>.
    /// </summary>
    /// <param name="store">
    /// The <see cref="BrowserDeviceStore"/> used to look up device records
    /// and update authentication state.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="store"/> is <c>null</c>.
    /// </exception>
    public DeviceAuthService(BrowserDeviceStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Authenticates a device attempting to reconnect via WebSocket.
    /// </summary>
    /// <remarks>
    /// Verification steps:
    /// <list type="number">
    ///   <item><description>Look up device; verify not expired, not revoked, not locked out.</description></item>
    ///   <item><description>Verify <c>nonce</c> has not been used (anti-replay, 5-minute window).</description></item>
    ///   <item><description>Verify <c>timestamp</c> is within ±60 seconds of server time.</description></item>
    ///   <item><description>Compute HMAC-SHA256(sharedKey, deviceId + timestamp + nonce) and compare.</description></item>
    ///   <item><description>On success: record nonce, update lastSeenAt, reset consecutiveFailures.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="request">The authentication request from the extension.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="DeviceAuthResult"/> indicating success or failure with details.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="request"/> is <c>null</c>.
    /// </exception>
    public async Task<DeviceAuthResult> AuthenticateReconnectAsync(
        BridgeAuthRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ── Step 1: Look up device ──────────────────────────────────────
        if (!_store.TryGetInternalDevice(request.DeviceId, out var record) || record is null)
        {
            return Fail("BRIDGE_006", "Device not registered.");
        }

        var now = DateTimeOffset.UtcNow;

        // Check device expiration
        if (record.ExpiresAt < now)
        {
            return Fail("BRIDGE_006", "Device has expired.");
        }

        // Check revocation
        if (record.Revoked)
        {
            return Fail("BRIDGE_006", "Device has been revoked.");
        }

        // Check lockout from consecutive failures
        if (record.ConsecutiveFailures >= MaxConsecutiveFailures)
        {
            // Use lastSeenAt as proxy for when the lockout started
            var lockoutStart = record.LastSeenAt ?? now;
            var lockoutEnd = lockoutStart.Add(_lockoutDuration);

            if (now < lockoutEnd)
            {
                var remaining = lockoutEnd - now;
                return Fail(
                    "BRIDGE_006",
                    $"Device is locked out due to too many failures. " +
                    $"Retry in {Math.Ceiling(remaining.TotalMinutes)} minute(s).");
            }

            // Lockout period has elapsed — allow retry
        }

        // ── Step 2: Verify nonce (anti-replay) ─────────────────────────
        CleanupExpiredNonces(now);

        if (string.IsNullOrWhiteSpace(request.Nonce))
        {
            await IncrementFailuresAsync(record.DeviceId, cancellationToken)
                .ConfigureAwait(false);
            return Fail("BRIDGE_006", "Nonce is required.");
        }

        if (!_usedNonces.TryAdd(request.Nonce, now))
        {
            await IncrementFailuresAsync(record.DeviceId, cancellationToken)
                .ConfigureAwait(false);
            return Fail("BRIDGE_006", "Replay attack detected: nonce already used.");
        }

        // ── Step 3: Verify timestamp ───────────────────────────────────
        var requestTime = DateTimeOffset.FromUnixTimeMilliseconds(request.Timestamp);
        var timeDelta = Math.Abs((now - requestTime).TotalSeconds);

        if (timeDelta > TimestampWindowSeconds)
        {
            // Remove the nonce we just recorded — let the client retry with a fresh one
            _usedNonces.TryRemove(request.Nonce, out _);

            await IncrementFailuresAsync(record.DeviceId, cancellationToken)
                .ConfigureAwait(false);
            return Fail(
                "BRIDGE_006",
                $"Timestamp drift too large ({timeDelta:F0}s). Check clock synchronization.");
        }

        // ── Step 4: Verify HMAC-SHA256 signature ──────────────────────
        if (!VerifyHmac(record.SharedKey, request))
        {
            // Remove nonce to allow honest retry
            _usedNonces.TryRemove(request.Nonce, out _);

            await IncrementFailuresAsync(record.DeviceId, cancellationToken)
                .ConfigureAwait(false);
            return Fail("BRIDGE_006", "Authentication failed: invalid signature.");
        }

        // ── Step 5: Success — update device state ──────────────────────
        await _store.UpdateLastSeenAsync(record.DeviceId, cancellationToken)
            .ConfigureAwait(false);

        var sessionToken = GenerateSessionToken();

        return new DeviceAuthResult(
            Success: true,
            SessionToken: sessionToken);
    }

    // ── HMAC verification ───────────────────────────────────────────────────

    /// <summary>
    /// Computes the expected HMAC-SHA256 signature and compares it with
    /// the value provided in the auth request using constant-time comparison.
    /// </summary>
    /// <param name="sharedKeyBase64">
    /// Base64-encoded shared key (derived during pairing via ECDH + HKDF).
    /// </param>
    /// <param name="request">The authentication request containing the claimed HMAC.</param>
    /// <returns>
    /// <c>true</c> if the HMAC matches; <c>false</c> otherwise.
    /// </returns>
    private static bool VerifyHmac(string sharedKeyBase64, BridgeAuthRequest request)
    {
        try
        {
            var keyBytes = Convert.FromBase64String(sharedKeyBase64);
            var message = $"{request.DeviceId}{request.Timestamp}{request.Nonce}";
            var messageBytes = Encoding.UTF8.GetBytes(message);

            using var hmac = new HMACSHA256(keyBytes);
            var computedHash = hmac.ComputeHash(messageBytes);
            var computedBase64Url = Base64UrlEncode(computedHash);

            // Constant-time comparison to prevent timing attacks
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(computedBase64Url),
                Encoding.UTF8.GetBytes(request.Hmac));
        }
        catch
        {
            // Invalid base64 key or other crypto error → reject silently
            return false;
        }
    }

    // ── Session token generation ────────────────────────────────────────────

    /// <summary>
    /// Generates a cryptographically random session token (32 bytes, base64url-encoded).
    /// </summary>
    /// <returns>A URL-safe base64-encoded random token string.</returns>
    private static string GenerateSessionToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    // ── Failure tracking ────────────────────────────────────────────────────

    /// <summary>
    /// Increments the consecutive failure counter for a device.
    /// If the threshold (<see cref="MaxConsecutiveFailures"/>) is reached,
    /// the device is revoked to enforce lockout.
    /// </summary>
    /// <param name="deviceId">The device identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task IncrementFailuresAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        try
        {
            var newCount = await _store.IncrementConsecutiveFailuresAsync(
                deviceId, cancellationToken).ConfigureAwait(false);

            if (newCount >= MaxConsecutiveFailures)
            {
                // Revoke device to enforce lockout
                await _store.RevokeDeviceAsync(deviceId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Failure tracking is best-effort; don't fail auth on tracking error
        }
    }

    // ── Nonce cleanup ───────────────────────────────────────────────────────

    /// <summary>
    /// Removes expired nonce entries from the anti-replay cache.
    /// Runs at most once per minute to avoid frequent collection overhead.
    /// </summary>
    /// <param name="now">The current server time.</param>
    private void CleanupExpiredNonces(DateTimeOffset now)
    {
        // Throttle cleanup to once per minute
        if ((now - _lastNonceCleanup).TotalMinutes < 1)
        {
            return;
        }

        _lastNonceCleanup = now;

        var expiredKeys = _usedNonces
            .Where(kvp => now - kvp.Value > _nonceExpiry)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _usedNonces.TryRemove(key, out _);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a failed <see cref="DeviceAuthResult"/>.
    /// </summary>
    private static DeviceAuthResult Fail(string code, string error) =>
        new(Success: false, ErrorCode: code, Error: error);

    /// <summary>
    /// Encodes a byte array as a base64url string (RFC 4648 §5).
    /// Replaces <c>+</c> with <c>-</c> and <c>/</c> with <c>_</c>, then trims padding.
    /// </summary>
    /// <param name="bytes">The bytes to encode.</param>
    /// <returns>A base64url-encoded string.</returns>
    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
