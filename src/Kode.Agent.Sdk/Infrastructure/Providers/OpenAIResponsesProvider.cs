using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kode.Agent.Sdk.Core.Types;
using Microsoft.Extensions.Logging;
using ContentBlock = Kode.Agent.Sdk.Core.Types.ContentBlock;
using FileContent = Kode.Agent.Sdk.Core.Types.FileContent;
using ImageContent = Kode.Agent.Sdk.Core.Types.ImageContent;
using TextContent = Kode.Agent.Sdk.Core.Types.TextContent;
using ToolResultContent = Kode.Agent.Sdk.Core.Types.ToolResultContent;
using ToolUseContent = Kode.Agent.Sdk.Core.Types.ToolUseContent;
using VideoContent = Kode.Agent.Sdk.Core.Types.VideoContent;

namespace Kode.Agent.Sdk.Infrastructure.Providers;

/// <summary>
/// OpenAI Responses API provider (<c>/v1/responses</c>).
/// Uses raw HTTP instead of the OpenAI SDK, which does not yet have
/// first-class support for this endpoint.
/// </summary>
public sealed class OpenAIResponsesProvider : IModelProvider
{
    private readonly HttpClient _httpClient;
    private readonly RetryPolicy _retryPolicy;
    private readonly ILogger<OpenAIResponsesProvider>? _logger;
    private readonly string _endpoint;

    public string ProviderName => "openai-responses";

    /// <summary>Production constructor.</summary>
    public OpenAIResponsesProvider(OpenAIResponsesOptions options, ILogger<OpenAIResponsesProvider>? logger = null)
        : this(new HttpClientHandler(), options, logger)
    {
    }

    /// <summary>
    /// For backward compatibility with HttpClient-based construction (e.g. DI).
    /// </summary>
    public OpenAIResponsesProvider(HttpClient httpClient, OpenAIResponsesOptions options, ILogger<OpenAIResponsesProvider>? logger = null)
    {
        _logger = logger;
        _retryPolicy = options.RetryPolicy ?? RetryPolicy.Default;
        var baseUrl = options.BaseUrl?.TrimEnd('/') ?? "https://api.openai.com/v1";
        _endpoint = baseUrl + "/responses";
        _httpClient = httpClient;
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.ApiKey);
        ApplyCustomHeaders(_httpClient, options);
    }

    /// <summary>
    /// Internal constructor for testing — accepts a custom <see cref="HttpMessageHandler"/>.
    /// </summary>
    internal OpenAIResponsesProvider(HttpMessageHandler handler, OpenAIResponsesOptions options, ILogger<OpenAIResponsesProvider>? logger = null)
    {
        _logger = logger;
        _retryPolicy = options.RetryPolicy ?? RetryPolicy.Default;
        var baseUrl = options.BaseUrl?.TrimEnd('/') ?? "https://api.openai.com/v1";
        _endpoint = baseUrl + "/responses";
        _httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.ApiKey);
        ApplyCustomHeaders(_httpClient, options);
    }

    private static void ApplyCustomHeaders(HttpClient client, OpenAIResponsesOptions options)
    {
        if (options.CustomHeaders is { Count: > 0 })
        {
            foreach (var (key, value) in options.CustomHeaders)
                client.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        }
    }

    // =========================================================================
    // IModelProvider
    // =========================================================================

    public async IAsyncEnumerable<StreamChunk> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in ProviderRetryHelper.StreamWithRetryAsync(
            ct => StreamCoreAsync(request, ct),
            _retryPolicy, _logger, ProviderName, cancellationToken))
        {
            yield return chunk;
        }
    }

    private async IAsyncEnumerable<StreamChunk> StreamCoreAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _logger?.LogDebug("OpenAI Responses stream: model={Model}, tools={ToolCount}",
            request.Model, request.Tools?.Count ?? 0);

        var body = BuildRequestBody(request, stream: true);
        var bodyJson = body.ToJsonString();
        _logger?.LogDebug("OpenAI Responses request body: {Body}", bodyJson);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger?.LogError("OpenAI Responses API error {Status}: {Body}", (int)response.StatusCode, errorBody);
            throw new HttpRequestException(
                $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). Body: {errorBody}",
                inner: null,
                statusCode: response.StatusCode);
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        await foreach (var chunk in ParseSseStreamAsync(stream, cancellationToken))
            yield return chunk;
    }

    public Task<ModelResponse> CompleteAsync(
        ModelRequest request, CancellationToken cancellationToken = default)
    {
        return ProviderRetryHelper.ExecuteWithRetryAsync(
            async ct =>
            {
                var body = BuildRequestBody(request, stream: false);
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint)
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
                };

                using var response = await _httpClient.SendAsync(httpRequest, ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                return ParseCompleteResponse(json);
            },
            _retryPolicy, _logger, ProviderName, cancellationToken);
    }

    public async Task<bool> ValidateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await CompleteAsync(new ModelRequest
            {
                Model = "gpt-4o-mini",
                Messages = [Message.User("Hi")],
                MaxTokens = 1
            }, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "OpenAI Responses provider validation failed");
            return false;
        }
    }

    // =========================================================================
    // Request building
    // =========================================================================

    internal JsonObject BuildRequestBody(ModelRequest request, bool stream)
    {
        var body = new JsonObject { ["model"] = request.Model };

        if (!string.IsNullOrEmpty(request.SystemPrompt))
            body["instructions"] = request.SystemPrompt;

        if (request.MaxTokens.HasValue)
            body["max_output_tokens"] = request.MaxTokens.Value;

        if (request.Temperature.HasValue)
            body["temperature"] = (float)request.Temperature.Value;

        if (stream)
            body["stream"] = true;

        // Input array
        var input = new JsonArray();
        foreach (var item in BuildInputItems(request.Messages))
            input.Add(item);
        body["input"] = input;

        // Tools
        if (request.Tools?.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                var schemaJson = tool.InputSchema is JsonElement je
                    ? je.GetRawText()
                    : JsonSerializer.Serialize(tool.InputSchema);

                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(schemaJson)
                });
            }
            body["tools"] = tools;
        }

        return body;
    }

    /// <summary>
    /// Converts SDK <see cref="Message"/> list into Responses API input items.
    /// <list type="bullet">
    ///   <item>User text/image → <c>role: "user"</c></item>
    ///   <item>ToolResultContent → <c>role: "tool"</c> (function_call_output)</item>
    ///   <item>Assistant text → <c>role: "assistant"</c> with output_text parts</item>
    ///   <item>Assistant tool call → <c>role: "assistant"</c> with function_call parts</item>
    /// </list>
    /// </summary>
    internal static IEnumerable<JsonObject> BuildInputItems(IReadOnlyList<Message> messages)
    {
        foreach (var msg in messages)
        {
            if (msg.Role == MessageRole.System) continue;

            if (msg.Role == MessageRole.User)
            {
                var toolResults = msg.Content.OfType<ToolResultContent>().ToList();
                var otherContent = msg.Content.Where(c => c is not ToolResultContent).ToList();

                // Tool results are top-level function_call_output items (no role wrapper).
                foreach (var tr in toolResults)
                {
                    // Avoid double-serialization: if Content is already a string, use it directly.
                    var output = tr.Content is string s ? s : JsonSerializer.Serialize(tr.Content);
                    yield return new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = tr.ToolUseId,
                        ["output"] = output
                    };
                }

                if (otherContent.Count > 0)
                {
                    var userItem = BuildUserItem(otherContent);
                    if (userItem != null) yield return userItem;
                }
            }
            else if (msg.Role == MessageRole.Assistant)
            {
                foreach (var item in BuildAssistantItems(msg.Content))
                    yield return item;
            }
        }
    }

    private static JsonObject? BuildUserItem(IReadOnlyList<ContentBlock> content)
    {
        var hasMultiModal = content.OfType<ImageContent>().Any()
            || content.OfType<VideoContent>().Any()
            || content.OfType<FileContent>().Any();

        if (!hasMultiModal)
        {
            var text = string.Join("", content.OfType<TextContent>().Select(t => t.Text));
            if (string.IsNullOrEmpty(text)) return null;
            // Simple string content for text-only messages
            return new JsonObject { ["role"] = "user", ["content"] = text };
        }

        var parts = new JsonArray();
        foreach (var block in content)
        {
            switch (block)
            {
                case TextContent tc:
                    parts.Add(new JsonObject { ["type"] = "input_text", ["text"] = tc.Text });
                    break;
                case ImageContent { Url: not null } img:
                    parts.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = img.Url });
                    break;
                case ImageContent { Data: not null } img:
                    parts.Add(new JsonObject
                    {
                        ["type"] = "input_image",
                        ["image_url"] = $"data:{img.MediaType ?? "image/png"};base64,{img.Data}"
                    });
                    break;
                case VideoContent video:
                    // Encode as text marker; video_url is not natively supported
                    parts.Add(new JsonObject
                    {
                        ["type"] = "input_text",
                        ["text"] = MediaRewriteHttpHandler.VideoMarker + video.Url
                    });
                    break;
                case FileContent file:
                    parts.Add(new JsonObject
                    {
                        ["type"] = "input_text",
                        ["text"] = MediaRewriteHttpHandler.FileMarker + file.Url
                    });
                    break;
            }
        }

        return new JsonObject { ["role"] = "user", ["content"] = parts };
    }

    /// <summary>
    /// Builds the assistant turn as a mix of top-level items:
    /// - Text → <c>{"role":"assistant","content":"..."}</c> message
    /// - Tool call → top-level <c>{"type":"function_call",...}</c> (no role wrapper)
    /// </summary>
    private static IEnumerable<JsonObject> BuildAssistantItems(IReadOnlyList<ContentBlock> content)
    {
        var textBuffer = new System.Text.StringBuilder();

        foreach (var block in content)
        {
            switch (block)
            {
                case TextContent { Text: { Length: > 0 } text }:
                    textBuffer.Append(text);
                    break;
                case ToolUseContent tu:
                    // Flush accumulated text before emitting function_call
                    if (textBuffer.Length > 0)
                    {
                        yield return new JsonObject { ["role"] = "assistant", ["content"] = textBuffer.ToString() };
                        textBuffer.Clear();
                    }
                    // function_call is a top-level item.
                    // id must start with "fc_" (Responses API requirement); call_id is the external tool use ID.
                    yield return new JsonObject
                    {
                        ["type"] = "function_call",
                        ["id"] = "fc_" + tu.Id,
                        ["call_id"] = tu.Id,
                        ["name"] = tu.Name,
                        ["arguments"] = JsonSerializer.Serialize(tu.Input)
                    };
                    break;
            }
        }

        if (textBuffer.Length > 0)
            yield return new JsonObject { ["role"] = "assistant", ["content"] = textBuffer.ToString() };
    }

    // =========================================================================
    // SSE streaming
    // =========================================================================

    private async IAsyncEnumerable<StreamChunk> ParseSseStreamAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Keyed by item.id (not call_id); tracks in-progress function calls.
        var builders = new Dictionary<string, (string CallId, string Name, StringBuilder Args)>();
        using var reader = new StreamReader(stream);
        string? eventType = null;
        var dataBuffer = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) break;

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventType = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                dataBuffer.Append(line[5..].Trim());
            }
            else if (line.Length == 0 && dataBuffer.Length > 0)
            {
                var data = dataBuffer.ToString();
                dataBuffer.Clear();
                var currentEvent = eventType;
                eventType = null;

                if (data == "[DONE]") yield break;

                var (chunks, done) = ProcessSseEvent(currentEvent, data, builders);
                foreach (var chunk in chunks)
                    yield return chunk;

                if (done) yield break;
            }
        }

        // Flush any trailing data (malformed SSE without final blank line)
        if (dataBuffer.Length > 0)
        {
            var (chunks, _) = ProcessSseEvent(eventType, dataBuffer.ToString(), builders);
            foreach (var chunk in chunks)
                yield return chunk;
        }
    }

    private (List<StreamChunk> Chunks, bool Done) ProcessSseEvent(
        string? eventType,
        string data,
        Dictionary<string, (string CallId, string Name, StringBuilder Args)> builders)
    {
        var chunks = new List<StreamChunk>();

        if (string.IsNullOrEmpty(data)) return (chunks, false);

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(data).RootElement;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Failed to parse SSE event: {EventType}", eventType);
            return (chunks, false);
        }

        switch (eventType)
        {
            case "response.output_text.delta":
            {
                if (root.TryGetProperty("delta", out var d) && d.GetString() is { Length: > 0 } delta)
                    chunks.Add(new StreamChunk { Type = StreamChunkType.TextDelta, TextDelta = delta });
                break;
            }

            case "response.output_item.added":
            {
                if (!root.TryGetProperty("item", out var item)) break;
                if (!item.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "function_call") break;

                var itemId = item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
                var callId = item.TryGetProperty("call_id", out var cid) ? cid.GetString() ?? "" : "";
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

                builders[itemId] = (callId, name, new StringBuilder());
                chunks.Add(new StreamChunk
                {
                    Type = StreamChunkType.ToolUseStart,
                    ToolUse = new ToolUseChunk { Id = callId, Name = name }
                });
                break;
            }

            case "response.function_call_arguments.delta":
            {
                var itemId = root.TryGetProperty("item_id", out var id) ? id.GetString() ?? "" : "";
                if (!root.TryGetProperty("delta", out var d) || d.GetString() is not { Length: > 0 } delta) break;
                if (!builders.TryGetValue(itemId, out var builder)) break;

                builder.Args.Append(delta);
                chunks.Add(new StreamChunk
                {
                    Type = StreamChunkType.ToolUseInputDelta,
                    ToolUse = new ToolUseChunk { Id = builder.CallId, InputDelta = delta }
                });
                break;
            }

            case "response.output_item.done":
            {
                if (!root.TryGetProperty("item", out var item)) break;
                if (!item.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "function_call") break;

                var itemId = item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
                if (!builders.Remove(itemId, out var builder)) break;

                object? input = null;
                var argsJson = builder.Args.ToString();
                if (!string.IsNullOrEmpty(argsJson))
                {
                    try { input = JsonSerializer.Deserialize<object>(argsJson); }
                    catch (JsonException ex) { _logger?.LogWarning(ex, "Failed to deserialize tool args"); }
                }

                chunks.Add(new StreamChunk
                {
                    Type = StreamChunkType.ToolUseComplete,
                    ToolUse = new ToolUseChunk
                    {
                        Id = builder.CallId,
                        Name = builder.Name,
                        Input = input
                    }
                });
                break;
            }

            case "response.done":
            {
                if (!root.TryGetProperty("response", out var resp)) break;

                TokenUsage? usage = null;
                if (resp.TryGetProperty("usage", out var u))
                {
                    usage = new TokenUsage
                    {
                        InputTokens = u.TryGetProperty("input_tokens", out var inp) ? inp.GetInt32() : 0,
                        OutputTokens = u.TryGetProperty("output_tokens", out var outp) ? outp.GetInt32() : 0
                    };
                }

                chunks.Add(new StreamChunk
                {
                    Type = StreamChunkType.MessageStop,
                    StopReason = DetectStopReason(resp),
                    Usage = usage
                });
                return (chunks, true);
            }
        }

        return (chunks, false);
    }

    // =========================================================================
    // Non-streaming response parsing
    // =========================================================================

    internal ModelResponse ParseCompleteResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var content = new List<ContentBlock>();

        if (root.TryGetProperty("output", out var output))
        {
            foreach (var item in output.EnumerateArray())
            {
                var itemType = item.TryGetProperty("type", out var t) ? t.GetString() : null;

                if (itemType == "message" && item.TryGetProperty("content", out var parts))
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        var partType = part.TryGetProperty("type", out var pt) ? pt.GetString() : null;
                        if (partType == "output_text" && part.TryGetProperty("text", out var txt))
                            content.Add(new TextContent { Text = txt.GetString() ?? "" });
                    }
                }
                else if (itemType == "function_call")
                {
                    var callId = item.TryGetProperty("call_id", out var cid) ? cid.GetString() ?? "" : "";
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var argsRaw = item.TryGetProperty("arguments", out var args) ? args.GetString() : null;

                    object? input = null;
                    if (!string.IsNullOrEmpty(argsRaw))
                    {
                        try { input = JsonSerializer.Deserialize<object>(argsRaw); }
                        catch { /* keep null */ }
                    }

                    content.Add(new ToolUseContent
                    {
                        Id = callId,
                        Name = name,
                        Input = input ?? new { }
                    });
                }
            }
        }

        TokenUsage usage = new() { InputTokens = 0, OutputTokens = 0 };
        if (root.TryGetProperty("usage", out var usageEl))
        {
            usage = new TokenUsage
            {
                InputTokens = usageEl.TryGetProperty("input_tokens", out var inp) ? inp.GetInt32() : 0,
                OutputTokens = usageEl.TryGetProperty("output_tokens", out var outp) ? outp.GetInt32() : 0
            };
        }

        var stopReason = DetectStopReason(root);
        // If there are tool calls in the output, override to ToolUse
        if (content.OfType<ToolUseContent>().Any())
            stopReason = ModelStopReason.ToolUse;

        var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";

        return new ModelResponse
        {
            Content = content,
            StopReason = stopReason,
            Usage = usage,
            Model = model
        };
    }

    private static ModelStopReason DetectStopReason(JsonElement responseEl)
    {
        // Check output array for function_calls first
        if (responseEl.TryGetProperty("output", out var output))
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var t) && t.GetString() == "function_call")
                    return ModelStopReason.ToolUse;
            }
        }

        var status = responseEl.TryGetProperty("status", out var s) ? s.GetString() : "completed";
        if (status == "incomplete")
        {
            if (responseEl.TryGetProperty("incomplete_details", out var d)
                && d.TryGetProperty("reason", out var r)
                && r.GetString() == "max_output_tokens")
                return ModelStopReason.MaxTokens;
            return ModelStopReason.MaxTokens;
        }

        return ModelStopReason.EndTurn;
    }
}

/// <summary>
/// Options for the OpenAI Responses API provider.
/// </summary>
public class OpenAIResponsesOptions
{
    /// <summary>API key for authentication.</summary>
    public required string ApiKey { get; init; }

    /// <summary>
    /// Base URL override (default: <c>https://api.openai.com/v1</c>).
    /// The <c>/responses</c> path is appended automatically.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>Additional HTTP headers sent with every request.</summary>
    public IReadOnlyDictionary<string, string>? CustomHeaders { get; init; }

    /// <summary>
    /// Retry policy for transient errors (rate limits, overload, timeouts).
    /// Defaults to <see cref="RetryPolicy.Default"/> when null.
    /// </summary>
    public RetryPolicy? RetryPolicy { get; init; }
}
