using Kode.Agent.Sdk.Tools;

namespace KodaClaw.BrowserHub.Tools;

/// <summary>
/// Arguments for the browser_action tool.
/// </summary>
public sealed record class BrowserActionArgs
{
    [ToolParameter(
        Description = "Browser action name. Supported values: navigate, snapshot, screenshot, get_url, list_tabs, click, type, scroll, key_press, go_back, close_tab, switch_tab, evaluate, evaluate_dom, extract_links, extract_results, evaluate_write, cookies, form_state, console, wait, upload_file, intercept, intercept_clear, intercept_result.")]
    public required string Action { get; init; }

    [ToolParameter(
        Description = "Optional browser tab id. Required for actions that target a specific tab such as snapshot, screenshot, get_url, close_tab, and switch_tab. For switch_tab, this is the target tab id.",
        Required = false)]
    public string? TabId { get; init; }

    [ToolParameter(
        Description = "Optional browser device id. Strongly recommended when more than one browser device is connected; required for non-list_tabs actions in multi-device scenarios. list_tabs responses include deviceId for each tab.",
        Required = false)]
    public string? DeviceId { get; init; }

    [ToolParameter(
        Description = "Action-specific parameters as a dictionary. DOM-oriented actions may target a same-origin iframe via frameSelector or framePath (array of iframe selectors from top document to nested frame). Supported params by action: navigate { url }; snapshot { selector?, frameSelector?, framePath? }; screenshot { quality?, format? }; click { elementIndex, frameSelector?, framePath?, offsetX?, offsetY? }; type { elementIndex, text, frameSelector?, framePath?, clearFirst?, delayMs? }; scroll { direction?='down', amount?, elementIndex?, frameSelector?, framePath? }; key_press { key }; evaluate { script, sandboxed? } for sandboxed projected page state; evaluate_dom { script, frameSelector?, framePath? } for live DOM reads that return JSON-safe data; extract_links { selector?, linkSelector?, limit?, sameOriginOnly?, frameSelector?, framePath? }; extract_results { selector?, itemSelector?, titleSelector?, linkSelector?, snippetSelector?, strategy?, limit?, sameOriginOnly?, frameSelector?, framePath? }; evaluate_write { script, frameSelector?, framePath? }; cookies { url? }; form_state { frameSelector?, framePath? }; console { sinceTimestamp? }; wait { durationMs?, timeoutMs?, waitForSelector?, waitUntil?, frameSelector?, framePath? }; upload_file { elementIndex, filePath, frameSelector?, framePath? }; intercept { urlPattern?, resourceTypes?, requestHeaders?, responseBody? }; intercept_result { urlPattern?, sinceTimestamp?, resourceTypes?, limit? }. Actions list_tabs, get_url, go_back, close_tab, switch_tab, and intercept_clear do not require extra params.",
        Required = false)]
    public Dictionary<string, object?>? Params { get; init; }
}
