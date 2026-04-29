using KodaClaw.BrowserHub.Models;
using KodaClaw.Contracts.Browser;

namespace KodaClaw.BrowserHub;

/// <summary>
/// Defines the browser hub service contract for Phase 1 core read-only operations
/// and Phase 2 write operations.
/// Provides connectivity checks, device management, navigation, DOM snapshots,
/// screenshots, and tab listing through the CDP bridge.
/// <para>
/// Phase 2 completed：write operations (click, type, scroll, key press, evaluate, etc.) are included below.
/// </para>
/// </summary>
public interface IBrowserHubService
{
    /// <summary>
    /// Checks whether the browser hub is currently connected to at least one
    /// browser extension via the CDP bridge.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if at least one device is connected; otherwise <c>false</c>.</returns>
    Task<bool> IsConnectedAsync(CancellationToken ct = default);

    /// <summary>
    /// Retrieves the overall status of the browser hub, including connectivity
    /// state, the list of paired devices, and the time of the last heartbeat.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="BrowserStatus"/> summarizing the hub state.</returns>
    Task<BrowserStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns all browser devices that are currently paired and connected to
    /// the browser hub.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A read-only list of <see cref="BrowserDevice"/> records.</returns>
    Task<IReadOnlyList<BrowserDevice>> GetConnectedDevicesAsync(CancellationToken ct = default);

    /// <summary>
    /// Navigates the specified tab (or the active tab) to the given URL.
    /// </summary>
    /// <param name="url">The absolute URL to navigate to.</param>
    /// <param name="tabId">
    /// Optional tab identifier. When <c>null</c>, the active tab of the
    /// default device is used.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="BrowserResult{T}"/> containing the final URL after
    /// navigation on success, or error details on failure.
    /// </returns>
    Task<BrowserResult<NavigateResult>> NavigateAsync(
        string url,
        string? tabId = null,
        string? deviceId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Captures a structured DOM snapshot of the specified tab, optionally
    /// scoped to elements matching a CSS selector.
    /// </summary>
    /// <param name="tabId">The tab identifier to snapshot.</param>
    /// <param name="selector">
    /// Optional CSS selector. When provided, only matching elements are
    /// included in the snapshot.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="BrowserResult{T}"/> containing a JSON-encoded structured
    /// snapshot payload on success, or error details on failure.
    /// </returns>
    Task<BrowserResult<string>> SnapshotAsync(
        string tabId,
        string? selector = null,
        IReadOnlyList<string>? framePath = null,
        string? deviceId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Takes a screenshot of the specified tab and returns the image as a
    /// base64-encoded data URI.
    /// </summary>
    /// <param name="tabId">The tab identifier to capture.</param>
    /// <param name="format">
    /// The output image format. Defaults to <see cref="ScreenshotFormat.Jpeg"/>.
    /// </param>
    /// <param name="quality">
    /// Image quality for lossy formats (0–100). Defaults to 80.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="BrowserResult{T}"/> containing the base64 data URI string
    /// on success, or error details on failure.
    /// </returns>
    Task<BrowserResult<string>> ScreenshotAsync(
        string tabId,
        ScreenshotFormat format = ScreenshotFormat.Jpeg,
        int quality = 80,
        string? deviceId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves the current URL of the specified tab.
    /// </summary>
    /// <param name="tabId">The tab identifier to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="BrowserResult{T}"/> containing the current URL string
    /// on success, or error details on failure.
    /// </returns>
    Task<BrowserResult<string>> GetUrlAsync(
        string tabId,
        string? deviceId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Lists all open tabs across connected browser devices.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="BrowserResult{T}"/> containing a read-only list of
    /// <see cref="TabInfo"/> records on success, or error details on failure.
    /// </returns>
    Task<BrowserResult<IReadOnlyList<TabInfo>>> ListTabsAsync(
        CancellationToken ct = default);

    // ── Phase 2: write operations ─────────────────────────────────────────────

    /// <summary>
    /// Clicks the element identified by <paramref name="elementIndex"/> (data-kc-index).
    /// Optionally accepts pixel offsets within the element bounding box.
    /// </summary>
    Task<BrowserResult<ClickResult>> ClickAsync(
        string deviceId,
        string? tabId,
        int elementIndex,
        IReadOnlyList<string>? framePath = null,
        int? offsetX = null,
        int? offsetY = null,
        CancellationToken ct = default);

    /// <summary>
    /// Types <paramref name="text"/> into the input element identified by
    /// <paramref name="elementIndex"/>.
    /// </summary>
    /// <param name="clearFirst">When <c>true</c>, clears the input before typing.</param>
    /// <param name="delayMs">Per-character delay in milliseconds (simulates human typing).</param>
    Task<BrowserResult<TypeResult>> TypeAsync(
        string deviceId,
        string? tabId,
        int elementIndex,
        string text,
        IReadOnlyList<string>? framePath = null,
        bool clearFirst = false,
        int delayMs = 50,
        CancellationToken ct = default);

    /// <summary>
    /// Scrolls the page or a specific element in the given direction.
    /// </summary>
    /// <param name="direction">Scroll direction.</param>
    /// <param name="amount">Pixels to scroll; <c>null</c> defaults to 300 px.</param>
    /// <param name="elementIndex">Element to scroll within; <c>null</c> scrolls the page.</param>
    Task<BrowserResult<ScrollResult>> ScrollAsync(
        string deviceId,
        string? tabId,
        ScrollDirection direction,
        int? amount = null,
        int? elementIndex = null,
        IReadOnlyList<string>? framePath = null,
        CancellationToken ct = default);

    /// <summary>
    /// Dispatches a keyboard key event. Supports modifier combos such as
    /// <c>Control+A</c> or <c>Shift+Tab</c>.
    /// </summary>
    Task<BrowserResult<KeyPressResult>> KeyPressAsync(
        string deviceId,
        string? tabId,
        string key,
        CancellationToken ct = default);

    /// <summary>
    /// Navigates the specified tab back in its history (equivalent to clicking
    /// the browser Back button).
    /// </summary>
    Task<BrowserResult<NavigationResult>> GoBackAsync(
        string deviceId,
        string? tabId,
        CancellationToken ct = default);

    /// <summary>
    /// Closes the tab identified by <paramref name="tabId"/>.
    /// </summary>
    Task<BrowserResult<CloseTabResult>> CloseTabAsync(
        string deviceId,
        string tabId,
        CancellationToken ct = default);

    /// <summary>
    /// Switches focus to the tab identified by <paramref name="tabId"/>.
    /// </summary>
    Task<BrowserResult<SwitchTabResult>> SwitchTabAsync(
        string deviceId,
        string tabId,
        CancellationToken ct = default);

    /// <summary>
    /// Evaluates a JavaScript expression inside the safe sandbox helper. This
    /// is suitable for projected page state such as title/body text, but it
    /// does not expose the live DOM tree.
    /// </summary>
    /// <param name="script">The JavaScript expression to evaluate.</param>
    /// <param name="sandboxed">
    /// When <c>true</c> (default), runs in a safe sandbox.
    /// When <c>false</c>, executes via direct <c>eval</c>.
    /// </param>
    Task<BrowserResult<object?>> EvaluateAsync(
        string deviceId,
        string? tabId,
        string script,
        bool sandboxed = true,
        CancellationToken ct = default);

    /// <summary>
    /// Evaluates a read-only JavaScript expression against the live page DOM.
    /// The expression must synchronously return a JSON-serializable value.
    /// DOM mutations, network requests, and other side effects are rejected by
    /// the browser runtime when possible.
    /// </summary>
    Task<BrowserResult<object?>> EvaluateDomAsync(
        string deviceId,
        string? tabId,
        string script,
        IReadOnlyList<string>? framePath = null,
        CancellationToken ct = default);

    /// <summary>
    /// Extracts a bounded list of links from the live DOM using a high-level,
    /// read-only helper. Prefer this over hand-written DOM scripts when you
    /// need href/text pairs from a page or section.
    /// </summary>
    Task<BrowserResult<LinkExtractionResult>> ExtractLinksAsync(
        string deviceId,
        string? tabId,
        string? selector = null,
        string? linkSelector = null,
        int limit = 20,
        bool sameOriginOnly = false,
        IReadOnlyList<string>? framePath = null,
        CancellationToken ct = default);

    /// <summary>
    /// Extracts structured search/result cards from the live DOM using built-in
    /// heuristics or caller-supplied selectors.
    /// </summary>
    Task<BrowserResult<ResultExtractionResult>> ExtractResultsAsync(
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
        CancellationToken ct = default);

    /// <summary>
    /// Evaluates a write-intent JavaScript expression without sandboxing.
    /// Use for operations that intentionally modify page state.
    /// </summary>
    Task<BrowserResult<EvaluateResult>> EvaluateWriteAsync(
        string deviceId,
        string? tabId,
        string script,
        IReadOnlyList<string>? framePath = null,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves cookies for the current page, or for <paramref name="url"/> when specified.
    /// Sensitive cookie values are masked as <c>***</c> by the bridge.
    /// </summary>
    Task<BrowserResult<CookiesResult>> GetCookiesAsync(
        string deviceId,
        string? tabId,
        string? url = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the current state of all form elements on the page.
    /// </summary>
    Task<BrowserResult<FormStateResult>> GetFormStateAsync(
        string deviceId,
        string? tabId,
        IReadOnlyList<string>? framePath = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns console messages captured by the extension, optionally filtered
    /// to those recorded after <paramref name="sinceTimestamp"/>.
    /// </summary>
    Task<BrowserResult<ConsoleMessagesResult>> GetConsoleMessagesAsync(
        string deviceId,
        string? tabId,
        long? sinceTimestamp = null,
        CancellationToken ct = default);

    /// <summary>
    /// Waits for a duration, a CSS selector to appear, or a navigation condition.
    /// Mirrors the <c>cdpWait</c> handler in the extension.
    /// </summary>
    /// <param name="durationMs">Fixed wait in milliseconds; mutually exclusive with <paramref name="waitForSelector"/> and <paramref name="waitUntil"/>.</param>
    /// <param name="timeoutMs">Maximum polling time in milliseconds for selector/navigation waits.</param>
    /// <param name="waitForSelector">CSS selector to wait for.</param>
    /// <param name="waitUntil">Navigation condition to wait for (e.g. <c>load</c>, <c>domcontentloaded</c>).</param>
    Task<BrowserResult<WaitResult>> WaitAsync(
        string deviceId,
        string? tabId,
        int? durationMs = null,
        int? timeoutMs = null,
        string? waitForSelector = null,
        string? waitUntil = null,
        IReadOnlyList<string>? framePath = null,
        CancellationToken ct = default);

    /// <summary>
    /// Uploads a file to the file-input element identified by
    /// <paramref name="elementIndex"/> via CDP <c>DOM.setFileInputFiles</c>.
    /// <paramref name="filePath"/> must be accessible on the server side.
    /// </summary>
    Task<BrowserResult<UploadFileResult>> UploadFileAsync(
        string deviceId,
        string? tabId,
        int elementIndex,
        string filePath,
        IReadOnlyList<string>? framePath = null,
        CancellationToken ct = default);

    // ── Network intercept ─────────────────────────────────────────────────────

    /// <summary>
    /// Starts network request interception on the specified tab using the CDP Fetch domain.
    /// Captured requests are stored in-extension (max 100) and can be retrieved via
    /// <see cref="GetInterceptedRequestsAsync"/>.
    /// </summary>
    /// <param name="urlPattern">URL match pattern (glob). Defaults to <c>*</c>.</param>
    /// <param name="resourceTypes">Resource types to intercept (e.g. <c>XHR</c>, <c>Fetch</c>). Defaults to XHR and Fetch.</param>
    /// <param name="requestHeaders">When <c>true</c>, captures request headers for each intercepted request.</param>
    /// <param name="responseBody">Reserved for future use; currently ignored by the extension.</param>
    Task<BrowserResult<InterceptResult>> StartInterceptAsync(
        string deviceId,
        string? tabId,
        string? urlPattern = null,
        string[]? resourceTypes = null,
        bool requestHeaders = false,
        bool responseBody = false,
        CancellationToken ct = default);

    /// <summary>
    /// Stops network interception on the specified tab and clears all captured request records.
    /// </summary>
    Task<BrowserResult<InterceptClearResult>> StopInterceptAsync(
        string deviceId,
        string? tabId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns captured network requests, optionally filtered by URL pattern, timestamp, or resource type.
    /// </summary>
    /// <param name="urlPattern">Glob pattern to filter URLs.</param>
    /// <param name="sinceTimestamp">Unix ms timestamp — only requests after this time are returned.</param>
    /// <param name="resourceTypes">Resource type filter.</param>
    /// <param name="limit">Maximum number of records to return. Defaults to 50.</param>
    Task<BrowserResult<InterceptRequestsResult>> GetInterceptedRequestsAsync(
        string deviceId,
        string? tabId,
        string? urlPattern = null,
        long? sinceTimestamp = null,
        string[]? resourceTypes = null,
        int limit = 50,
        CancellationToken ct = default);

}
