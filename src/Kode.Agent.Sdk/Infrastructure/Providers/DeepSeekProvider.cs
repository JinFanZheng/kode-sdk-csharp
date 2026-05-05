using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kode.Agent.Sdk.Diagnostics;
using Microsoft.Extensions.Logging;
using ContentBlock = Kode.Agent.Sdk.Core.Types.ContentBlock;
using ImageContent = Kode.Agent.Sdk.Core.Types.ImageContent;
using Message = Kode.Agent.Sdk.Core.Types.Message;
using TextContent = Kode.Agent.Sdk.Core.Types.TextContent;
using ThinkingContent = Kode.Agent.Sdk.Core.Types.ThinkingContent;
using ToolResultContent = Kode.Agent.Sdk.Core.Types.ToolResultContent;
using ToolUseContent = Kode.Agent.Sdk.Core.Types.ToolUseContent;

namespace Kode.Agent.Sdk.Infrastructure.Providers;

/// <summary>
/// DeepSeek model provider implementation using raw HTTP (OpenAI-compatible chat/completions API).
/// Handles DeepSeek's thinking mode (<c>reasoning_content</c>) natively as
/// <see cref="ThinkingContent"/> / <see cref="StreamChunkType.ThinkingDelta"/> blocks
/// instead of the marker-based workaround used by <see cref="OpenAIProvider"/>.
/// </summary>
public sealed class DeepSeekProvider : IModelProvider
{
    private readonly HttpClient _httpClient;
    private readonly DeepSeekOptions _options;
    private readonly RetryPolicy _retryPolicy;
    private readonly ILogger<DeepSeekProvider>? _logger;
    private readonly string _endpoint;

    public string ProviderName => "deepseek";

    /// <inheritdoc />
    public ModelCapabilities? GetModelCapabilities(string modelId)
    {
        // User-configured overrides (no SDK upgrade required)
        if (_options.ModelCapabilitiesOverride?.TryGetValue(modelId, out var caps) == true)
            return caps;
        // Centralised registry (built-in + prefix heuristics)
        return ModelCapabilitiesRegistry.Default.Get(modelId);
    }

    /// <summary>
    /// Production constructor — creates its own <see cref="HttpClientHandler"/>.
    /// </summary>
    public DeepSeekProvider(DeepSeekOptions options, ILogger<DeepSeekProvider>? logger = null)
        : this(new HttpClientHandler(), options, logger)
    {
    }

    /// <summary>
    /// DI constructor — accepts an existing <see cref="HttpClient"/> (connection pooling,
    /// pre-configured timeout). The client's Timeout is forced to
    /// <see cref="Timeout.InfiniteTimeSpan"/>; streaming calls are bounded by
    /// <see cref="CancellationToken"/> instead.
    /// </summary>
    public DeepSeekProvider(HttpClient httpClient, DeepSeekOptions options, ILogger<DeepSeekProvider>? logger = null)
    {
        _logger = logger;
        _retryPolicy = options.RetryPolicy ?? RetryPolicy.Default;
        var baseUrl = options.BaseUrl?.TrimEnd('/') ?? "https://api.deepseek.com/v1";
        _endpoint = baseUrl + "/chat/completions";
        _options = options;

        _httpClient = httpClient;
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.ApiKey);

        if (options.CustomHeaders is { Count: > 0 })
        {
            foreach (var (key, value) in options.CustomHeaders)
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        }
    }

    /// <summary>
    /// Internal constructor for testing — accepts a custom <see cref="HttpMessageHandler"/>.
    /// </summary>
    internal DeepSeekProvider(HttpMessageHandler handler, DeepSeekOptions options, ILogger<DeepSeekProvider>? logger = null)
    {
        _logger = logger;
        _retryPolicy = options.RetryPolicy ?? RetryPolicy.Default;
        var baseUrl = options.BaseUrl?.TrimEnd('/') ?? "https://api.deepseek.com/v1";
        _endpoint = baseUrl + "/chat/completions";
        _options = options;

        _httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.ApiKey);

        if (options.CustomHeaders is { Count: > 0 })
        {
            foreach (var (key, value) in options.CustomHeaders)
                _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        }
    }

    // =========================================================================
    // IModelProvider — streaming
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
        using var providerActivity = KodeAgentActivitySource.Source.StartActivity("provider.stream");
        providerActivity?.SetTag("gen_ai.system", "deepseek");
        providerActivity?.SetTag("gen_ai.request.model", request.Model);

        var streamStopwatch = Stopwatch.StartNew();
        var ttftRecorded = false;

        _logger?.LogDebug("DeepSeek stream starting: model={Model}, tools={ToolCount}",
            request.Model, request.Tools?.Count ?? 0);

        var body = BuildRequestBody(request, stream: true);
        var bodyJson = body.ToJsonString();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger?.LogError("DeepSeek API error {Status}: {Body}", (int)response.StatusCode, errorBody);
            throw new HttpRequestException(
                $"DeepSeek API returned {(int)response.StatusCode} ({response.ReasonPhrase}). Body: {errorBody}",
                inner: null,
                statusCode: response.StatusCode);
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        // Track in-progress tool calls: index → (id, name, args builder)
        var toolCallBuilders = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();
        TokenUsage? finalUsage = null;

        await foreach (var sseEvent in ParseSseStreamAsync(stream, cancellationToken))
        {
            if (sseEvent.TryGetDelta(out var delta))
            {
                // reasoning_content → ThinkingDelta
                if (delta.reasoningContent is { Length: > 0 } rc)
                {
                    if (!ttftRecorded)
                    {
                        ttftRecorded = true;
                        RecordTtft(providerActivity, streamStopwatch, request.Model);
                    }
                    yield return new StreamChunk
                    {
                        Type = StreamChunkType.ThinkingDelta,
                        ThinkingDelta = rc
                    };
                }

                // content → TextDelta
                if (delta.content is { Length: > 0 } c)
                {
                    if (!ttftRecorded)
                    {
                        ttftRecorded = true;
                        RecordTtft(providerActivity, streamStopwatch, request.Model);
                    }
                    yield return new StreamChunk
                    {
                        Type = StreamChunkType.TextDelta,
                        TextDelta = c
                    };
                }

                // tool_calls
                if (delta.toolCalls is { Count: > 0 })
                {
                    foreach (var tc in delta.toolCalls)
                    {
                        // New tool call (has id)
                        if (tc.id is { Length: > 0 })
                        {
                            toolCallBuilders[tc.index] = (tc.id, tc.functionName ?? "", new StringBuilder());
                            if (!ttftRecorded)
                            {
                                ttftRecorded = true;
                                RecordTtft(providerActivity, streamStopwatch, request.Model);
                            }
                            yield return new StreamChunk
                            {
                                Type = StreamChunkType.ToolUseStart,
                                ToolUse = new ToolUseChunk { Id = tc.id, Name = tc.functionName }
                            };
                        }

                        // Tool arguments delta
                        if (tc.functionArguments is { Length: > 0 } argsDelta
                            && toolCallBuilders.TryGetValue(tc.index, out var builder))
                        {
                            builder.Args.Append(argsDelta);
                            yield return new StreamChunk
                            {
                                Type = StreamChunkType.ToolUseInputDelta,
                                ToolUse = new ToolUseChunk
                                {
                                    Id = builder.Id,
                                    InputDelta = argsDelta
                                }
                            };
                        }
                    }
                }
            }
            else if (sseEvent.TryGetFinishReason(out var finishReason, out var usage))
            {
                // Complete pending tool calls
                foreach (var (_, builder) in toolCallBuilders)
                {
                    object? input = null;
                    var argsJson = builder.Args.ToString();
                    if (!string.IsNullOrEmpty(argsJson))
                    {
                        try { input = JsonSerializer.Deserialize<object>(argsJson); }
                        catch (Exception ex) { _logger?.LogWarning(ex, "Failed to deserialize tool args"); }
                    }

                    yield return new StreamChunk
                    {
                        Type = StreamChunkType.ToolUseComplete,
                        ToolUse = new ToolUseChunk
                        {
                            Id = builder.Id,
                            Name = builder.Name,
                            Input = input
                        }
                    };
                }
                toolCallBuilders.Clear();

                if (usage != null) finalUsage = usage;

                streamStopwatch.Stop();
                providerActivity?.SetTag("gen_ai.stream.duration_ms", streamStopwatch.ElapsedMilliseconds);
                if (finalUsage != null)
                {
                    providerActivity?.SetTag("gen_ai.usage.input_tokens", finalUsage.InputTokens);
                    providerActivity?.SetTag("gen_ai.usage.output_tokens", finalUsage.OutputTokens);
                    _logger?.LogDebug(
                        "DeepSeek stream complete: model={Model}, input_tokens={InputTokens}, output_tokens={OutputTokens}, duration_ms={DurationMs}",
                        request.Model, finalUsage.InputTokens, finalUsage.OutputTokens, streamStopwatch.ElapsedMilliseconds);
                }

                yield return new StreamChunk
                {
                    Type = StreamChunkType.MessageStop,
                    StopReason = finishReason,
                    Usage = finalUsage
                };
            }
        }
    }

    // =========================================================================
    // IModelProvider — non-streaming
    // =========================================================================

    public Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default)
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
                Model = _options.ModelId ?? "deepseek-chat",
                Messages = [Message.User("Hi")],
                MaxTokens = 1
            }, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "DeepSeek provider validation failed");
            return false;
        }
    }

    // =========================================================================
    // Request building
    // =========================================================================

    internal JsonObject BuildRequestBody(ModelRequest request, bool stream)
    {
        var body = new JsonObject { ["model"] = request.Model };

        // Messages
        var messages = new JsonArray();
        foreach (var msg in BuildChatMessages(request))
            messages.Add(msg);
        body["messages"] = messages;

        if (stream)
            body["stream"] = true;

        // Max tokens
        if (request.MaxTokens.HasValue)
            body["max_tokens"] = request.MaxTokens.Value;

        // Temperature: DeepSeek requires omitting temperature when thinking is enabled
        if (request.EnableThinking != true && request.Temperature.HasValue)
            body["temperature"] = request.Temperature.Value;

        // Thinking mode
        if (request.EnableThinking == true)
        {
            body["thinking"] = new JsonObject { ["type"] = "enabled" };
            // DeepSeek requires temperature to be omitted or 1.0 when thinking is enabled
            body["temperature"] = 1.0;
        }

        // Stop sequences
        if (request.StopSequences?.Count > 0)
        {
            var stops = new JsonArray();
            foreach (var s in request.StopSequences)
                stops.Add(s);
            body["stop"] = stops;
        }

        // Tools
        if (request.Tools?.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                var schemaJson = tool.InputSchema is JsonElement je
                    ? JsonNode.Parse(je.GetRawText())
                    : JsonNode.Parse(JsonSerializer.Serialize(tool.InputSchema));

                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = schemaJson
                    }
                });
            }
            body["tools"] = tools;
        }

        return body;
    }

    private List<JsonNode> BuildChatMessages(ModelRequest request)
    {
        var messages = new List<JsonNode>();

        // System prompt
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = request.SystemPrompt
            });
        }

        // Memory blocks (core-memory / context-summary) from system-role messages
        foreach (var msg in request.Messages)
        {
            if (msg.Role != MessageRole.System) continue;
            foreach (var text in msg.Content.OfType<TextContent>())
            {
                if (!string.IsNullOrWhiteSpace(text.Text))
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "system",
                        ["content"] = text.Text
                    });
                }
            }
        }

        // Coalesce consecutive same-role messages (DeepSeek requires alternating roles,
        // and every tool_use must have its tool_result in the immediately next message)
        var nonSystem = request.Messages.Where(m => m.Role != MessageRole.System).ToList();
        var coalesced = CoalesceConsecutiveSameRoleMessages(nonSystem);

        foreach (var msg in coalesced)
        {
            foreach (var msgNode in ConvertMessages(msg))
            {
                if (msgNode != null)
                    messages.Add(msgNode);
            }
        }

        return messages;
    }

    /// <summary>
    /// Merges consecutive messages of the same role into a single message.
    /// DeepSeek (like Anthropic) requires strictly alternating user/assistant roles.
    /// </summary>
    private static List<Message> CoalesceConsecutiveSameRoleMessages(List<Message> messages)
    {
        if (messages.Count <= 1) return messages;

        var result = new List<Message>(messages.Count);
        var current = messages[0];

        for (var i = 1; i < messages.Count; i++)
        {
            var next = messages[i];
            if (next.Role == current.Role)
            {
                var merged = new List<ContentBlock>(current.Content);
                merged.AddRange(next.Content);
                current = current with { Content = merged };
            }
            else
            {
                result.Add(current);
                current = next;
            }
        }
        result.Add(current);
        return result;
    }

    /// <summary>
    /// Converts an SDK message into one or more chat/completions message JSON nodes.
    /// User messages with <see cref="ToolResultContent"/> blocks are expanded into
    /// individual <c>role: "tool"</c> messages so every <c>tool_call_id</c> is answered.
    /// </summary>
    private static IEnumerable<JsonObject?> ConvertMessages(Message msg)
    {
        if (msg.Role == MessageRole.User)
        {
            var toolResults = msg.Content.OfType<ToolResultContent>().ToList();
            var textContent = string.Join("", msg.Content.OfType<TextContent>().Select(t => t.Text));
            var hasImages = msg.Content.OfType<ImageContent>().Any();

            // If there are tool results, emit each as a separate tool message.
            // This is required by DeepSeek (and OpenAI-compatible APIs): every
            // assistant message with tool_calls must be followed by one tool
            // message per tool_call_id.
            if (toolResults.Count > 0)
            {
                foreach (var tr in toolResults)
                {
                    var output = tr.Content is string s ? s : JsonSerializer.Serialize(tr.Content);
                    yield return new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = tr.ToolUseId,
                        ["content"] = output
                    };
                }
                // Non-tool-result content (text/images) in the same User message
                // is dropped — it doesn't fit the required message ordering.
                yield break;
            }

            if (hasImages)
            {
                var parts = new JsonArray();
                if (!string.IsNullOrEmpty(textContent))
                {
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = textContent });
                }
                foreach (var img in msg.Content.OfType<ImageContent>())
                {
                    if (img.Url is not null)
                    {
                        parts.Add(new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = img.Url }
                        });
                    }
                    else if (img.Data is not null)
                    {
                        parts.Add(new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject
                            {
                                ["url"] = $"data:{img.MediaType ?? "image/png"};base64,{img.Data}"
                            }
                        });
                    }
                }
                yield return new JsonObject { ["role"] = "user", ["content"] = parts };
                yield break;
            }

            // Plain text user message
            if (!string.IsNullOrEmpty(textContent))
                yield return new JsonObject { ["role"] = "user", ["content"] = textContent };

            yield break;
        }

        if (msg.Role == MessageRole.Assistant)
        {
            var thinkingText = string.Join("", msg.Content.OfType<ThinkingContent>().Select(t => t.Thinking));
            var visibleText = string.Join("", msg.Content.OfType<TextContent>().Select(t => t.Text));
            var toolUses = msg.Content.OfType<ToolUseContent>().ToList();

            var assistantObj = new JsonObject { ["role"] = "assistant" };

            // Content: visible text (or empty string if there are only tool calls)
            if (toolUses.Count > 0 && string.IsNullOrEmpty(visibleText))
            {
                assistantObj["content"] = string.Empty;
            }
            else if (!string.IsNullOrEmpty(visibleText))
            {
                assistantObj["content"] = visibleText;
            }
            else if (!string.IsNullOrEmpty(thinkingText))
            {
                // Only thinking, no visible text and no tools
                assistantObj["content"] = string.Empty;
            }

            // reasoning_content: pass thinking back verbatim
            if (!string.IsNullOrEmpty(thinkingText))
            {
                assistantObj["reasoning_content"] = thinkingText;
            }

            // tool_calls
            if (toolUses.Count > 0)
            {
                var toolCalls = new JsonArray();
                foreach (var tu in toolUses)
                {
                    var callObj = new JsonObject
                    {
                        ["id"] = tu.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = tu.Name,
                            ["arguments"] = tu.Input is JsonElement je
                                ? je.GetRawText()
                                : JsonSerializer.Serialize(tu.Input)
                        }
                    };
                    toolCalls.Add(callObj);
                }
                assistantObj["tool_calls"] = toolCalls;
            }

            yield return assistantObj;
            yield break;
        }

        yield break;
    }

    // =========================================================================
    // Response parsing — non-streaming
    // =========================================================================

    internal ModelResponse ParseCompleteResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var content = new List<ContentBlock>();

        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message))
            {
                // reasoning_content → ThinkingContent (must come before text)
                if (message.TryGetProperty("reasoning_content", out var rc)
                    && rc.GetString() is { Length: > 0 } reasoning)
                {
                    content.Add(new ThinkingContent { Thinking = reasoning });
                }

                // content → TextContent
                if (message.TryGetProperty("content", out var c)
                    && c.GetString() is { Length: > 0 } text)
                {
                    content.Add(new TextContent { Text = text });
                }

                // tool_calls → ToolUseContent
                if (message.TryGetProperty("tool_calls", out var toolCalls))
                {
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                        var func = tc.TryGetProperty("function", out var f) ? f : default;
                        var name = func.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var argsRaw = func.TryGetProperty("arguments", out var a) ? a.GetString() : null;

                        object? input = null;
                        if (!string.IsNullOrEmpty(argsRaw))
                        {
                            try { input = JsonSerializer.Deserialize<object>(argsRaw); }
                            catch { input = argsRaw; }
                        }

                        content.Add(new ToolUseContent
                        {
                            Id = id,
                            Name = name,
                            Input = input ?? new { }
                        });
                    }
                }
            }
        }

        var stopReason = ConvertFinishReason(
            root.TryGetProperty("choices", out var cs)
            && cs.GetArrayLength() > 0
            && cs[0].TryGetProperty("finish_reason", out var fr)
                ? fr.GetString()
                : null);

        // Override to ToolUse if we have tool calls in content
        if (content.OfType<ToolUseContent>().Any())
            stopReason = ModelStopReason.ToolUse;

        var usage = ParseTokenUsage(root);
        // Override with detailed usage if present
        if (root.TryGetProperty("usage", out var u))
        {
            usage = ParseTokenUsage(u);
        }

        var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";

        return new ModelResponse
        {
            Content = content,
            StopReason = stopReason,
            Usage = usage,
            Model = model
        };
    }

    // =========================================================================
    // SSE streaming
    // =========================================================================

    private static async IAsyncEnumerable<SseEvent> ParseSseStreamAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var dataBuffer = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) break;

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                dataBuffer.Append(line[5..].Trim());
            }
            else if (line.Length == 0 && dataBuffer.Length > 0)
            {
                var data = dataBuffer.ToString();
                dataBuffer.Clear();

                if (data == "[DONE]")
                    yield break;

                if (TryParseSseData(data, out var sseEvent))
                    yield return sseEvent;
            }
        }

        // Flush trailing data (malformed SSE without final blank line)
        if (dataBuffer.Length > 0)
        {
            if (TryParseSseData(dataBuffer.ToString(), out var sseEvent))
                yield return sseEvent;
        }
    }

    private static bool TryParseSseData(string data, out SseEvent result)
    {
        result = default;
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return false;

            var choice = choices[0];
            var finishReason = choice.TryGetProperty("finish_reason", out var fr)
                && fr.ValueKind != JsonValueKind.Null
                    ? fr.GetString()
                    : null;

            if (finishReason != null)
            {
                TokenUsage? usage = null;
                if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    usage = ParseTokenUsage(u);
                }

                result = SseEvent.CreateFinish(ConvertFinishReason(finishReason), usage);
                return true;
            }

            if (choice.TryGetProperty("delta", out var delta))
            {
                string? content = null;
                string? reasoningContent = null;
                var toolCalls = new List<(int index, string? id, string? functionName, string? functionArguments)>();

                if (delta.TryGetProperty("content", out var c)
                    && c.ValueKind != JsonValueKind.Null)
                    content = c.GetString();

                if (delta.TryGetProperty("reasoning_content", out var rc)
                    && rc.ValueKind != JsonValueKind.Null)
                    reasoningContent = rc.GetString();

                if (delta.TryGetProperty("tool_calls", out var tcs)
                    && tcs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        var index = tc.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;
                        var id = tc.TryGetProperty("id", out var idEl)
                            && idEl.ValueKind != JsonValueKind.Null ? idEl.GetString() : null;
                        string? funcName = null;
                        string? funcArgs = null;
                        if (tc.TryGetProperty("function", out var func) && func.ValueKind == JsonValueKind.Object)
                        {
                            if (func.TryGetProperty("name", out var fn)
                                && fn.ValueKind != JsonValueKind.Null)
                                funcName = fn.GetString();
                            if (func.TryGetProperty("arguments", out var fa)
                                && fa.ValueKind != JsonValueKind.Null)
                                funcArgs = fa.GetString();
                        }
                        toolCalls.Add((index, id, funcName, funcArgs));
                    }
                }

                result = SseEvent.CreateDelta(content, reasoningContent, toolCalls);
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Parses TokenUsage from a usage JsonElement, including cache hit/miss tokens
    /// when the provider reports them (DeepSeek V4 API returns
    /// <c>prompt_cache_hit_tokens</c> and <c>prompt_cache_miss_tokens</c>).
    /// </summary>
    private static TokenUsage ParseTokenUsage(JsonElement usageEl)
    {
        var usage = new TokenUsage
        {
            InputTokens = usageEl.TryGetProperty("prompt_tokens", out var inp) ? inp.GetInt32() : 0,
            OutputTokens = usageEl.TryGetProperty("completion_tokens", out var outp) ? outp.GetInt32() : 0,
        };

        // DeepSeek V4 API returns cache tokens; older/compatible providers may not.
        if (usageEl.TryGetProperty("prompt_cache_hit_tokens", out var hitEl))
            usage = usage with { CacheHitTokens = hitEl.GetInt32() };
        if (usageEl.TryGetProperty("prompt_cache_miss_tokens", out var missEl))
            usage = usage with { CacheMissTokens = missEl.GetInt32() };

        return usage;
    }

    private static ModelStopReason ConvertFinishReason(string? finishReason)
    {
        return finishReason switch
        {
            "stop" => ModelStopReason.EndTurn,
            "length" => ModelStopReason.MaxTokens,
            "tool_calls" => ModelStopReason.ToolUse,
            "content_filter" => ModelStopReason.EndTurn,
            _ => ModelStopReason.EndTurn
        };
    }

    private static void RecordTtft(
        System.Diagnostics.Activity? activity,
        Stopwatch stopwatch,
        string model)
    {
        var ttftMs = stopwatch.ElapsedMilliseconds;
        activity?.SetTag("gen_ai.client.time_to_first_token_ms", ttftMs);
        KodeAgentMetrics.ModelTtft.Record(ttftMs,
            new TagList { { "model", model }, { "provider", "deepseek" } });
    }

    // =========================================================================
    // SSE event helper struct
    // =========================================================================

    private readonly record struct SseEvent
    {
        public string? Content { get; init; }
        public string? ReasoningContent { get; init; }
        public List<(int index, string? id, string? functionName, string? functionArguments)>? ToolCalls { get; init; }
        public ModelStopReason? FinishReason { get; init; }
        public TokenUsage? Usage { get; init; }

        public bool TryGetDelta(out (string? content, string? reasoningContent,
            List<(int index, string? id, string? functionName, string? functionArguments)> toolCalls) delta)
        {
            if (FinishReason == null && Usage == null)
            {
                delta = (Content, ReasoningContent, ToolCalls ?? []);
                return true;
            }
            delta = default;
            return false;
        }

        public bool TryGetFinishReason(out ModelStopReason reason, out TokenUsage? usage)
        {
            if (FinishReason.HasValue)
            {
                reason = FinishReason.Value;
                usage = Usage;
                return true;
            }
            reason = default;
            usage = null;
            return false;
        }

        public static SseEvent CreateDelta(
            string? content,
            string? reasoningContent,
            List<(int, string?, string?, string?)> toolCalls)
            => new()
            {
                Content = content,
                ReasoningContent = reasoningContent,
                ToolCalls = toolCalls
            };

        public static SseEvent CreateFinish(ModelStopReason reason, TokenUsage? usage)
            => new() { FinishReason = reason, Usage = usage };
    }
}

/// <summary>
/// Options for configuring the DeepSeek provider.
/// </summary>
public class DeepSeekOptions
{
    /// <summary>
    /// The API key for authentication.
    /// </summary>
    public required string ApiKey { get; init; }

    /// <summary>
    /// The base URL for the API (default: <c>https://api.deepseek.com/v1</c>).
    /// The <c>/chat/completions</c> path is appended automatically.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// The default model ID to use.
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Custom HTTP request headers to add to each request.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CustomHeaders { get; init; }

    /// <summary>
    /// Retry policy for transient errors (rate limits, overload, timeouts).
    /// Defaults to <see cref="RetryPolicy.Default"/> when null.
    /// </summary>
    public RetryPolicy? RetryPolicy { get; init; }

    /// <summary>
    /// Optional per-model capability overrides. Entries here take precedence over
    /// the SDK's built-in registry, so new models can be configured without an
    /// SDK upgrade. Key is the model ID (case-insensitive).
    /// </summary>
    public IReadOnlyDictionary<string, ModelCapabilities>? ModelCapabilitiesOverride { get; init; }
}
