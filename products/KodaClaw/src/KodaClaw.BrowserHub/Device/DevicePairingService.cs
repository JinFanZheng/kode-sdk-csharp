using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KodaClaw.Contracts.Browser;

namespace KodaClaw.BrowserHub.Device;

/// <summary>
/// Represents the current state of an in-flight pairing session.
/// Tracks the ephemeral keys, challenge, and token metadata.
/// </summary>
/// <param name="Token">
/// The one-time pairing token (32-char URL-safe base64).
/// </param>
/// <param name="Challenge">
/// The 32-byte random challenge sent to the extension for signing.
/// </param>
/// <param name="GatewayEcdsaPrivateKey">
/// The Gateway's ephemeral ECDSA P-256 private key used to sign the challenge.
/// </param>
/// <param name="GatewayEcdsaPublicKey">
/// The Gateway's ephemeral ECDSA P-256 public key (SubjectPublicKeyInfo) sent to the extension.
/// </param>
/// <param name="GatewayEcdhPrivateKey">
/// The Gateway's ephemeral ECDH P-256 private key for key agreement.
/// </param>
/// <param name="GatewayEcdhPublicKey">
/// The Gateway's ephemeral ECDH P-256 public key (SubjectPublicKeyInfo) exchanged with the extension.
/// </param>
/// <param name="CreatedAt">
/// UTC timestamp when this session was created.
/// </param>
internal sealed record PairingSession(
    string Token,
    byte[] Challenge,
    byte[] GatewayEcdsaPrivateKey,
    byte[] GatewayEcdsaPublicKey,
    byte[] GatewayEcdhPrivateKey,
    byte[] GatewayEcdhPublicKey,
    DateTimeOffset CreatedAt);

/// <summary>
/// Result returned upon successful device pairing.
/// Contains the device record, session token, and shared key.
/// </summary>
/// <param name="Device">
/// The public <see cref="BrowserDevice"/> DTO for the paired device.
/// </param>
/// <param name="SessionToken">
/// Base64url-encoded session token (32 bytes) for subsequent reconnections.
/// Valid for 24 hours.
/// </param>
/// <param name="SharedKey">
/// Base64-encoded shared symmetric key derived via HKDF-SHA256.
/// </param>
public sealed record PairingResult(
    BrowserDevice Device,
    string SessionToken,
    string SharedKey);

/// <summary>
/// Challenge data returned to the Chrome Extension during pairing.
/// All fields are base64-encoded.
/// </summary>
/// <param name="Challenge">The 32-byte random challenge (base64).</param>
/// <param name="GwEcdsaPubKey">Gateway's ephemeral ECDSA P-256 public key (base64, SubjectPublicKeyInfo DER).</param>
/// <param name="GwEcdhPubKey">Gateway's ephemeral ECDH P-256 public key (base64, SubjectPublicKeyInfo DER).</param>
/// <param name="GwSignature">Gateway's ECDSA P-256 signature of the challenge (base64, DER-encoded).</param>
public sealed record PairingChallengeData(
    string Challenge,
    string GwEcdsaPubKey,
    string GwEcdhPubKey,
    string GwSignature);

/// <summary>
/// Implements the 10-step ECDSA/ECDH device pairing protocol between
/// the Chrome Extension and the Gateway.
///
/// <para>
/// Pairing flow overview:
/// <list type="number">
///   <item>Gateway generates pairing Token (UUID + 5-min TTL).</item>
///   <item>Extension initiates pair request with Token.</item>
///   <item>Gateway validates Token有效性.</item>
///   <item>Gateway generates challenge (random 32 bytes).</item>
///   <item>Extension signs challenge with ECDSA P-256.</item>
///   <item>Gateway verifies signature.</item>
///   <item>Both sides exchange ECDSA P-256 public keys.</item>
///   <item>ECDH P-256 derives shared secret.</item>
///   <item>HKDF-SHA256 extracts symmetric encryption key.</item>
///   <item>Store deviceInfo to DeviceStore.</item>
/// </list>
/// </para>
///
/// <para>
/// Token storage: <c>ConcurrentDictionary&lt;string, PairingSession&gt;</c>,
/// in-memory, 5-minute automatic expiry.
/// </para>
///
/// <para>
/// Cryptographic primitives: ECDSA P-256 for signing/verification,
/// ECDH P-256 for key agreement, HKDF-SHA256 for key derivation.
/// </para>
/// </summary>
public sealed class DevicePairingService
{
    // ─── Constants ─────────────────────────────────────────────────────

    /// <summary>
    /// Pairing token time-to-live. Tokens expire after this duration.
    /// </summary>
    private const int PairingTokenTtlMinutes = 5;

    /// <summary>
    /// Session token time-to-live for post-pairing reconnections.
    /// </summary>
    private const int SessionTokenTtlHours = 24;

    /// <summary>
    /// Length of the random challenge in bytes.
    /// </summary>
    private const int ChallengeLengthBytes = 32;

    /// <summary>
    /// Length of the session token in bytes.
    /// </summary>
    private const int SessionTokenLengthBytes = 32;

    /// <summary>
    /// HKDF salt used for shared key derivation.
    /// </summary>
    private const string HkdfSalt = "kodaclaw-bridge-v1";

    /// <summary>
    /// HKDF output length (symmetric key size) in bytes.
    /// </summary>
    private const int HkdfKeyLength = 32;

    /// <summary>
    /// Token character count for pairing tokens (URL-safe base64 from 24 random bytes).
    /// </summary>
    private const int PairingTokenByteLength = 24;

    /// <summary>
    /// Interval between cleanup sweeps for expired pairing sessions.
    /// </summary>
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);

    // ─── Private fields ────────────────────────────────────────────────

    /// <summary>
    /// In-memory store of active pairing sessions keyed by token.
    /// Sessions are removed after use or on expiry.
    /// </summary>
    private readonly ConcurrentDictionary<string, PairingSession> _pendingSessions = new();

    /// <summary>
    /// Persistent device store for saving paired device records.
    /// </summary>
    private readonly BrowserDeviceStore _deviceStore;

    /// <summary>
    /// Timer for periodic cleanup of expired pairing sessions.
    /// </summary>
    private readonly Timer _cleanupTimer;

    // ─── JSON options ──────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    // ─── Constructor ───────────────────────────────────────────────────

    /// <summary>
    /// Initializes a new instance of the <see cref="DevicePairingService"/> class.
    /// </summary>
    /// <param name="deviceStore">
    /// The <see cref="BrowserDeviceStore"/> used to persist paired devices.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="deviceStore"/> is <c>null</c>.
    /// </exception>
    public DevicePairingService(BrowserDeviceStore deviceStore)
    {
        _deviceStore = deviceStore ?? throw new ArgumentNullException(nameof(deviceStore));

        // Start periodic cleanup of expired pairing sessions
        _cleanupTimer = new Timer(
            _ => CleanupExpiredSessions(),
            state: null,
            dueTime: CleanupInterval,
            period: CleanupInterval);
    }

    // ─── Step 1: Generate Pairing Token ────────────────────────────────

    /// <summary>
    /// Step 1 — Gateway generates a one-time pairing token.
    /// The token is a URL-safe base64-encoded random string with a 5-minute TTL.
    /// A <see cref="PairingSession"/> is created and stored in memory,
    /// pre-loaded with the challenge and ephemeral key pairs.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A tuple containing the pairing token and the 32-byte challenge
    /// (base64-encoded) to be displayed to the user.
    /// </returns>
    public (string Token, string Challenge) GeneratePairingToken(
        CancellationToken cancellationToken = default)
    {
        // Step 1: Generate a cryptographically random pairing token
        var tokenBytes = RandomNumberGenerator.GetBytes(PairingTokenByteLength);
        var token = Convert.ToBase64String(tokenBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        // Step 4 (pre-generate): Generate random 32-byte challenge
        var challenge = RandomNumberGenerator.GetBytes(ChallengeLengthBytes);

        // Pre-generate Gateway's ephemeral ECDSA P-256 key pair (for challenge signing)
        using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var gatewayEcdsaPrivateKey = ecdsa.ExportPkcs8PrivateKey();
            var gatewayEcdsaPublicKey = ecdsa.ExportSubjectPublicKeyInfo();

            // Pre-generate Gateway's ephemeral ECDH P-256 key pair (for key agreement)
            using (var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256))
            {
                var gatewayEcdhPrivateKey = ecdh.ExportPkcs8PrivateKey();
                var gatewayEcdhPublicKey = ecdh.ExportSubjectPublicKeyInfo();

                // Create and store the pairing session
                var session = new PairingSession(
                    Token: token,
                    Challenge: challenge,
                    GatewayEcdsaPrivateKey: gatewayEcdsaPrivateKey,
                    GatewayEcdsaPublicKey: gatewayEcdsaPublicKey,
                    GatewayEcdhPrivateKey: gatewayEcdhPrivateKey,
                    GatewayEcdhPublicKey: gatewayEcdhPublicKey,
                    CreatedAt: DateTimeOffset.UtcNow);

                _pendingSessions[token] = session;
            }
        }

        return (token, Convert.ToBase64String(challenge));
    }

    // ─── Steps 2–10: Complete Pairing ──────────────────────────────────

    /// <summary>
    /// Steps 2–10 — Completes the pairing handshake initiated by the extension.
    ///
    /// <para>This method executes the following steps:</para>
    /// <list type="number">
    ///   <item><description>Step 2: Extension submits pair request with token.</description></item>
    ///   <item><description>Step 3: Gateway validates token validity and TTL.</description></item>
    ///   <item><description>Step 5: Extension provides ECDSA P-256 public key.</description></item>
    ///   <item><description>Step 6: Extension signs the challenge with its ECDSA P-256 private key.</description></item>
    ///   <item><description>Step 7: Both sides exchange ECDSA P-256 public keys.</description></item>
    ///   <item><description>Step 8: ECDH P-256 derives shared secret.</description></item>
    ///   <item><description>Step 9: HKDF-SHA256 extracts symmetric encryption key.</description></item>
    ///   <item><description>Step 10: Store deviceInfo to DeviceStore.</description></item>
    /// </list>
    /// </summary>
    /// <param name="pairingToken">
    /// The one-time pairing token obtained from <see cref="GeneratePairingToken"/>.
    /// </param>
    /// <param name="extensionPublicKey">
    /// Base64-encoded ECDSA P-256 public key from the extension (SubjectPublicKeyInfo DER).
    /// </param>
    /// <param name="challengeSignature">
    /// Base64-encoded ECDSA P-256 signature of the challenge (DER-encoded).
    /// </param>
    /// <param name="extensionEcdhPublicKey">
    /// Base64-encoded ECDH P-256 public key from the extension (SubjectPublicKeyInfo DER).
    /// </param>
    /// <param name="deviceId">
    /// UUID v4 device identifier generated by the extension.
    /// </param>
    /// <param name="label">
    /// Optional human-readable label for the device.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A <see cref="PairingResult"/> containing the device record,
    /// session token, and shared key.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when any input parameter is null, empty, or malformed.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the pairing token is invalid, expired, or already used.
    /// </exception>
    /// <exception cref="CryptographicException">
    /// Thrown when the ECDSA signature verification fails.
    /// </exception>
    public async Task<PairingResult> CompletePairingAsync(
        string pairingToken,
        string extensionPublicKey,
        string challengeSignature,
        string extensionEcdhPublicKey,
        string deviceId,
        string? label = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionPublicKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeSignature);
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionEcdhPublicKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        // ─── Step 3: Validate token validity and TTL ──────────────────
        if (!_pendingSessions.TryRemove(pairingToken, out var session))
        {
            throw new InvalidOperationException(
                "PAIRING_INVALID_TOKEN: Pairing token not found or already used.");
        }

        if (DateTimeOffset.UtcNow - session.CreatedAt > TimeSpan.FromMinutes(PairingTokenTtlMinutes))
        {
            throw new InvalidOperationException(
                "PAIRING_TOKEN_EXPIRED: Pairing token has expired.");
        }

        // Token is one-time use — already removed via TryRemove above.

        // ─── Step 5 & 7: Decode extension's ECDSA P-256 public key ───
        var extEcdsaPubKeyDer = Convert.FromBase64String(extensionPublicKey);

        // ─── Step 6: Verify extension's signature of the challenge ────
        var signature = Convert.FromBase64String(challengeSignature);

        bool isSignatureValid;
        using (var extEcdsa = ECDsa.Create())
        {
            extEcdsa.ImportSubjectPublicKeyInfo(extEcdsaPubKeyDer, out _);
            isSignatureValid = extEcdsa.VerifyData(
                session.Challenge,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }

        if (!isSignatureValid)
        {
            throw new CryptographicException(
                "PAIRING_SIGNATURE_INVALID: ECDSA P-256 signature verification failed. " +
                "The extension's private key does not match the provided public key.");
        }

        // ─── Step 7: Exchange ECDSA P-256 public keys ─────────────────
        // The extension's public key is already received above.
        // The Gateway's public key is returned as part of the pairing result.
        var gatewayEcdsaPubKeyBase64 = Convert.ToBase64String(session.GatewayEcdsaPublicKey);

        // ─── Step 8: ECDH P-256 — Derive shared secret ────────────────
        var extEcdhPubKeyDer = Convert.FromBase64String(extensionEcdhPublicKey);

        byte[] sharedSecret;
        using (var gatewayEcdh = ECDiffieHellman.Create())
        {
            gatewayEcdh.ImportPkcs8PrivateKey(session.GatewayEcdhPrivateKey, out _);

            using (var extEcdh = ECDiffieHellman.Create())
            {
                extEcdh.ImportSubjectPublicKeyInfo(extEcdhPubKeyDer, out _);
                sharedSecret = gatewayEcdh.DeriveKeyFromHash(
                    extEcdh.PublicKey,
                    HashAlgorithmName.SHA256,
                    secretPrepend: null,
                    secretAppend: null);
            }
        }

        // ─── Step 9: HKDF-SHA256 — Extract symmetric encryption key ───
        // shared_key = HKDF-SHA256(
        //     salt = "kodaclaw-bridge-v1",
        //     ikm  = shared_secret,
        //     info = device_id,
        //     L    = 32)
        var saltBytes = Encoding.UTF8.GetBytes(HkdfSalt);
        var infoBytes = Encoding.UTF8.GetBytes(deviceId);

        var prk = HKDF.Extract(HashAlgorithmName.SHA256, sharedSecret, saltBytes);
        var sharedKey = new byte[HkdfKeyLength];
        HKDF.Expand(HashAlgorithmName.SHA256, prk, sharedKey, infoBytes);

        var sharedKeyBase64 = Convert.ToBase64String(sharedKey);

        // ─── Step 10: Store device info to DeviceStore ────────────────
        var extPublicKeyBase64 = Convert.ToBase64String(extEcdsaPubKeyDer);

        var device = await _deviceStore.AddDeviceAsync(
            deviceId: deviceId,
            publicKey: extPublicKeyBase64,
            sharedKey: sharedKeyBase64,
            label: label,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // ─── Generate session token for subsequent reconnections ───────
        var sessionTokenBytes = RandomNumberGenerator.GetBytes(SessionTokenLengthBytes);
        var sessionToken = Convert.ToBase64String(sessionTokenBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        return new PairingResult(
            Device: device,
            SessionToken: sessionToken,
            SharedKey: sharedKeyBase64);
    }

    // ─── Public Helpers ────────────────────────────────────────────────

    /// <summary>
    /// Gets the Gateway's ECDSA P-256 public key and signature for a given pairing token.
    /// Called after the extension verifies its own challenge signature.
    ///
    /// <para>
    /// The Gateway signs the challenge with its ephemeral ECDSA P-256 private key
    /// so the extension can verify the Gateway's identity.
    /// </para>
    /// </summary>
    /// <param name="pairingToken">The active pairing token.</param>
    /// <returns>
    /// A tuple of (GatewayEcdsaPublicKey, ChallengeSignature), both base64-encoded.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the token is not found or expired.
    /// </exception>
    public (string GatewayPublicKey, string Signature) GetGatewaySignature(string pairingToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingToken);

        if (!_pendingSessions.TryGetValue(pairingToken, out var session))
        {
            throw new InvalidOperationException(
                "PAIRING_INVALID_TOKEN: Pairing token not found.");
        }

        if (DateTimeOffset.UtcNow - session.CreatedAt > TimeSpan.FromMinutes(PairingTokenTtlMinutes))
        {
            throw new InvalidOperationException(
                "PAIRING_TOKEN_EXPIRED: Pairing token has expired.");
        }

        // Gateway signs the challenge with its ephemeral ECDSA P-256 private key
        byte[] gatewaySignature;
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportPkcs8PrivateKey(session.GatewayEcdsaPrivateKey, out _);
            gatewaySignature = ecdsa.SignData(
                session.Challenge,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }

        return (
            Convert.ToBase64String(session.GatewayEcdsaPublicKey),
            Convert.ToBase64String(gatewaySignature));
    }

    /// <summary>
    /// Returns all four fields the Chrome Extension needs to complete pairing:
    /// the challenge, the Gateway's ECDSA public key, the Gateway's ECDH public key,
    /// and the Gateway's signature of the challenge.
    /// </summary>
    /// <param name="pairingToken">The active pairing token.</param>
    /// <returns>
    /// A <see cref="PairingChallengeData"/> with all fields base64-encoded.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the token is not found or has expired.
    /// </exception>
    public PairingChallengeData GetPairingChallenge(string pairingToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingToken);

        if (!_pendingSessions.TryGetValue(pairingToken, out var session))
        {
            throw new InvalidOperationException(
                "PAIRING_INVALID_TOKEN: Pairing token not found.");
        }

        if (DateTimeOffset.UtcNow - session.CreatedAt > TimeSpan.FromMinutes(PairingTokenTtlMinutes))
        {
            throw new InvalidOperationException(
                "PAIRING_TOKEN_EXPIRED: Pairing token has expired.");
        }

        byte[] gatewaySignature;
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportPkcs8PrivateKey(session.GatewayEcdsaPrivateKey, out _);
            gatewaySignature = ecdsa.SignData(
                session.Challenge,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }

        return new PairingChallengeData(
            Challenge: Convert.ToBase64String(session.Challenge),
            GwEcdsaPubKey: Convert.ToBase64String(session.GatewayEcdsaPublicKey),
            GwEcdhPubKey: Convert.ToBase64String(session.GatewayEcdhPublicKey),
            GwSignature: Convert.ToBase64String(gatewaySignature));
    }

    /// <summary>
    /// Validates whether a pairing token exists and has not expired,
    /// without consuming it. Used for pre-flight checks.
    /// </summary>
    /// <param name="pairingToken">The pairing token to validate.</param>
    /// <returns>
    /// <c>true</c> if the token exists and is within TTL; <c>false</c> otherwise.
    /// </returns>
    public bool IsPairingTokenValid(string pairingToken)
    {
        if (string.IsNullOrWhiteSpace(pairingToken))
        {
            return false;
        }

        if (!_pendingSessions.TryGetValue(pairingToken, out var session))
        {
            return false;
        }

        return DateTimeOffset.UtcNow - session.CreatedAt <= TimeSpan.FromMinutes(PairingTokenTtlMinutes);
    }

    // ─── Cleanup ───────────────────────────────────────────────────────

    /// <summary>
    /// Removes expired pairing sessions from the in-memory dictionary.
    /// Called periodically by the cleanup timer.
    /// </summary>
    private void CleanupExpiredSessions()
    {
        var expiryThreshold = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(PairingTokenTtlMinutes);

        foreach (var kvp in _pendingSessions)
        {
            if (kvp.Value.CreatedAt < expiryThreshold)
            {
                _pendingSessions.TryRemove(kvp.Key, out _);
            }
        }
    }

    // ─── JSON Options ──────────────────────────────────────────────────

    /// <summary>
    /// Creates the <see cref="JsonSerializerOptions"/> instance consistent
    /// with the project convention: <see cref="JsonSerializerDefaults.Web"/>
    /// with camelCase naming and string enum conversion.
    /// </summary>
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
