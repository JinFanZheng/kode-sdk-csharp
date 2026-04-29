using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using KodaClaw.BrowserHub.Connection;
using KodaClaw.BrowserHub.Device;
using KodaClaw.BrowserHub.Models;
using KodaClaw.BrowserHub.Screenshot;
using KodaClaw.Contracts.Browser;

namespace KodaClaw.BrowserHub;

/// <summary>
/// Core implementation of <see cref="IBrowserHubService"/> for Phase 1
/// read-only browser operations and Phase 2 write operations.
///
/// <para>
/// Orchestrates <see cref="BrowserDeviceStore"/>, <see cref="DevicePairingService"/>,
/// <see cref="DeviceAuthService"/>, and <see cref="BridgeConnectionManager"/> to
/// forward Agent tool requests to the Chrome extension over the CDP bridge and
/// correlate the responses.
/// </para>
///
/// <para>
/// Request-response correlation: each outbound request is assigned a UUID v4
/// correlation ID. A <see cref="TaskCompletionSource{T}"/> is registered in
/// <see cref="_pendingRequests"/> before the message is sent. When the handler
/// fires <see cref="BridgeConnectionHandler.OnResponse"/>, the matching TCS is
/// completed and the awaiting method returns.
/// </para>
/// </summary>
public sealed class BrowserHubService : IBrowserHubService
{
    private const string DefaultSessionId = "hub-internal";
    private const int DefaultRequestTimeoutMs = 30_000;
    private const int UploadFileTimeoutMs = 60_000;
    private const int InterceptTimeoutMs = 60_000;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly BrowserDeviceStore _deviceStore;
    private readonly BridgeConnectionManager _connectionManager;
    private readonly BridgeConnectionHandler _connectionHandler;
    private readonly ScreenshotUploadService? _screenshotUploadService;

    // ── Pending request map ───────────────────────────────────────────────────

    private readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeResponse>>
        _pendingRequests = new();
    private readonly ConcurrentDictionary<string, string> _tabDeviceMap = new();

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the service and wires up the response callback.
    /// </summary>
    /// <param name="deviceStore">Persistent device store.</param>
    /// <param name="connectionManager">WebSocket connection manager.</param>
    /// <param name="connectionHandler">Protocol message handler.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is <c>null</c>.</exception>
    public BrowserHubService(
        BrowserDeviceStore deviceStore,
        BridgeConnectionManager connectionManager,
        BridgeConnectionHandler connectionHandler,
        ScreenshotUploadService? screenshotUploadService = null)
    {
        ArgumentNullException.ThrowIfNull(deviceStore);
        ArgumentNullException.ThrowIfNull(connectionManager);
        ArgumentNullException.ThrowIfNull(connectionHandler);

        _deviceStore = deviceStore;
        _connectionManager = connectionManager;
        _connectionHandler = connectionHandler;
        _screenshotUploadService = screenshotUploadService;

        _connectionHandler.OnResponse += CompleteRequest;
    }

    // ── IBrowserHubService ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<bool> IsConnectedAsync(CancellationToken ct = default)
    {
        var connected = _connectionManager.ConnectedDeviceIds.Count > 0;
        return Task.FromResult(connected);
    }

    /// <inheritdoc/>
    public async Task<BrowserStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var devices = await _deviceStore.GetAllDevicesAsync(ct).ConfigureAwait(false);
        var connectedDevices = devices.Where(d => d.IsOnline).ToList();

        var lastHeartbeat = devices
            .Where(d => d.LastSeenAt.HasValue)
            .Select(d => d.LastSeenAt!.Value)
            .OrderByDescending(t => t)
            .FirstOrDefault();

        return new BrowserStatus(
            IsConnected: connectedDevices.Count > 0,
            Devices: connectedDevices,
            LastHeartbeat: lastHeartbeat == default ? null : lastHeartbeat);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BrowserDevice>> GetConnectedDevicesAsync(
        CancellationToken ct = default)
    {
        var all = await _deviceStore.GetAllDevicesAsync(ct).ConfigureAwait(false);
        return all.Where(d => d.IsOnline).ToList();
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<NavigateResult>> NavigateAsync(
        string url,
        string? tabId = null,
        string? deviceId = null,
        CancellationToken ct = default)
    {
        var resolvedDeviceId = ResolveDeviceId(tabId, deviceId);
        var payload = new { url };
        var result = resolvedDeviceId is null
            ? await SendRequestAsync<NavigateResult>(
                action: "navigate",
                payload: payload,
                tabId: tabId,
                ct: ct).ConfigureAwait(false)
            : await SendRequestAsync<NavigateResult>(
                deviceId: resolvedDeviceId,
                action: "navigate",
                payload: payload,
                tabId: tabId,
                timeoutMs: DefaultRequestTimeoutMs,
                ct: ct).ConfigureAwait(false);

        return WithNavigateDevice(result, resolvedDeviceId);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<string>> SnapshotAsync(
        string tabId,
        string? selector = null,
        IReadOnlyList<string>? framePath = null,
        string? deviceId = null,
        CancellationToken ct = default)
    {
        var resolvedDeviceId = ResolveDeviceId(tabId, deviceId);
        var normalizedFramePath = NormalizeFramePath(framePath);
        var payload = selector is not null || normalizedFramePath is not null
            ? new
            {
                selector,
                framePath = normalizedFramePath,
            }
            : (object?)null;
        return resolvedDeviceId is null
            ? await SendRequestAsync<string>(
                action: "snapshot",
                payload: payload,
                tabId: tabId,
                ct: ct).ConfigureAwait(false)
            : await SendRequestAsync<string>(
                deviceId: resolvedDeviceId,
                action: "snapshot",
                payload: payload,
                tabId: tabId,
                timeoutMs: DefaultRequestTimeoutMs,
                ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<string>> ScreenshotAsync(
        string tabId,
        ScreenshotFormat format = ScreenshotFormat.Jpeg,
        int quality = 80,
        string? deviceId = null,
        CancellationToken ct = default)
    {
        var resolvedDeviceId = ResolveDeviceId(tabId, deviceId) ?? _connectionManager.ConnectedDeviceIds.FirstOrDefault();
        if (resolvedDeviceId is null)
        {
            return Fail<string>("BRIDGE_001", "No connected browser device.");
        }

        object payload = new
        {
            format = format.ToString().ToLowerInvariant(),
            quality,
        };

        if (_screenshotUploadService is not null)
        {
            var (token, _, _) = _screenshotUploadService.GenerateUploadToken(
                resolvedDeviceId,
                requestId: Guid.NewGuid().ToString("N"),
                format: format.ToString());

            payload = new
            {
                format = format.ToString().ToLowerInvariant(),
                quality,
                uploadToken = token,
                uploadUrl = "/api/browser/screenshot/upload",
            };
        }

        return await SendRequestAsync<string>(
            deviceId: resolvedDeviceId,
            action: "screenshot",
            payload: payload,
            tabId: tabId,
            timeoutMs: DefaultRequestTimeoutMs,
            ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<string>> GetUrlAsync(
        string tabId,
        string? deviceId = null,
        CancellationToken ct = default)
    {
        var resolvedDeviceId = ResolveDeviceId(tabId, deviceId);
        return resolvedDeviceId is null
            ? await SendRequestAsync<string>(
                action: "get_url",
                payload: null,
                tabId: tabId,
                ct: ct).ConfigureAwait(false)
            : await SendRequestAsync<string>(
                deviceId: resolvedDeviceId,
                action: "get_url",
                payload: null,
                tabId: tabId,
                timeoutMs: DefaultRequestTimeoutMs,
                ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<IReadOnlyList<TabInfo>>> ListTabsAsync(
        CancellationToken ct = default)
    {
        var deviceIds = _connectionManager.ConnectedDeviceIds.ToList();
        if (deviceIds.Count == 0)
        {
            return Fail<IReadOnlyList<TabInfo>>("BRIDGE_001", "No connected browser device.");
        }

        var tabs = new List<TabInfo>();

        foreach (var deviceId in deviceIds)
        {
            var result = await SendRequestAsync<IReadOnlyList<TabInfo>>(
                deviceId: deviceId,
                action: "list_tabs",
                payload: null,
                tabId: null,
                timeoutMs: DefaultRequestTimeoutMs,
                ct: ct).ConfigureAwait(false);

            if (!result.Ok)
            {
                return result;
            }

            if (result.Data is null)
            {
                continue;
            }

            foreach (var tab in result.Data)
            {
                var enriched = tab with { DeviceId = deviceId };
                RememberTab(enriched.TabId, deviceId);
                tabs.Add(enriched);
            }
        }

        return new BrowserResult<IReadOnlyList<TabInfo>>(Ok: true, Data: tabs);
    }

    // ── Phase 2: write operations ─────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<BrowserResult<ClickResult>> ClickAsync(
        string deviceId,
        string? tabId,
        int elementIndex,
        IReadOnlyList<string>? framePath = null,
        int? offsetX = null,
        int? offsetY = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            elementIndex,
            framePath = NormalizeFramePath(framePath),
            offsetX,
            offsetY,
        };
        return await SendRequestAsync<ClickResult>(
            deviceId: deviceId,
            action: "click",
            payload: payload,
            tabId: tabId,
            timeoutMs: DefaultRequestTimeoutMs,
            ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<TypeResult>> TypeAsync(
        string deviceId,
        string? tabId,
        int elementIndex,
        string text,
        IReadOnlyList<string>? framePath = null,
        bool clearFirst = false,
        int delayMs = 50,
        CancellationToken ct = default)
    {
        var payload = new
        {
            elementIndex,
            text,
            framePath = NormalizeFramePath(framePath),
            clearFirst,
            delayMs,
        };
        return await SendRequestAsync<TypeResult>(
            deviceId: deviceId,
            action: "type",
            payload: payload,
            tabId: tabId,
            timeoutMs: DefaultRequestTimeoutMs,
            ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<ScrollResult>> ScrollAsync(
        string deviceId,
        string? tabId,
        ScrollDirection direction,
        int? amount = null,
        int? elementIndex = null,
        IReadOnlyList<string>? framePath = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            direction = direction.ToString().ToLowerInvariant(),
            amount,
            elementIndex,
            framePath = NormalizeFramePath(framePath),
        };
        return await SendRequestAsync<ScrollResult>(
            deviceId: deviceId,
            action: "scroll",
            payload: payload,
            tabId: tabId,
            timeoutMs: DefaultRequestTimeoutMs,
            ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<KeyPressResult>> KeyPressAsync(
        string deviceId,
        string? tabId,
        string key,
        CancellationToken ct = default)
    {
        var payload = new { key };
        return await SendRequestAsync<KeyPressResult>(
            deviceId: deviceId,
            action: "key_press",
            payload: payload,
            tabId: tabId,
            timeoutMs: DefaultRequestTimeoutMs,
            ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<NavigationResult>> GoBackAsync(
        string deviceId,
        string? tabId,
        CancellationToken ct = default)
    {
        return await SendRequestAsync<NavigationResult>(
            deviceId: deviceId,
            action: "go_back",
            payload: null,
            tabId: tabId,
            timeoutMs: DefaultRequestTimeoutMs,
            ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<CloseTabResult>> CloseTabAsync(
        string deviceId,
        string tabId,
        CancellationToken ct = default)
    {
        return await SendRequestAsync<CloseTabResult>(
            deviceId: deviceId,
            action: "close_tab",
            payload: null,
            tabId: tabId,
            timeoutMs: DefaultRequestTimeoutMs,
            ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<SwitchTabResult>> SwitchTabAsync(
        string deviceId,
        string tabId,
        CancellationToken ct = default)
    {
        var payload = new { tabId };
        return await SendRequestAsync<SwitchTabResult>(
            deviceId: deviceId,
            action: "switch_tab",
            payload: payload,
            tabId: null,
            timeoutMs: DefaultRequestTimeoutMs,
            ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<object?>> EvaluateAsync(
        string deviceId,
        string? tabId,
        string script,
        bool sandboxed = true,
        CancellationToken ct = default)
    {
        var payload = new { script, sandboxed };
        return await SendRequestAsync<object?>(
            deviceId: deviceId,
            action: "evaluate",
            payload: payload,
            tabId: tabId,
            timeoutMs: DefaultRequestTimeoutMs,
            ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<object?>> EvaluateDomAsync(
        string deviceId,
        string? tabId,
        string script,
        IReadOnlyList<string>? framePath = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            script,
            framePath = NormalizeFramePath(framePath),
        };
        return await SendRequestAsync<object?>(
            deviceId: deviceId,
            action: "evaluate_dom",
            payload: payload,
            tabId: tabId,
            timeoutMs: DefaultRequestTimeoutMs,
            ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<LinkExtractionResult>> ExtractLinksAsync(
        string deviceId,
        string? tabId,
        string? selector = null,
        string? linkSelector = null,
        int limit = 20,
        bool sameOriginOnly = false,
        IReadOnlyList<string>? framePath = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            selector,
            linkSelector,
            limit,
            sameOriginOnly,
            framePath = NormalizeFramePath(framePath),
        };

        return await SendRequestAsync<LinkExtractionResult>(
            deviceId: deviceId,
            action: "extract_links",
            payload: payload,
            tabId: tabId,
            timeoutMs: DefaultRequestTimeoutMs,
            ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<ResultExtractionResult>> ExtractResultsAsync(
        string deviceId,
        string? tabId,
        string? selector = null,
        string? itemSelector = null,
        string? titleSelector = null,
        string? linkSelector = null,
        string? snippetSelector = null,
        string? strategy = null,
        int limit = 10,
        bool sameOriginOnly = false,
        IReadOnlyList<string>? framePath = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            selector,
            itemSelector,
            titleSelector,
            linkSelector,
            snippetSelector,
            strategy,
            limit,
            sameOriginOnly,
            framePath = NormalizeFramePath(framePath),
        };

        return await SendRequestAsync<ResultExtractionResult>(
            deviceId: deviceId,
            action: "extract_results",
            payload: payload,
            tabId: tabId,
            timeoutMs: DefaultRequestTimeoutMs,
            ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<EvaluateResult>> EvaluateWriteAsync(
        string deviceId,
        string? tabId,
        string script,
        IReadOnlyList<string>? framePath = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            script,
            framePath = NormalizeFramePath(framePath),
            sandboxed = false,
        };
        return await SendRequestAsync<EvaluateResult>(
            deviceId: deviceId,
            action: "evaluate_write",
            payload: payload,
            tabId: tabId,
            timeoutMs: DefaultRequestTimeoutMs,
            ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<CookiesResult>> GetCookiesAsync(
        string deviceId,
        string? tabId,
        string? url = null,
        CancellationToken ct = default)
    {
        var payload = url is not null ? new { url } : (object?)null;
        return await SendRequestAsync<CookiesResult>(
            deviceId: deviceId,
            action: "cookies",
            payload: payload,
            tabId: tabId,
            timeoutMs: DefaultRequestTimeoutMs,
            ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<FormStateResult>> GetFormStateAsync(
        string deviceId,
        string? tabId,
        IReadOnlyList<string>? framePath = null,
        CancellationToken ct = default)
    {
        var payload = NormalizeFramePath(framePath) is { } normalizedFramePath
            ? new { framePath = normalizedFramePath }
            : null;
        return await SendRequestAsync<FormStateResult>(
            deviceId: deviceId,
            action: "form_state",
            payload: payload,
            tabId: tabId,
            timeoutMs: DefaultRequestTimeoutMs,
            ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<ConsoleMessagesResult>> GetConsoleMessagesAsync(
        string deviceId,
        string? tabId,
        long? sinceTimestamp = null,
        CancellationToken ct = default)
    {
        var payload = sinceTimestamp.HasValue ? new { sinceTimestamp } : (object?)null;
        return await SendRequestAsync<ConsoleMessagesResult>(
            deviceId: deviceId,
            action: "console",
            payload: payload,
            tabId: tabId,
            timeoutMs: DefaultRequestTimeoutMs,
            ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<WaitResult>> WaitAsync(
        string deviceId,
        string? tabId,
        int? durationMs = null,
        int? timeoutMs = null,
        string? waitForSelector = null,
        string? waitUntil = null,
        IReadOnlyList<string>? framePath = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            durationMs,
            timeoutMs,
            waitForSelector,
            waitUntil,
            framePath = NormalizeFramePath(framePath),
        };
        var requestTimeoutMs = durationMs.HasValue
            ? Math.Max(DefaultRequestTimeoutMs, durationMs.Value + 5_000)
            : timeoutMs.HasValue
                ? Math.Max(DefaultRequestTimeoutMs, timeoutMs.Value + 5_000)
            : DefaultRequestTimeoutMs;
        return await SendRequestAsync<WaitResult>(
            deviceId: deviceId,
            action: "wait",
            payload: payload,
            tabId: tabId,
            timeoutMs: requestTimeoutMs,
            ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<UploadFileResult>> UploadFileAsync(
        string deviceId,
        string? tabId,
        int elementIndex,
        string filePath,
        IReadOnlyList<string>? framePath = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            elementIndex,
            filePath,
            framePath = NormalizeFramePath(framePath),
        };
        return await SendRequestAsync<UploadFileResult>(
            deviceId: deviceId,
            action: "upload_file",
            payload: payload,
            tabId: tabId,
            timeoutMs: UploadFileTimeoutMs,
            ct: ct).ConfigureAwait(false);
    }

    // ── Network intercept ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<BrowserResult<InterceptResult>> StartInterceptAsync(
        string deviceId,
        string? tabId,
        string? urlPattern = null,
        string[]? resourceTypes = null,
        bool requestHeaders = false,
        bool responseBody = false,
        CancellationToken ct = default)
    {
        var payload = new { urlPattern, resourceTypes, requestHeaders, responseBody };
        return await SendRequestAsync<InterceptResult>(
            deviceId: deviceId,
            action: "intercept",
            payload: payload,
            tabId: tabId,
            timeoutMs: InterceptTimeoutMs,
            ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<InterceptClearResult>> StopInterceptAsync(
        string deviceId,
        string? tabId,
        CancellationToken ct = default)
    {
        return await SendRequestAsync<InterceptClearResult>(
            deviceId: deviceId,
            action: "intercept_clear",
            payload: null,
            tabId: tabId,
            timeoutMs: DefaultRequestTimeoutMs,
            ct: ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BrowserResult<InterceptRequestsResult>> GetInterceptedRequestsAsync(
        string deviceId,
        string? tabId,
        string? urlPattern = null,
        long? sinceTimestamp = null,
        string[]? resourceTypes = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        var payload = new { urlPattern, sinceTimestamp, resourceTypes, limit };
        return await SendRequestAsync<InterceptRequestsResult>(
            deviceId: deviceId,
            action: "intercept_result",
            payload: payload,
            tabId: tabId,
            timeoutMs: DefaultRequestTimeoutMs,
            ct: ct).ConfigureAwait(false);
    }

    // ── Request dispatch ──────────────────────────────────────────────────────

    /// <summary>
    /// Selects the first connected device, serialises a <see cref="BridgeRequest"/>,
    /// sends it, and awaits the correlated <see cref="BridgeResponse"/>.
    /// </summary>
    /// <typeparam name="T">Expected data type in the response.</typeparam>
    /// <param name="action">Bridge action name.</param>
    /// <param name="payload">Action payload object.</param>
    /// <param name="tabId">Optional target tab ID.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<BrowserResult<T>> SendRequestAsync<T>(
        string action,
        object? payload,
        string? tabId,
        CancellationToken ct)
    {
        var deviceId = _connectionManager.ConnectedDeviceIds.FirstOrDefault();
        if (deviceId is null)
        {
            return Fail<T>("BRIDGE_001", "No connected browser device.");
        }

        var (requestId, json) = _connectionHandler.BuildRequestJson(
            action: action,
            sessionId: DefaultSessionId,
            payload: payload,
            tabId: tabId,
            timeoutMs: DefaultRequestTimeoutMs);

        var tcs = new TaskCompletionSource<BridgeResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _pendingRequests[requestId] = tcs;

        try
        {
            var sent = await _connectionManager
                .SendToDeviceAsync(deviceId, json, ct)
                .ConfigureAwait(false);

            if (!sent)
            {
                _pendingRequests.TryRemove(requestId, out _);
                return Fail<T>("BRIDGE_007", "Failed to send message to device.");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(DefaultRequestTimeoutMs);

            try
            {
                var response = await tcs.Task
                    .WaitAsync(timeoutCts.Token)
                    .ConfigureAwait(false);

                if (response.Ok)
                {
                    if (!TryDeserializeData(response.Data, out T? data))
                    {
                        return Fail<T>("BRIDGE_009", "Failed to deserialize response data.");
                    }

                    return new BrowserResult<T>(Ok: true, Data: data);
                }

                return Fail<T>("BRIDGE_008", response.Error ?? "Unknown error.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Fail<T>("BRIDGE_004", "Request timed out.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Routes a request to a specific device, serialises a <see cref="BridgeRequest"/>,
    /// sends it, and awaits the correlated <see cref="BridgeResponse"/>.
    /// Used by Phase 2 write operations that accept an explicit device identifier.
    /// </summary>
    /// <typeparam name="T">Expected data type in the response.</typeparam>
    /// <param name="deviceId">Target device identifier.</param>
    /// <param name="action">Bridge action name.</param>
    /// <param name="payload">Action payload object.</param>
    /// <param name="tabId">Optional target tab ID.</param>
    /// <param name="timeoutMs">Request timeout in milliseconds.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<BrowserResult<T>> SendRequestAsync<T>(
        string deviceId,
        string action,
        object? payload,
        string? tabId,
        int timeoutMs,
        CancellationToken ct)
    {
        if (!_connectionManager.ConnectedDeviceIds.Contains(deviceId))
        {
            return Fail<T>("BRIDGE_001", $"Device '{deviceId}' is not connected.");
        }

        var (requestId, json) = _connectionHandler.BuildRequestJson(
            action: action,
            sessionId: DefaultSessionId,
            payload: payload,
            tabId: tabId,
            timeoutMs: timeoutMs);

        var tcs = new TaskCompletionSource<BridgeResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _pendingRequests[requestId] = tcs;

        try
        {
            var sent = await _connectionManager
                .SendToDeviceAsync(deviceId, json, ct)
                .ConfigureAwait(false);

            if (!sent)
            {
                _pendingRequests.TryRemove(requestId, out _);
                return Fail<T>("BRIDGE_007", "Failed to send message to device.");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            try
            {
                var response = await tcs.Task
                    .WaitAsync(timeoutCts.Token)
                    .ConfigureAwait(false);

                if (response.Ok)
                {
                    if (!TryDeserializeData(response.Data, out T? data))
                    {
                        return Fail<T>("BRIDGE_009", "Failed to deserialize response data.");
                    }

                    return new BrowserResult<T>(Ok: true, Data: data);
                }

                return Fail<T>("BRIDGE_008", response.Error ?? "Unknown error.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Fail<T>("BRIDGE_004", "Request timed out.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    private static string[]? NormalizeFramePath(IReadOnlyList<string>? framePath)
    {
        if (framePath is null || framePath.Count == 0)
        {
            return null;
        }

        var normalized = framePath
            .Where(selector => !string.IsNullOrWhiteSpace(selector))
            .Select(selector => selector.Trim())
            .ToArray();

        return normalized.Length == 0 ? null : normalized;
    }

    // ── Response correlation ──────────────────────────────────────────────────

    /// <summary>
    /// Called by <see cref="BridgeConnectionHandler.OnResponse"/> when a response
    /// arrives. Locates the matching <see cref="TaskCompletionSource{T}"/> and
    /// completes it.
    /// </summary>
    private void CompleteRequest(string deviceId, BridgeResponse response)
    {
        if (_pendingRequests.TryRemove(response.Id, out var tcs))
        {
            tcs.TrySetResult(response);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static BrowserResult<T> Fail<T>(string code, string error) =>
        new(Ok: false, Data: default, Error: error, ErrorCode: code);

    private BrowserResult<NavigateResult> WithNavigateDevice(
        BrowserResult<NavigateResult> result,
        string? fallbackDeviceId)
    {
        if (!result.Ok || result.Data is null)
        {
            return result;
        }

        var resolvedDeviceId = result.Data.DeviceId ?? fallbackDeviceId;
        if (string.IsNullOrWhiteSpace(resolvedDeviceId))
        {
            return result;
        }

        RememberTab(result.Data.TabId, resolvedDeviceId);
        return result with
        {
            Data = result.Data with { DeviceId = resolvedDeviceId },
        };
    }

    private void RememberTab(string? tabId, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(tabId) || string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        _tabDeviceMap[tabId] = deviceId;
    }

    private string? ResolveDeviceId(string? tabId, string? explicitDeviceId)
    {
        if (!string.IsNullOrWhiteSpace(explicitDeviceId))
        {
            return explicitDeviceId;
        }

        if (!string.IsNullOrWhiteSpace(tabId) && _tabDeviceMap.TryGetValue(tabId, out var mappedDeviceId))
        {
            return mappedDeviceId;
        }

        return null;
    }

    /// <summary>
    /// Deserialises the <c>data</c> field of a <see cref="BridgeResponse"/>.
    /// The field arrives as a <see cref="JsonElement"/> when using System.Text.Json;
    /// this helper converts it to <typeparamref name="T"/>.
    /// </summary>
    private static bool TryDeserializeData<T>(object? data, out T? value)
    {
        if (data is null)
        {
            value = default;
            return true;
        }

        if (data is T directCast)
        {
            value = directCast;
            return true;
        }

        if (data is JsonElement element)
        {
            try
            {
                value = element.Deserialize<T>(JsonOptions);
                return true;
            }
            catch
            {
                value = default;
                return false;
            }
        }

        try
        {
            var json = JsonSerializer.Serialize(data, JsonOptions);
            value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return true;
        }
        catch
        {
            value = default;
            return false;
        }
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
