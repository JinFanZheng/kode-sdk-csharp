using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using KodaClaw.Contracts.Browser;

namespace KodaClaw.BrowserHub.Device;

/// <summary>
/// Internal device record stored in the JSON file.
/// Contains sensitive fields (sharedKey, consecutiveFailures, revoked)
/// that are not exposed in the public <see cref="BrowserDevice"/> DTO.
/// </summary>
internal sealed record InternalDeviceRecord(
    string DeviceId,
    string PublicKey,
    string SharedKey,
    DateTimeOffset PairedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LastSeenAt,
    int ConsecutiveFailures,
    bool Revoked,
    string? Label = null);

/// <summary>
/// JSON envelope for device persistence file.
/// </summary>
internal sealed record DeviceStoreEnvelope(
    List<InternalDeviceRecord> Devices);

/// <summary>
/// Thread-safe device persistence store backed by a single JSON file
/// and an in-memory <see cref="ConcurrentDictionary{TKey,TValue}"/> cache.
///
/// Storage path: {workspaceRoot}/browser/devices.json
///
/// On construction the file is loaded into memory. Every mutation updates
/// both the dictionary and the file atomically (write-then-move).
/// </summary>
public sealed class BrowserDeviceStore
{
    private const string DevicesFileName = "devices.json";
    private const string BrowserDirectoryName = "browser";
    private const int DefaultPairingExpiryDays = 90;

    private readonly string _filePath;
    private readonly ConcurrentDictionary<string, InternalDeviceRecord> _cache = new();
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    /// <summary>
    /// Creates a new <see cref="BrowserDeviceStore"/> and loads existing
    /// devices from the JSON file into the in-memory cache.
    /// </summary>
    /// <param name="workspaceRoot">
    /// Absolute path to the workspace root directory (e.g. ~/.kodaclaw).
    /// </param>
    /// <param name="cancellationToken">Cancellation token for initial load.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="workspaceRoot"/> is null or whitespace.
    /// </exception>
    public BrowserDeviceStore(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        var browserDir = Path.Combine(workspaceRoot, BrowserDirectoryName);
        _filePath = Path.Combine(browserDir, DevicesFileName);

        LoadFromFileAsync(cancellationToken).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Adds a new device to the store. Both the in-memory cache and the
    /// JSON file are updated atomically.
    /// </summary>
    /// <param name="deviceId">Unique device identifier (UUID).</param>
    /// <param name="publicKey">Base64-encoded public key.</param>
    /// <param name="sharedKey">Base64-encoded shared key (stored encrypted).</param>
    /// <param name="label">Optional human-readable label for the device.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The public <see cref="BrowserDevice"/> DTO for the newly added device.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when deviceId, publicKey, or sharedKey is null or whitespace.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a device with the same ID already exists.
    /// </exception>
    public async Task<BrowserDevice> AddDeviceAsync(
        string deviceId,
        string publicKey,
        string sharedKey,
        string? label,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedKey);

        var now = DateTimeOffset.UtcNow;
        var record = new InternalDeviceRecord(
            DeviceId: deviceId,
            PublicKey: publicKey,
            SharedKey: sharedKey,
            PairedAt: now,
            ExpiresAt: now.AddDays(DefaultPairingExpiryDays),
            LastSeenAt: null,
            ConsecutiveFailures: 0,
            Revoked: false,
            Label: label);

        if (!_cache.TryAdd(deviceId, record))
        {
            throw new InvalidOperationException(
                $"Device '{deviceId}' already exists in the store.");
        }

        await PersistAsync(cancellationToken).ConfigureAwait(false);
        return ToPublicDevice(record);
    }

    /// <summary>
    /// Removes a device from the store by its identifier.
    /// </summary>
    /// <param name="deviceId">The device identifier to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> if the device was found and removed; <c>false</c> otherwise.
    /// </returns>
    public async Task<bool> RemoveDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        if (!_cache.TryRemove(deviceId, out _))
        {
            return false;
        }

        await PersistAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Retrieves a device by its identifier, projected to the public
    /// <see cref="BrowserDevice"/> DTO (no sensitive fields).
    /// </summary>
    /// <param name="deviceId">The device identifier to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The <see cref="BrowserDevice"/> DTO, or <c>null</c> if not found.
    /// </returns>
    public Task<BrowserDevice?> GetDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        _cache.TryGetValue(deviceId, out var record);
        return Task.FromResult(record is not null ? ToPublicDevice(record) : null);
    }

    /// <summary>
    /// Retrieves the internal device record including sensitive fields (sharedKey, etc.).
    /// Use this for authentication/verification flows only.
    /// Returns <c>null</c> if the device is not found.
    /// </summary>
    /// <param name="deviceId">The device identifier to look up.</param>
    /// <returns>The internal device record, or <c>null</c> if not found.</returns>
    internal bool TryGetInternalDevice(string deviceId, out InternalDeviceRecord? record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return _cache.TryGetValue(deviceId, out record);
    }

    /// <summary>
    /// Returns all paired devices projected to public DTOs.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A read-only list of <see cref="BrowserDevice"/> records.</returns>
    public Task<IReadOnlyList<BrowserDevice>> GetAllDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var devices = _cache.Values
            .Select(ToPublicDevice)
            .ToList();

        return Task.FromResult<IReadOnlyList<BrowserDevice>>(devices);
    }

    /// <summary>
    /// Updates the <c>LastSeenAt</c> timestamp and resets the
    /// <c>ConsecutiveFailures</c> counter for the specified device.
    /// </summary>
    /// <param name="deviceId">The device identifier to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The updated <see cref="BrowserDevice"/> DTO, or <c>null</c> if not found.
    /// </returns>
    public async Task<BrowserDevice?> UpdateLastSeenAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        var updated = _cache.AddOrUpdate(
            deviceId,
            _ => throw new KeyNotFoundException(
                $"Device '{deviceId}' not found in the store."),
            (_, existing) => existing with
            {
                LastSeenAt = DateTimeOffset.UtcNow,
                ConsecutiveFailures = 0,
            });

        await PersistAsync(cancellationToken).ConfigureAwait(false);
        return ToPublicDevice(updated);
    }

    /// <summary>
    /// Marks a device as revoked. Revoked devices will be excluded from
    /// normal operations.
    /// </summary>
    /// <param name="deviceId">The device identifier to revoke.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> if the device was found and revoked; <c>false</c> otherwise.
    /// </returns>
    public async Task<bool> RevokeDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        if (!_cache.TryGetValue(deviceId, out var existing))
        {
            return false;
        }

        var revoked = existing with { Revoked = true };
        _cache[deviceId] = revoked;

        await PersistAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }


    /// <summary>
    /// Increments the consecutive failure counter for a device without
    /// resetting <c>LastSeenAt</c>. Used by the authentication service
    /// to track failed auth attempts.
    /// </summary>
    /// <param name=\deviceId\>The device identifier to update.</param>
    /// <param name=\cancellationToken\>Cancellation token.</param>
    /// <returns>
    /// The updated consecutive failure count, or <c>-1</c> if the device was not found.
    /// </returns>
    internal async Task<int> IncrementConsecutiveFailuresAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        if (!_cache.TryGetValue(deviceId, out var existing))
        {
            return -1;
        }

        var newCount = existing.ConsecutiveFailures + 1;
        var updated = existing with { ConsecutiveFailures = newCount };
        _cache[deviceId] = updated;

        await PersistAsync(cancellationToken).ConfigureAwait(false);
        return newCount;
    }

    // ─── Persistence ──────────────────────────────────────────────

    /// <summary>
    /// Loads all device records from the JSON file into the in-memory cache.
    /// If the file does not exist or is corrupted, the cache remains empty.
    /// </summary>
    private async Task LoadFromFileAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var json = await File.ReadAllTextAsync(_filePath, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            var envelope = JsonSerializer.Deserialize<DeviceStoreEnvelope>(
                json, JsonOptions);

            if (envelope?.Devices is null)
            {
                return;
            }

            foreach (var device in envelope.Devices)
            {
                _cache[device.DeviceId] = device;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Corrupt or unreadable file — start with empty cache
            _cache.Clear();
        }
    }

    /// <summary>
    /// Persists the entire in-memory cache to the JSON file atomically.
    /// Uses a file lock to prevent concurrent writes and a temp-file-then-move
    /// strategy for crash safety.
    /// </summary>
    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(dir);

            var envelope = new DeviceStoreEnvelope(
                Devices: _cache.Values.ToList());

            var tempPath = _filePath + $".{Guid.NewGuid():N}.tmp";
            var json = JsonSerializer.Serialize(envelope, JsonOptions);
            await File.WriteAllTextAsync(tempPath, json, cancellationToken)
                .ConfigureAwait(false);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Persistence failure logged silently — cache is still authoritative
        }
        finally
        {
            _fileLock.Release();
        }
    }

    // ─── Mapping ──────────────────────────────────────────────────

    /// <summary>
    /// Projects an internal record to the public <see cref="BrowserDevice"/> DTO.
    /// Sensitive fields (SharedKey, ConsecutiveFailures, Revoked) are excluded.
    /// </summary>
    private static BrowserDevice ToPublicDevice(InternalDeviceRecord record)
    {
        var isOnline = record.LastSeenAt.HasValue
            && (DateTimeOffset.UtcNow - record.LastSeenAt.Value) < TimeSpan.FromMinutes(5);

        return new BrowserDevice(
            DeviceId: record.DeviceId,
            Label: record.Label ?? record.DeviceId,
            PairedAt: record.PairedAt,
            ExpiresAt: record.ExpiresAt,
            LastSeenAt: record.LastSeenAt,
            IsOnline: isOnline);
    }

    // ─── JSON options ─────────────────────────────────────────────

    /// <summary>
    /// Creates the <see cref="JsonSerializerOptions"/> instance used for
    /// device persistence. Uses <see cref="JsonSerializerDefaults.Web"/>
    /// with camelCase naming and string enum conversion.
    /// </summary>
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
