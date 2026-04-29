using System.Text.Json;
using System.Collections;
using System.Diagnostics;
using System.Text.RegularExpressions;
using KodaClaw.BrowserHub.Models;
using KodaClaw.Contracts.Browser;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.BrowserHub.Tools;

/// <summary>
/// Agent tool that exposes BrowserHub actions for browser automation.
/// </summary>
public sealed class BrowserActionTool : ToolBase<BrowserActionArgs>
{
    private static readonly HashSet<string> SupportedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "navigate",
        "snapshot",
        "screenshot",
        "get_url",
        "list_tabs",
        "click",
        "type",
        "scroll",
        "key_press",
        "go_back",
        "close_tab",
        "switch_tab",
        "evaluate",
        "evaluate_dom",
        "extract_links",
        "extract_results",
        "evaluate_write",
        "cookies",
        "form_state",
        "console",
        "wait",
        "upload_file",
        "intercept",
        "intercept_clear",
        "intercept_result",
    };

    private static readonly Regex DataUriRegex = new(
        "^data:(?<mediaType>[^;]+);base64,",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IBrowserHubService _browserHubService;

    public BrowserActionTool(IBrowserHubService browserHubService)
    {
        ArgumentNullException.ThrowIfNull(browserHubService);
        _browserHubService = browserHubService;
    }

    public override string Name => "browser_action";

    public override string Description =>
        "Execute BrowserHub browser actions through a single tool entry point. " +
        "Supports navigation, DOM inspection, screenshots, tab management, input, JavaScript evaluation, cookies, console logs, waits, file upload, and network interception. " +
        "Use `snapshot` for lightweight page summaries, `extract_links`/`extract_results` for common structured scraping, `evaluate_dom` for custom live DOM reads that return JSON-safe data, and `evaluate_write` only when you intentionally need to modify page state.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<BrowserActionArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = false,
    };

    protected override async Task<ToolResult> ExecuteAsync(
        BrowserActionArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(args.Action))
            {
                return ToolResult.Fail("Parameter 'action' is required.");
            }

            var action = args.Action.Trim().ToLowerInvariant();
            if (!SupportedActions.Contains(action))
            {
                return ToolResult.Fail($"Unsupported browser action '{args.Action}'.");
            }

            string? deviceId = null;
            if (!string.Equals(action, "list_tabs", StringComparison.OrdinalIgnoreCase))
            {
                var deviceResult = await ResolveDeviceIdAsync(action, args, cancellationToken).ConfigureAwait(false);
                if (!deviceResult.Success)
                {
                    return deviceResult.ErrorResult!;
                }

                deviceId = deviceResult.DeviceId!;
            }

            return action switch
            {
                "navigate" => ToToolResult(
                    await _browserHubService.NavigateAsync(
                        url: GetRequiredStringParam(args.Params, "url"),
                        tabId: args.TabId,
                        deviceId: deviceId,
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "snapshot" => ToToolResult(
                    await _browserHubService.SnapshotAsync(
                        tabId: GetRequiredTabId(args),
                        selector: GetOptionalStringParam(args.Params, "selector"),
                        framePath: GetOptionalFramePathParam(args.Params),
                        deviceId: deviceId,
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "screenshot" => ToToolResult(
                    await _browserHubService.ScreenshotAsync(
                        tabId: GetRequiredTabId(args),
                        format: GetOptionalEnumParam(args.Params, "format", ScreenshotFormat.Jpeg),
                        quality: GetOptionalIntParam(args.Params, "quality") ?? 80,
                        deviceId: deviceId,
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "get_url" => ToToolResult(
                    await _browserHubService.GetUrlAsync(
                        tabId: GetRequiredTabId(args),
                        deviceId: deviceId,
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "list_tabs" => ToToolResult(
                    await _browserHubService.ListTabsAsync(cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "click" => ToToolResult(
                    await _browserHubService.ClickAsync(
                        deviceId: deviceId!,
                        tabId: args.TabId,
                        elementIndex: GetRequiredIntParam(args.Params, "elementIndex"),
                        framePath: GetOptionalFramePathParam(args.Params),
                        offsetX: GetOptionalIntParam(args.Params, "offsetX"),
                        offsetY: GetOptionalIntParam(args.Params, "offsetY"),
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "type" => ToToolResult(
                    await _browserHubService.TypeAsync(
                        deviceId: deviceId!,
                        tabId: args.TabId,
                        elementIndex: GetRequiredIntParam(args.Params, "elementIndex"),
                        text: GetRequiredStringParam(args.Params, "text"),
                        framePath: GetOptionalFramePathParam(args.Params),
                        clearFirst: GetOptionalBoolParam(args.Params, "clearFirst") ?? false,
                        delayMs: GetOptionalIntParam(args.Params, "delayMs") ?? 50,
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "scroll" => ToToolResult(
                    await _browserHubService.ScrollAsync(
                        deviceId: deviceId!,
                        tabId: args.TabId,
                        direction: GetOptionalEnumParam(args.Params, "direction", ScrollDirection.Down),
                        amount: GetOptionalIntParam(args.Params, "amount"),
                        elementIndex: GetOptionalIntParam(args.Params, "elementIndex"),
                        framePath: GetOptionalFramePathParam(args.Params),
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "key_press" => ToToolResult(
                    await _browserHubService.KeyPressAsync(
                        deviceId: deviceId!,
                        tabId: args.TabId,
                        key: GetRequiredStringParam(args.Params, "key"),
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "go_back" => ToToolResult(
                    await _browserHubService.GoBackAsync(
                        deviceId: deviceId!,
                        tabId: args.TabId,
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "close_tab" => ToToolResult(
                    await _browserHubService.CloseTabAsync(
                        deviceId: deviceId!,
                        tabId: GetRequiredTabId(args),
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "switch_tab" => ToToolResult(
                    await _browserHubService.SwitchTabAsync(
                        deviceId: deviceId!,
                        tabId: GetRequiredTabId(args),
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "evaluate" => ToToolResult(
                    await _browserHubService.EvaluateAsync(
                        deviceId: deviceId!,
                        tabId: args.TabId,
                        script: GetRequiredStringParam(args.Params, "script"),
                        sandboxed: GetOptionalBoolParam(args.Params, "sandboxed") ?? true,
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "evaluate_dom" => ToToolResult(
                    await _browserHubService.EvaluateDomAsync(
                        deviceId: deviceId!,
                        tabId: args.TabId,
                        script: GetRequiredStringParam(args.Params, "script"),
                        framePath: GetOptionalFramePathParam(args.Params),
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "extract_links" => ToToolResult(
                    await _browserHubService.ExtractLinksAsync(
                        deviceId: deviceId!,
                        tabId: args.TabId,
                        selector: GetOptionalStringParam(args.Params, "selector"),
                        linkSelector: GetOptionalStringParam(args.Params, "linkSelector"),
                        limit: GetOptionalIntParam(args.Params, "limit") ?? 20,
                        sameOriginOnly: GetOptionalBoolParam(args.Params, "sameOriginOnly") ?? false,
                        framePath: GetOptionalFramePathParam(args.Params),
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "extract_results" => ToToolResult(
                    await _browserHubService.ExtractResultsAsync(
                        deviceId: deviceId!,
                        tabId: args.TabId,
                        selector: GetOptionalStringParam(args.Params, "selector"),
                        itemSelector: GetOptionalStringParam(args.Params, "itemSelector"),
                        titleSelector: GetOptionalStringParam(args.Params, "titleSelector"),
                        linkSelector: GetOptionalStringParam(args.Params, "linkSelector"),
                        snippetSelector: GetOptionalStringParam(args.Params, "snippetSelector"),
                        strategy: GetOptionalStringParam(args.Params, "strategy"),
                        limit: GetOptionalIntParam(args.Params, "limit") ?? 10,
                        sameOriginOnly: GetOptionalBoolParam(args.Params, "sameOriginOnly") ?? false,
                        framePath: GetOptionalFramePathParam(args.Params),
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "evaluate_write" => ToToolResult(
                    await _browserHubService.EvaluateWriteAsync(
                        deviceId: deviceId!,
                        tabId: args.TabId,
                        script: GetRequiredStringParam(args.Params, "script"),
                        framePath: GetOptionalFramePathParam(args.Params),
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "cookies" => ToToolResult(
                    await _browserHubService.GetCookiesAsync(
                        deviceId: deviceId!,
                        tabId: args.TabId,
                        url: GetOptionalStringParam(args.Params, "url"),
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "form_state" => ToToolResult(
                    await _browserHubService.GetFormStateAsync(
                        deviceId: deviceId!,
                        tabId: args.TabId,
                        framePath: GetOptionalFramePathParam(args.Params),
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "console" => ToToolResult(
                    await _browserHubService.GetConsoleMessagesAsync(
                        deviceId: deviceId!,
                        tabId: args.TabId,
                        sinceTimestamp: GetOptionalLongParam(args.Params, "sinceTimestamp"),
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "wait" => ToToolResult(
                    await _browserHubService.WaitAsync(
                        deviceId: deviceId!,
                        tabId: args.TabId,
                        durationMs: GetOptionalIntParam(args.Params, "durationMs"),
                        timeoutMs: GetOptionalIntParam(args.Params, "timeoutMs"),
                        waitForSelector: GetOptionalStringParam(args.Params, "waitForSelector"),
                        waitUntil: GetOptionalStringParam(args.Params, "waitUntil"),
                        framePath: GetOptionalFramePathParam(args.Params),
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "upload_file" => ToToolResult(
                    await _browserHubService.UploadFileAsync(
                        deviceId: deviceId!,
                        tabId: args.TabId,
                        elementIndex: GetRequiredIntParam(args.Params, "elementIndex"),
                        filePath: GetRequiredStringParam(args.Params, "filePath"),
                        framePath: GetOptionalFramePathParam(args.Params),
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "intercept" => ToToolResult(
                    await _browserHubService.StartInterceptAsync(
                        deviceId: deviceId!,
                        tabId: args.TabId,
                        urlPattern: GetOptionalStringParam(args.Params, "urlPattern"),
                        resourceTypes: GetOptionalStringArrayParam(args.Params, "resourceTypes"),
                        requestHeaders: GetOptionalBoolParam(args.Params, "requestHeaders") ?? false,
                        responseBody: GetOptionalBoolParam(args.Params, "responseBody") ?? false,
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "intercept_clear" => ToToolResult(
                    await _browserHubService.StopInterceptAsync(
                        deviceId: deviceId!,
                        tabId: args.TabId,
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                "intercept_result" => ToToolResult(
                    await _browserHubService.GetInterceptedRequestsAsync(
                        deviceId: deviceId!,
                        tabId: args.TabId,
                        urlPattern: GetOptionalStringParam(args.Params, "urlPattern"),
                        sinceTimestamp: GetOptionalLongParam(args.Params, "sinceTimestamp"),
                        resourceTypes: GetOptionalStringArrayParam(args.Params, "resourceTypes"),
                        limit: GetOptionalIntParam(args.Params, "limit") ?? 50,
                        ct: cancellationToken).ConfigureAwait(false),
                    action,
                    context.ContextPressure),

                _ => throw new UnreachableException($"Unsupported browser action '{args.Action}'.")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Browser action failed: {ex.Message}");
        }
    }

    private async Task<(bool Success, string? DeviceId, ToolResult? ErrorResult)> ResolveDeviceIdAsync(
        string action,
        BrowserActionArgs args,
        CancellationToken cancellationToken)
    {
        var devices = await _browserHubService.GetConnectedDevicesAsync(cancellationToken).ConfigureAwait(false);
        if (devices.Count == 0)
        {
            return (false, null, ToolResult.Fail(
                "No connected browser device is available. Reconnect the browser extension, then retry."));
        }

        if (!string.IsNullOrWhiteSpace(args.DeviceId))
        {
            var deviceId = args.DeviceId.Trim();
            if (devices.Any(d => string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal)))
            {
                return (true, deviceId, null);
            }

            return (false, null, ToolResult.Fail(
                $"Browser device '{args.DeviceId}' is not connected. Call `list_tabs` to refresh the current tab/device mapping, then retry with a connected `deviceId`."));
        }

        if (devices.Count > 1)
        {
            return (false, null, ToolResult.Fail(
                $"Parameter 'deviceId' is required for action '{action}' when multiple browser devices are connected. First call `list_tabs`, then retry this action with the matching top-level `deviceId` from the target tab."));
        }

        return (true, devices[0].DeviceId, null);
    }

    private static ToolResult ToToolResult<T>(BrowserResult<T> result, string action, float contextPressure)
    {
        if (result.Ok)
        {
            return ToolResult.Ok(ProtectLargeResult(action, result.Data, contextPressure));
        }

        var error = string.IsNullOrWhiteSpace(result.Error)
            ? $"Browser action '{action}' failed."
            : $"Browser action '{action}' failed: {result.Error}";

        if (!string.IsNullOrWhiteSpace(result.ErrorCode))
        {
            error = $"{error} (code: {result.ErrorCode})";
        }

        var retryGuidance = BuildRetryGuidance(action, result.ErrorCode, result.Error);
        if (!string.IsNullOrWhiteSpace(retryGuidance))
        {
            error = $"{error} {retryGuidance}";
        }

        return ToolResult.Fail(error);
    }

    private static string? BuildRetryGuidance(string action, string? errorCode, string? error)
    {
        if (string.Equals(errorCode, "BRIDGE_004", StringComparison.OrdinalIgnoreCase))
        {
            return action switch
            {
                "snapshot" => "Retry once. If it times out again, narrow the target with `params.selector` or switch to a smaller tab first.",
                "screenshot" => "Retry once. If it times out again, retry on a simpler page state or smaller target tab.",
                "evaluate" => "Retry once. If it times out again, shorten the script or switch to `evaluate_dom` for targeted live DOM extraction.",
                "evaluate_dom" or "evaluate_write" => "Retry once. If it times out again, shorten the script and return a smaller JSON-safe result.",
                "extract_links" => "Retry once. If it times out again, narrow the target with `params.selector` or `params.linkSelector`, or lower `params.limit`.",
                "extract_results" => "Retry once. If it times out again, narrow the target with `params.selector`/`params.itemSelector`, or lower `params.limit`.",
                _ => "Retry once. If it keeps timing out, call `list_tabs` to refresh the current tab/device mapping and retry on the correct target."
            };
        }

        if (string.Equals(errorCode, "BRIDGE_007", StringComparison.OrdinalIgnoreCase))
        {
            return "Call `list_tabs` to refresh the current tab/device mapping, then retry with the correct top-level `deviceId` and `tabId`.";
        }

        if (string.Equals(errorCode, "BRIDGE_001", StringComparison.OrdinalIgnoreCase))
        {
            return "Ensure the browser extension is connected. If multiple devices are available, call `list_tabs` and retry with the matching top-level `deviceId`.";
        }

        if (string.Equals(errorCode, "BRIDGE_008", StringComparison.OrdinalIgnoreCase) &&
            LooksLikeMissingOrMovedTab(error))
        {
            return "Call `list_tabs` to refresh the current tabs, then retry with the latest `tabId` and matching top-level `deviceId`.";
        }

        if (string.Equals(action, "snapshot", StringComparison.OrdinalIgnoreCase) &&
            error?.Contains("SNAPSHOT_HELPER_UNAVAILABLE", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Retry once. If the page is already stable and it still fails, use `evaluate_dom` for targeted extraction instead of requesting full-page HTML.";
        }

        if ((string.Equals(action, "extract_links", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(action, "extract_results", StringComparison.OrdinalIgnoreCase)) &&
            error?.Contains("EXTRACT_HELPER_UNAVAILABLE", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Retry once after reloading the browser extension or page. If it still fails, the gateway and extension may be running different builds.";
        }

        if (LooksLikeNavigationInProgress(error))
        {
            return action switch
            {
                "snapshot" or "extract_links" or "extract_results" or "evaluate_dom" or "form_state" =>
                    "The page may still be navigating or re-rendering. Call `wait` for a stable selector or readyState, then retry the read on the current tab.",
                "evaluate" or "screenshot" or "cookies" or "get_url" =>
                    "The page may still be navigating. Wait briefly or call `wait`, then retry once on the same tab.",
                _ => null
            };
        }

        if (LooksLikeFrameResolutionFailure(error))
        {
            return action switch
            {
                "snapshot" or "click" or "type" or "scroll" or "evaluate_dom" or "extract_links" or "extract_results" or "evaluate_write" or "form_state" or "wait" or "upload_file" =>
                    "If the target lives inside a same-origin iframe, retry with `params.frameSelector` or `params.framePath`. Cross-origin iframe content is not directly supported.",
                _ => null
            };
        }

        if (string.Equals(action, "evaluate_dom", StringComparison.OrdinalIgnoreCase))
        {
            if (error?.Contains("side effect", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "Return pure JSON-safe data from the current document only. Avoid DOM mutations, clicks, network requests, timers, or any expression that can cause side effects.";
            }

            if (error?.Contains("cross-origin", StringComparison.OrdinalIgnoreCase) == true ||
                error?.Contains("Blocked a frame", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "Read from the current document only. Cross-origin iframe content is not directly readable; target same-origin content or a narrower selector in the top document.";
            }
        }

        if (string.Equals(action, "extract_links", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(action, "extract_results", StringComparison.OrdinalIgnoreCase))
        {
            if (error?.Contains("cross-origin", StringComparison.OrdinalIgnoreCase) == true ||
                error?.Contains("Blocked a frame", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "These extract actions only read the current document. Cross-origin iframe content is not directly readable; narrow the top-document selector or navigate closer to the target content.";
            }
        }

        return null;
    }

    private static bool LooksLikeMissingOrMovedTab(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return error.Contains("BRIDGE_002", StringComparison.OrdinalIgnoreCase)
            || error.Contains("tab", StringComparison.OrdinalIgnoreCase)
            || error.Contains("active tab", StringComparison.OrdinalIgnoreCase)
            || error.Contains("不存在", StringComparison.OrdinalIgnoreCase)
            || error.Contains("活跃标签页", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeNavigationInProgress(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return error.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Cannot find context with specified id", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Cannot find object with given id", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Inspected target navigated or closed", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Loader has changed while resolving nodes", StringComparison.OrdinalIgnoreCase)
            || error.Contains("No frame with given id", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Frame with the given id was not found", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeFrameResolutionFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return error.Contains("FRAME_", StringComparison.OrdinalIgnoreCase)
            || error.Contains("same-origin iframe", StringComparison.OrdinalIgnoreCase)
            || error.Contains("iframe selector", StringComparison.OrdinalIgnoreCase);
    }

    private static object? ProtectLargeResult<T>(string action, T? value, float contextPressure)
    {
        if (action == "list_tabs" && value is IEnumerable<TabInfo> tabs)
        {
            var tabArray = tabs.ToArray();
            return new
            {
                tabs = tabArray,
                note = "Reuse the top-level `deviceId` from the target tab in follow-up browser_action calls when multiple browser devices are connected.",
            };
        }

        if (action == "navigate" && value is NavigateResult navigateResult)
        {
            return new
            {
                url = navigateResult.Url,
                tabId = navigateResult.TabId,
                deviceId = navigateResult.DeviceId,
                note = "Reuse this `tabId` for follow-up actions on the same page. When multiple browser devices are connected, also reuse this top-level `deviceId`.",
            };
        }

        if (value is not string text)
        {
            return value;
        }

        if (action == "screenshot" && TryBuildScreenshotReference(text, out var screenshotReference))
        {
            return screenshotReference;
        }

        if (action == "snapshot" && TryParseStructuredSnapshot(text, out var structuredSnapshot))
        {
            return structuredSnapshot;
        }

        var maxChars = GetMaxStringResultChars(contextPressure);
        if (text.Length <= maxChars)
        {
            return text;
        }

        var headLength = Math.Min(text.Length, Math.Max(512, maxChars - 512));
        var tailLength = Math.Min(text.Length - headLength, Math.Min(512, maxChars / 4));

        return new
        {
            truncated = true,
            action,
            originalChars = text.Length,
            preview = text[..headLength],
            tail = tailLength > 0 ? text[^tailLength..] : null,
            note = action == "snapshot"
                ? "Snapshot output was truncated to avoid context bloat. Re-run with `params.selector` to narrow the DOM region, or switch to `evaluate_dom` for targeted extraction, for example `{\"action\":\"evaluate_dom\",\"tabId\":\"<tabId>\",\"deviceId\":\"<deviceId>\",\"params\":{\"script\":\"Array.from(document.querySelectorAll('main a')).slice(0, 10).map(a => ({ text: a.textContent?.trim(), href: a.href }))\"}}`."
                : "Browser action output was truncated to avoid context bloat.",
        };
    }

    private static bool TryParseStructuredSnapshot(string value, out object? structuredSnapshot)
    {
        structuredSnapshot = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.TrimStart();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal) &&
            !trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            structuredSnapshot = JsonSerializer.Deserialize<JsonElement>(value);
            return true;
        }
        catch
        {
            structuredSnapshot = null;
            return false;
        }
    }

    private static bool TryBuildScreenshotReference(string value, out object? screenshotReference)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            screenshotReference = value;
            return true;
        }

        if (value.StartsWith("/api/browser/screenshot/", StringComparison.OrdinalIgnoreCase))
        {
            screenshotReference = new
            {
                stored = true,
                path = value,
                note = "Reuse this screenshot path instead of requesting inline base64 data.",
            };
            return true;
        }

        var match = DataUriRegex.Match(value);
        if (!match.Success)
        {
            screenshotReference = null;
            return false;
        }

        screenshotReference = new
        {
            stored = false,
            inlineDataOmitted = true,
            mediaType = match.Groups["mediaType"].Value,
            originalChars = value.Length,
            note = "Inline screenshot data was omitted to avoid context bloat. Re-run `screenshot` and reuse the returned path/reference instead of requesting base64.",
        };
        return true;
    }

    private static int GetMaxStringResultChars(float contextPressure)
    {
        if (contextPressure >= 1.0f)
        {
            return 2_000;
        }

        if (contextPressure >= 0.8f)
        {
            return 4_000;
        }

        return 12_000;
    }

    private static string GetRequiredTabId(BrowserActionArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.TabId))
        {
            throw new ArgumentException(
                "Parameter 'tabId' is required for this action. Call `list_tabs` first, then retry with the target `tabId` (and `deviceId` when multiple browser devices are connected).");
        }

        return args.TabId.Trim();
    }

    private static string GetRequiredStringParam(IReadOnlyDictionary<string, object?>? parameters, string name)
    {
        var value = GetParamValue(parameters, name);
        var converted = ConvertToString(value);

        if (string.IsNullOrWhiteSpace(converted))
        {
            throw new ArgumentException($"Parameter '{name}' is required.");
        }

        return converted;
    }

    private static string? GetOptionalStringParam(IReadOnlyDictionary<string, object?>? parameters, string name)
    {
        var value = GetParamValue(parameters, name);
        return value is null ? null : ConvertToString(value);
    }

    private static int GetRequiredIntParam(IReadOnlyDictionary<string, object?>? parameters, string name)
    {
        var value = GetParamValue(parameters, name);
        var converted = ConvertToInt(value, name);

        if (!converted.HasValue)
        {
            throw new ArgumentException($"Parameter '{name}' is required.");
        }

        return converted.Value;
    }

    private static int? GetOptionalIntParam(IReadOnlyDictionary<string, object?>? parameters, string name)
        => ConvertToInt(GetParamValue(parameters, name), name);

    private static long? GetOptionalLongParam(IReadOnlyDictionary<string, object?>? parameters, string name)
        => ConvertToLong(GetParamValue(parameters, name), name);

    private static bool? GetOptionalBoolParam(IReadOnlyDictionary<string, object?>? parameters, string name)
        => ConvertToBool(GetParamValue(parameters, name), name);

    private static TEnum GetOptionalEnumParam<TEnum>(
        IReadOnlyDictionary<string, object?>? parameters,
        string name,
        TEnum defaultValue)
        where TEnum : struct, Enum
    {
        var value = GetParamValue(parameters, name);
        if (value is null)
        {
            return defaultValue;
        }

        var raw = ConvertToString(value);
        if (string.IsNullOrWhiteSpace(raw) || !Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed))
        {
            var validValues = string.Join(", ", Enum.GetNames<TEnum>());
            throw new ArgumentException($"Parameter '{name}' must be one of: {validValues}.");
        }

        return parsed;
    }

    private static string[]? GetOptionalStringArrayParam(IReadOnlyDictionary<string, object?>? parameters, string name)
    {
        var value = GetParamValue(parameters, name);
        if (value is null)
        {
            return null;
        }

        return value switch
        {
            string text => [text],
            string[] array => array,
            IEnumerable<string> enumerable => enumerable.ToArray(),
            JsonElement element when element.ValueKind == JsonValueKind.Array =>
                element.EnumerateArray().Select(item =>
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        throw new ArgumentException($"Parameter '{name}' must be an array of strings.");
                    }

                    return item.GetString()!;
                }).ToArray(),
            IEnumerable enumerable => enumerable.Cast<object?>().Select(item => ConvertToString(item) ?? throw new ArgumentException($"Parameter '{name}' must be an array of strings.")).ToArray(),
            _ => throw new ArgumentException($"Parameter '{name}' must be an array of strings."),
        };
    }

    private static string[]? GetOptionalFramePathParam(IReadOnlyDictionary<string, object?>? parameters)
    {
        var framePath = GetOptionalStringArrayParam(parameters, "framePath");
        if (framePath is { Length: > 0 })
        {
            return framePath
                .Where(selector => !string.IsNullOrWhiteSpace(selector))
                .Select(selector => selector.Trim())
                .ToArray();
        }

        var frameSelector = GetOptionalStringParam(parameters, "frameSelector");
        if (!string.IsNullOrWhiteSpace(frameSelector))
        {
            return [frameSelector.Trim()];
        }

        return null;
    }

    private static object? GetParamValue(IReadOnlyDictionary<string, object?>? parameters, string name)
    {
        if (parameters is null)
        {
            return null;
        }

        foreach (var (key, value) in parameters)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static string? ConvertToString(object? value)
    {
        return value switch
        {
            null => null,
            string text => text,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            JsonElement element when element.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.ToString(),
            _ => value.ToString(),
        };
    }

    private static int? ConvertToInt(object? value, string name)
    {
        return value switch
        {
            null => null,
            int number => number,
            long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number) => number,
            JsonElement element when element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var number) => number,
            string text when int.TryParse(text, out var number) => number,
            _ => throw new ArgumentException($"Parameter '{name}' must be an integer."),
        };
    }

    private static long? ConvertToLong(object? value, string name)
    {
        return value switch
        {
            null => null,
            long number => number,
            int number => number,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number) => number,
            JsonElement element when element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), out var number) => number,
            string text when long.TryParse(text, out var number) => number,
            _ => throw new ArgumentException($"Parameter '{name}' must be an integer."),
        };
    }

    private static bool? ConvertToBool(object? value, string name)
    {
        return value switch
        {
            null => null,
            bool boolean => boolean,
            JsonElement element when element.ValueKind is JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
            JsonElement element when element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out var boolean) => boolean,
            string text when bool.TryParse(text, out var boolean) => boolean,
            _ => throw new ArgumentException($"Parameter '{name}' must be a boolean."),
        };
    }
}
