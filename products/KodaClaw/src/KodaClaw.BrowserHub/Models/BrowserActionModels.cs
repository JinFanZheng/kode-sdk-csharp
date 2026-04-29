using System.Text.Json;
using System.Text.Json.Serialization;

namespace KodaClaw.BrowserHub.Models;
/// <summary>滚动方向。</summary>
public enum ScrollDirection { Up, Down, Left, Right, Top, Bottom }
// ── Click ─────────────────────────────────────────────────────────────────────

public sealed record ClickResult(
    [property: JsonPropertyName("clicked")] bool Clicked,
    [property: JsonPropertyName("elementIndex")] int ElementIndex);
// ── Type ──────────────────────────────────────────────────────────────────────

public sealed record TypeResult(
    [property: JsonPropertyName("typed")] bool Typed,
    [property: JsonPropertyName("elementIndex")] int ElementIndex,
    [property: JsonPropertyName("charCount")] int CharCount);
// ── Scroll ────────────────────────────────────────────────────────────────────

public sealed record ScrollResult(
    [property: JsonPropertyName("scrolled")] bool Scrolled,
    [property: JsonPropertyName("scrollY")] int ScrollY);
// ── KeyPress ──────────────────────────────────────────────────────────────────

public sealed record KeyPressResult(
    [property: JsonPropertyName("pressed")] bool Pressed,
    [property: JsonPropertyName("key")] string Key);
// ── Navigation ────────────────────────────────────────────────────────────────

public sealed record NavigationResult(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("title")] string Title);
// ── Tab management ────────────────────────────────────────────────────────────

public sealed record CloseTabResult(
    [property: JsonPropertyName("closed")] bool Closed,
    [property: JsonPropertyName("tabId")] string TabId);

public sealed record SwitchTabResult(
    [property: JsonPropertyName("switched")] bool Switched,
    [property: JsonPropertyName("currentTabId")] string CurrentTabId);
// ── Evaluate ──────────────────────────────────────────────────────────────────

public sealed record EvaluateResult(
    [property: JsonPropertyName("value")] object? Value,
    [property: JsonPropertyName("error")] string? Error);
// ── Structured extraction ────────────────────────────────────────────────────

public sealed record ExtractedLink(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("href")] string Href,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("sameOrigin")] bool SameOrigin);

public sealed record LinkExtractionResult(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("selector")] string Selector,
    [property: JsonPropertyName("linkSelector")] string LinkSelector,
    [property: JsonPropertyName("totalMatches")] int TotalMatches,
    [property: JsonPropertyName("returnedCount")] int ReturnedCount,
    [property: JsonPropertyName("sameOriginOnly")] bool SameOriginOnly,
    [property: JsonPropertyName("links")] IReadOnlyList<ExtractedLink> Links,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record ExtractedResultItem(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("snippet")] string? Snippet,
    [property: JsonPropertyName("source")] string? Source);

public sealed record ResultExtractionResult(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("strategy")] string Strategy,
    [property: JsonPropertyName("selector")] string Selector,
    [property: JsonPropertyName("itemSelector")] string ItemSelector,
    [property: JsonPropertyName("totalMatches")] int TotalMatches,
    [property: JsonPropertyName("returnedCount")] int ReturnedCount,
    [property: JsonPropertyName("sameOriginOnly")] bool SameOriginOnly,
    [property: JsonPropertyName("results")] IReadOnlyList<ExtractedResultItem> Results,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("error")] string? Error = null);
// ── Cookies ───────────────────────────────────────────────────────────────────

public sealed record CookieItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("httpOnly")] bool HttpOnly,
    [property: JsonPropertyName("secure")] bool Secure,
    [property: JsonPropertyName("expires")] JsonElement? Expires);

public sealed record CookiesResult(
    [property: JsonPropertyName("cookies")] IReadOnlyList<CookieItem> Cookies);
// ── Form state ────────────────────────────────────────────────────────────────

public sealed record FormElementState(
    [property: JsonPropertyName("elementIndex")] int ElementIndex,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("checked")] bool? Checked,
    [property: JsonPropertyName("selectedOptions")] IReadOnlyList<JsonElement>? SelectedOptions);

public sealed record FormStateResult(
    [property: JsonPropertyName("forms")] IReadOnlyList<FormElementState> Forms);
// ── Console messages ──────────────────────────────────────────────────────────

public sealed record ConsoleMessage(
    [property: JsonPropertyName("type")] string Level,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("url")] string? Source);

public sealed record ConsoleMessagesResult(
    [property: JsonPropertyName("messages")] IReadOnlyList<ConsoleMessage> Messages);
// ── Wait ──────────────────────────────────────────────────────────────────────

public sealed record WaitResult(
    [property: JsonPropertyName("waited")] bool Waited,
    [property: JsonPropertyName("elapsedMs")] int? ElapsedMs);
// ── Upload file ───────────────────────────────────────────────────────────────

public sealed record UploadFileResult(
    [property: JsonPropertyName("uploaded")] bool Uploaded,
    [property: JsonPropertyName("elementIndex")] int ElementIndex,
    [property: JsonPropertyName("fileName")] string FileName);
// ── Network intercept ─────────────────────────────────────────────────────────

public sealed record InterceptedRequest(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("resourceType")] string ResourceType,
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("requestHeaders")] Dictionary<string, string>? RequestHeaders,
    [property: JsonPropertyName("statusCode")] int? StatusCode,
    [property: JsonPropertyName("responseHeaders")] Dictionary<string, string>? ResponseHeaders);

public sealed record InterceptResult(
    [property: JsonPropertyName("intercepting")] bool Intercepting,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("pattern")] string Pattern,
    [property: JsonPropertyName("resourceTypes")] string[] ResourceTypes);

public sealed record InterceptClearResult(
    [property: JsonPropertyName("intercepting")] bool Intercepting,
    [property: JsonPropertyName("clearedCount")] int ClearedCount);

public sealed record InterceptRequestsResult(
    [property: JsonPropertyName("requests")] InterceptedRequest[] Requests);
