using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Kode.Agent.Sdk.Diagnostics;
using Microsoft.Extensions.Logging;
using ContentBlock = Kode.Agent.Sdk.Core.Types.ContentBlock;
using ImageContent = Kode.Agent.Sdk.Core.Types.ImageContent;
using Message = Kode.Agent.Sdk.Core.Types.Message;
using TextContent = Kode.Agent.Sdk.Core.Types.TextContent;
using ToolResultContent = Kode.Agent.Sdk.Core.Types.ToolResultContent;
using ToolUseContent = Kode.Agent.Sdk.Core.Types.ToolUseContent;
using AnthropicStopReason = Anthropic.Models.Messages.StopReason;

namespace Kode.Agent.Sdk.Infrastructure.Providers;

/// <summary>
/// Anthropic (Claude) model provider implementation using official SDK.
/// </summary>
public sealed class AnthropicProvider : IModelProvider
{
    private readonly IAnthropicClient _client;
    private readonly AnthropicOptions _options;
    private readonly RetryPolicy _retryPolicy;
    private readonly ILogger<AnthropicProvider>? _logger;

    public string ProviderName => "anthropic";

    // ── Model capability registry ────────────────────────────────────────────

    /// <inheritdoc />
    public ModelCapabilities? GetModelCapabilities(string modelId)
    {
        // User-configured overrides (no SDK upgrade required)
        if (_options.ModelCapabilitiesOverride?.TryGetValue(modelId, out var caps) == true)
            return caps;
        // Centralised registry (built-in + prefix heuristics, claude-* already covered)
        return ModelCapabilitiesRegistry.Default.Get(modelId);
    }

    public AnthropicProvider(AnthropicOptions options, ILogger<AnthropicProvider>? logger = null)
    {
        _options = options;
        _retryPolicy = options.RetryPolicy ?? RetryPolicy.Default;
        _logger = logger;

        _client = new AnthropicClient
        {
            ApiKey = options.ApiKey,
            BaseUrl = options.BaseUrl ?? "https://api.anthropic.com"
        };

        // Always use a custom HttpClient with no timeout: streaming responses can run for
        // several minutes (long tool chains, large outputs). The 100-second default causes
        // TaskCanceledException mid-stream. Cancellation is controlled by the CancellationToken
        // passed to StreamAsync/CompleteAsync instead.
        var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        if (options.CustomHeaders is { Count: > 0 })
        {
            foreach (var (key, value) in options.CustomHeaders)
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        }

        _client = _client.WithOptions(opts =>
        {
            opts.HttpClient = httpClient;
            return opts;
        });
    }

    /// <summary>
    /// DI constructor: accepts an IHttpClientFactory-managed HttpClient (connection pooling,
    /// pre-configured Timeout). Custom headers, if any, are added to DefaultRequestHeaders.
    /// </summary>
    public AnthropicProvider(HttpClient httpClient, AnthropicOptions options, ILogger<AnthropicProvider>? logger = null)
    {
        _options = options;
        _retryPolicy = options.RetryPolicy ?? RetryPolicy.Default;
        _logger = logger;

        _client = new AnthropicClient
        {
            ApiKey = options.ApiKey,
            BaseUrl = options.BaseUrl ?? "https://api.anthropic.com"
        };

        if (options.CustomHeaders is { Count: > 0 })
            foreach (var (key, value) in options.CustomHeaders)
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, value);

        _client = _client.WithOptions(opts =>
        {
            opts.HttpClient = httpClient;
            return opts;
        });
    }

    public async IAsyncEnumerable<StreamChunk> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var parameters = BuildMessageParameters(request);
        await foreach (var chunk in ProviderRetryHelper.StreamWithRetryAsync(
            ct => StreamCoreAsync(parameters, ct),
            _retryPolicy, _logger, ProviderName, cancellationToken))
        {
            yield return chunk;
        }
    }

    private async IAsyncEnumerable<StreamChunk> StreamCoreAsync(
        MessageCreateParams parameters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var providerActivity = KodeAgentActivitySource.Source.StartActivity("provider.stream");
        providerActivity?.SetTag("gen_ai.system", "anthropic");
        providerActivity?.SetTag("gen_ai.request.model", parameters.Model);

        var streamStopwatch = Stopwatch.StartNew();
        var ttftRecorded = false;

        _logger?.LogDebug("Anthropic stream starting: model={Model}, tools={ToolCount}",
            parameters.Model, parameters.Tools?.Count ?? 0);

        var toolIdMap = new Dictionary<long, string>();
        var toolNameMap = new Dictionary<long, string>();
        var toolInputBuilders = new Dictionary<long, System.Text.StringBuilder>();
        // Track thinking blocks: index → accumulated signature (text arrives via ThinkingDelta,
        // signature arrives via SignatureDelta and must be returned verbatim in future turns).
        var thinkingBlockIndexes = new HashSet<long>();
        var thinkingSignatures = new Dictionary<long, string>();
        long messageStartInputTokens = 0;

        await foreach (var evt in _client.Messages.CreateStreaming(parameters, cancellationToken))
        {
            // Handle message start — input_tokens live here, not in message_delta
            if (evt.TryPickStart(out var messageStartEvent))
            {
                messageStartInputTokens = messageStartEvent.Message.Usage.InputTokens;
                continue;
            }

            // Fallback: Anthropic-compatible APIs (e.g. ZhiPu) may not parse cleanly via TryPickStart.
            // Try raw JSON to extract input_tokens from message_start.
            if (messageStartInputTokens == 0 && TryParseRawMessageStartTokens(evt.Json, out var rawInputTokens))
            {
                messageStartInputTokens = rawInputTokens;
                continue;
            }

            // Handle content block start
            if (evt.TryPickContentBlockStart(out var startEvent))
            {
                // Track thinking block indexes so we can capture their signatures.
                if (startEvent.ContentBlock.TryPickThinking(out _))
                {
                    thinkingBlockIndexes.Add(startEvent.Index);
                }

                if (startEvent.ContentBlock.TryPickToolUse(out var toolUse))
                {
                    toolIdMap[startEvent.Index] = toolUse.ID;
                    toolNameMap[startEvent.Index] = toolUse.Name;
                    toolInputBuilders[startEvent.Index] = new System.Text.StringBuilder();

                    if (!ttftRecorded)
                    {
                        ttftRecorded = true;
                        var ttftMs = streamStopwatch.ElapsedMilliseconds;
                        providerActivity?.SetTag("gen_ai.client.time_to_first_token_ms", ttftMs);
                        KodeAgentMetrics.ModelTtft.Record(ttftMs,
                            new TagList { { "model", parameters.Model }, { "provider", "anthropic" } });
                        _logger?.LogDebug("Anthropic TTFT: model={Model}, ttft_ms={TtftMs}", parameters.Model, ttftMs);
                    }

                    yield return new StreamChunk
                    {
                        Type = StreamChunkType.ToolUseStart,
                        ToolUse = new ToolUseChunk
                        {
                            Id = toolUse.ID,
                            Name = toolUse.Name
                        }
                    };
                }

                continue;
            }

            // Fallback: SDK 12.9.0+ requires a "caller" field in tool_use content blocks
            // that Anthropic-compatible APIs (e.g. BigModel/ZhiPu) do not include.
            // When TryPickContentBlockStart fails, try parsing the raw JSON to detect tool_use.
            if (TryParseRawToolUseContentBlockStart(evt.Json, out var rawIndex, out var rawId, out var rawName))
            {
                toolIdMap[rawIndex] = rawId;
                toolNameMap[rawIndex] = rawName;
                toolInputBuilders[rawIndex] = new System.Text.StringBuilder();

                yield return new StreamChunk
                {
                    Type = StreamChunkType.ToolUseStart,
                    ToolUse = new ToolUseChunk
                    {
                        Id = rawId,
                        Name = rawName
                    }
                };

                continue;
            }

            // Handle content block delta
            if (evt.TryPickContentBlockDelta(out var deltaEvent))
            {
                if (deltaEvent.Delta.TryPickText(out var textDelta))
                {
                    if (!ttftRecorded)
                    {
                        ttftRecorded = true;
                        var ttftMs = streamStopwatch.ElapsedMilliseconds;
                        providerActivity?.SetTag("gen_ai.client.time_to_first_token_ms", ttftMs);
                        KodeAgentMetrics.ModelTtft.Record(ttftMs,
                            new TagList { { "model", parameters.Model }, { "provider", "anthropic" } });
                        _logger?.LogDebug("Anthropic TTFT: model={Model}, ttft_ms={TtftMs}", parameters.Model, ttftMs);
                    }

                    yield return new StreamChunk
                    {
                        Type = StreamChunkType.TextDelta,
                        TextDelta = textDelta.Text
                    };
                }
                else if (deltaEvent.Delta.TryPickInputJson(out var jsonDelta))
                {
                    if (toolInputBuilders.TryGetValue(deltaEvent.Index, out var builder))
                    {
                        builder.Append(jsonDelta.PartialJson);
                    }

                    var toolId = toolIdMap.GetValueOrDefault(deltaEvent.Index, deltaEvent.Index.ToString());
                    var toolName = toolNameMap.GetValueOrDefault(deltaEvent.Index, "");

                    yield return new StreamChunk
                    {
                        Type = StreamChunkType.ToolUseInputDelta,
                        ToolUse = new ToolUseChunk
                        {
                            Id = toolId,
                            Name = toolName,
                            InputDelta = jsonDelta.PartialJson
                        }
                    };
                }
                else if (deltaEvent.Delta.TryPickThinking(out var thinkingDelta))
                {
                    yield return new StreamChunk
                    {
                        Type = StreamChunkType.ThinkingDelta,
                        ThinkingDelta = thinkingDelta.Thinking
                    };
                }
                else if (deltaEvent.Delta.TryPickSignature(out var signatureDelta)
                         && thinkingBlockIndexes.Contains(deltaEvent.Index))
                {
                    // Accumulate signature for this thinking block (usually a single delta).
                    if (!thinkingSignatures.TryGetValue(deltaEvent.Index, out var existing))
                        thinkingSignatures[deltaEvent.Index] = signatureDelta.Signature;
                    else
                        thinkingSignatures[deltaEvent.Index] = existing + signatureDelta.Signature;

                    // Emit a ThinkingDelta chunk carrying only the signature so the agent
                    // layer can store it for future turns without duplicating thinking text.
                    yield return new StreamChunk
                    {
                        Type = StreamChunkType.ThinkingDelta,
                        ThinkingSignature = signatureDelta.Signature
                    };
                }

                continue;
            }

            // Handle content block stop
            if (evt.TryPickContentBlockStop(out var stopEvent))
            {
                if (toolInputBuilders.TryGetValue(stopEvent.Index, out var builder))
                {
                    var inputJson = builder.ToString();
                    toolInputBuilders.Remove(stopEvent.Index);

                    object? input = null;
                    if (!string.IsNullOrEmpty(inputJson))
                    {
                        try
                        {
                            input = JsonSerializer.Deserialize<object>(inputJson);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Failed to deserialize tool input JSON for tool index {Index}", stopEvent.Index);
                        }
                    }

                    var toolId = toolIdMap.GetValueOrDefault(stopEvent.Index, stopEvent.Index.ToString());
                    toolIdMap.Remove(stopEvent.Index);
                    toolNameMap.Remove(stopEvent.Index);

                    yield return new StreamChunk
                    {
                        Type = StreamChunkType.ToolUseComplete,
                        ToolUse = new ToolUseChunk
                        {
                            Id = toolId,
                            Input = input
                        }
                    };
                }

                continue;
            }

            // Handle message delta
            if (evt.TryPickDelta(out var messageDelta))
            {
                var apiStopReason = messageDelta.Delta.StopReason;
                var stopReason = apiStopReason != null
                    ? ConvertStopReason((AnthropicStopReason)apiStopReason)
                    : ModelStopReason.EndTurn;

                // Providers fronted by Anthropic's SSE shape sometimes return non-standard
                // stop_reason strings on HTTP 200 (e.g. GLM's "model_context_window_exceeded").
                // The SDK nullifies unknown enum values, so we inspect the raw JSON and map
                // known overflow signals to ContextOverflow — the run loop treats that as an
                // explicit signal to force-compress and retry.
                if (TryParseRawStopReason(evt.Json, out var rawStopReason) &&
                    IsContextOverflowSignal(rawStopReason))
                {
                    stopReason = ModelStopReason.ContextOverflow;
                    _logger?.LogWarning(
                        "Provider reported non-standard overflow stop_reason: {StopReason} (model={Model})",
                        rawStopReason, parameters.Model);
                }

                var inputTokens = (int)(messageStartInputTokens > 0 ? messageStartInputTokens : (messageDelta.Usage.InputTokens ?? 0));
                var outputTokens = (int)messageDelta.Usage.OutputTokens;

                // Fallback: if SDK gave us zeros, try raw JSON
                if (inputTokens == 0 && outputTokens == 0)
                    TryParseRawMessageDeltaTokens(evt.Json, out inputTokens, out outputTokens);

                streamStopwatch.Stop();
                providerActivity?.SetTag("gen_ai.usage.input_tokens", inputTokens);
                providerActivity?.SetTag("gen_ai.usage.output_tokens", outputTokens);
                providerActivity?.SetTag("gen_ai.stream.duration_ms", streamStopwatch.ElapsedMilliseconds);
                _logger?.LogDebug(
                    "Anthropic stream complete: model={Model}, input_tokens={InputTokens}, output_tokens={OutputTokens}, duration_ms={DurationMs}",
                    parameters.Model, inputTokens, outputTokens, streamStopwatch.ElapsedMilliseconds);

                yield return new StreamChunk
                {
                    Type = StreamChunkType.MessageStop,
                    StopReason = stopReason,
                    Usage = new TokenUsage
                    {
                        InputTokens = inputTokens,
                        OutputTokens = outputTokens
                    }
                };
                continue;
            }

            // Handle message stop
            if (evt.TryPickStop(out _))
            {
                yield return new StreamChunk { Type = StreamChunkType.MessageStop };
            }
        }
    }

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        var parameters = BuildMessageParameters(request);
        return ProviderRetryHelper.ExecuteWithRetryAsync(
            async ct =>
            {
                var response = await _client.Messages.Create(parameters, ct);
                return ConvertToModelResponse(response);
            },
            _retryPolicy, _logger, ProviderName, cancellationToken);
    }

    public async Task<bool> ValidateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new ModelRequest
            {
                Model = _options.ModelId ?? "claude-3-5-haiku-20241022",
                Messages = [Message.User("Hi")],
                MaxTokens = 1
            };

            await CompleteAsync(request, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Anthropic provider validation failed");
            return false;
        }
    }

    private MessageCreateParams BuildMessageParameters(ModelRequest request)
    {
        // Collect memory blocks carried as system-role messages (e.g. core-memory and
        // context-summary emitted by ContextManager). These MUST reach the model — dropping
        // them defeats the three-layer compression architecture and causes the model to
        // re-overflow on the same conversation.
        var memoryTexts = new List<string>();
        foreach (var m in request.Messages)
        {
            if (m.Role != MessageRole.System) continue;
            foreach (var block in m.Content)
            {
                if (block is TextContent t && !string.IsNullOrWhiteSpace(t.Text))
                    memoryTexts.Add(t.Text);
            }
        }

        // Coalesce consecutive messages of the same role before sending to the Anthropic API.
        // This is a defensive measure that handles edge cases where the agent's internal state
        // has consecutive user messages (e.g., after AutoSealDanglingToolUsesAsync places a
        // synthetic tool_result user message before an existing partial-result user message).
        // Anthropic requires every tool_use to have a corresponding tool_result in the
        // IMMEDIATELY NEXT message, so consecutive user messages must be merged into one.
        var nonSystemMessages = request.Messages
            .Where(m => m.Role != MessageRole.System)
            .ToList();
        var messages = CoalesceConsecutiveSameRoleMessages(nonSystemMessages)
            .Select(ConvertMessage)
            .ToList();

        var tools = request.Tools?.Select(t => new ToolUnion(new Tool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = ConvertInputSchema(t.InputSchema)
        })).ToList();

        var stopSequences = request.StopSequences?.Count > 0
            ? request.StopSequences.ToList()
            : null;

        ThinkingConfigParam? thinking = request.EnableThinking == true
            ? new ThinkingConfigParam(new ThinkingConfigEnabled { BudgetTokens = request.ThinkingBudget ?? 8000 })
            : null;

        // MessageCreateParams.System accepts either a plain string or List<TextBlockParam>
        // via implicit conversion (see Anthropic SDK 12.9.0 MessageCreateParamsSystem).
        // Use the list form whenever memory blocks are present so each block is delivered
        // as a separate system content part (matches Anthropic's documented multi-block layout).
        MessageCreateParamsSystem system;
        if (memoryTexts.Count == 0)
        {
            system = !string.IsNullOrEmpty(request.SystemPrompt) ? request.SystemPrompt : null!;
        }
        else
        {
            var blocks = new List<TextBlockParam>();
            if (!string.IsNullOrEmpty(request.SystemPrompt))
                blocks.Add(new TextBlockParam { Text = request.SystemPrompt });
            foreach (var text in memoryTexts)
                blocks.Add(new TextBlockParam { Text = text });
            system = blocks;
        }

        return new MessageCreateParams
        {
            Model = request.Model,
            Messages = messages,
            MaxTokens = request.MaxTokens ?? 4096,
            Temperature = request.EnableThinking == true ? 1.0 : request.Temperature,
            System = system,
            Tools = tools,
            StopSequences = stopSequences,
            Thinking = thinking
        };
    }

    /// <summary>
    /// Merges consecutive messages of the same role into a single message.
    /// Required because Anthropic's API demands strictly alternating user/assistant roles,
    /// and every tool_use must have its tool_result in the IMMEDIATELY NEXT message.
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
                current = current with { Content = NormalizeAnthropicMessageContent(current.Role, merged) };
            }
            else
            {
                result.Add(current with { Content = NormalizeAnthropicMessageContent(current.Role, current.Content) });
                current = next;
            }
        }
        result.Add(current with { Content = NormalizeAnthropicMessageContent(current.Role, current.Content) });
        return result;
    }

    private static List<ContentBlock> NormalizeAnthropicMessageContent(MessageRole role, IReadOnlyList<ContentBlock> content)
    {
        if (content.Count <= 1)
            return content.ToList();

        var normalized = new List<ContentBlock>(content.Count);
        HashSet<string>? seenToolResultIds = null;
        HashSet<string>? seenToolUseIds = null;

        foreach (var block in content)
        {
            switch (block)
            {
                case ToolResultContent toolResult when role == MessageRole.User:
                    seenToolResultIds ??= new HashSet<string>(StringComparer.Ordinal);
                    if (!seenToolResultIds.Add(toolResult.ToolUseId))
                        continue;
                    break;

                case ToolUseContent toolUse when role == MessageRole.Assistant:
                    seenToolUseIds ??= new HashSet<string>(StringComparer.Ordinal);
                    if (!seenToolUseIds.Add(toolUse.Id))
                        continue;
                    break;
            }

            normalized.Add(block);
        }

        return normalized;
    }

    private static MessageParam ConvertMessage(Message msg)
    {
        var role = msg.Role == MessageRole.User ? Role.User : Role.Assistant;
        var content = msg.Content.Select(ConvertContentBlock).ToList();

        return new MessageParam
        {
            Role = role,
            Content = content
        };
    }

    private static ContentBlockParam ConvertContentBlock(ContentBlock block)
    {
        return block switch
        {
            TextContent text => new ContentBlockParam(new TextBlockParam { Text = text.Text }),
            ImageContent img when img.Url is not null =>
                new ContentBlockParam(new ImageBlockParam
                {
                    Source = new UrlImageSource { Url = img.Url }
                }),
            ImageContent img when img.Data is not null =>
                new ContentBlockParam(new ImageBlockParam
                {
                    Source = new Base64ImageSource
                    {
                        MediaType = img.MediaType switch
                        {
                            "image/jpeg" => MediaType.ImageJpeg,
                            "image/gif"  => MediaType.ImageGif,
                            "image/webp" => MediaType.ImageWebP,
                            _            => MediaType.ImagePng,
                        },
                        Data = img.Data
                    }
                }),
            ToolUseContent toolUse => new ContentBlockParam(new ToolUseBlockParam
            {
                ID = toolUse.Id,
                Name = toolUse.Name,
                Input = ConvertToolInput(toolUse.Input)
            }),
            ToolResultContent toolResult => new ContentBlockParam(new ToolResultBlockParam(toolResult.ToolUseId)
            {
                Content = JsonSerializer.Serialize(toolResult.Content) ?? "", // Fixed by Nietzsche: use JSON serialization instead of ToString() for anonymous types
                IsError = toolResult.IsError
            }),
            // Thinking blocks MUST be passed back verbatim (with signature) for Anthropic and
            // Anthropic-compatible providers (e.g. DeepSeek). Sending them as plain text causes
            // a 400 "thinking content must be passed back" error on subsequent turns.
            ThinkingContent thinking => new ContentBlockParam(new ThinkingBlockParam
            {
                Thinking = thinking.Thinking,
                Signature = thinking.Signature ?? string.Empty
            }),
            _ => new ContentBlockParam(new TextBlockParam { Text = "" })
        };
    }

    private static IReadOnlyDictionary<string, JsonElement> ConvertToolInput(object? input)
    {
        if (input == null)
            return new Dictionary<string, JsonElement>();

        if (input is IReadOnlyDictionary<string, JsonElement> readOnlyDict)
            return readOnlyDict;

        if (input is Dictionary<string, JsonElement> dict)
            return dict;

        if (input is JsonElement { ValueKind: JsonValueKind.Object } jsonElement)
        {
            var result = new Dictionary<string, JsonElement>();
            foreach (var prop in jsonElement.EnumerateObject())
            {
                result[prop.Name] = prop.Value.Clone();
            }

            return result;
        }

        // Serialize and deserialize to get proper JsonElement dictionary
        var json = JsonSerializer.Serialize(input);
        var element = JsonSerializer.Deserialize<JsonElement>(json);
        if (element.ValueKind == JsonValueKind.Object)
        {
            var result = new Dictionary<string, JsonElement>();
            foreach (var prop in element.EnumerateObject())
            {
                result[prop.Name] = prop.Value.Clone();
            }

            return result;
        }

        return new Dictionary<string, JsonElement>();
    }

    private static InputSchema ConvertInputSchema(object schema)
    {
        if (schema is JsonElement jsonElement)
        {
            var properties = new Dictionary<string, JsonElement>();
            var required = new List<string>();

            if (jsonElement.TryGetProperty("properties", out var propsElement) &&
                propsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in propsElement.EnumerateObject())
                {
                    properties[prop.Name] = prop.Value.Clone();
                }
            }

            if (jsonElement.TryGetProperty("required", out var reqElement) &&
                reqElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in reqElement.EnumerateArray())
                {
                    if (item.GetString() is { } reqProp)
                    {
                        required.Add(reqProp);
                    }
                }
            }

            return new InputSchema
            {
                Properties = properties,
                Required = required
            };
        }

        // For other object types, serialize and parse
        var json = JsonSerializer.Serialize(schema);
        var element = JsonSerializer.Deserialize<JsonElement>(json);
        return ConvertInputSchema(element);
    }

    private static ModelStopReason ConvertStopReason(AnthropicStopReason? stopReason)
    {
        return stopReason switch
        {
            AnthropicStopReason.EndTurn => ModelStopReason.EndTurn,
            AnthropicStopReason.MaxTokens => ModelStopReason.MaxTokens,
            AnthropicStopReason.StopSequence => ModelStopReason.StopSequence,
            AnthropicStopReason.ToolUse => ModelStopReason.ToolUse,
            _ => ModelStopReason.EndTurn
        };
    }

    private static ModelResponse ConvertToModelResponse(Anthropic.Models.Messages.Message response)
    {
        var content = new List<ContentBlock>();

        foreach (var block in response.Content)
        {
            // Thinking blocks must be captured first (they appear before text in Anthropic responses
            // with extended thinking enabled) and stored with their signature for future turns.
            if (block.TryPickThinking(out var thinkingBlock))
            {
                content.Add(new ThinkingContent
                {
                    Thinking = thinkingBlock.Thinking,
                    Signature = thinkingBlock.Signature
                });
            }
            else if (block.TryPickText(out var textBlock))
            {
                content.Add(new TextContent { Text = textBlock.Text });
            }
            else if (block.TryPickToolUse(out var toolUseBlock))
            {
                content.Add(new ToolUseContent
                {
                    Id = toolUseBlock.ID,
                    Name = toolUseBlock.Name,
                    Input = toolUseBlock.Input
                });
            }
        }

        var apiStopReason = response.StopReason;
        var stopReason = apiStopReason != null
            ? ConvertStopReason((AnthropicStopReason)apiStopReason)
            : ModelStopReason.EndTurn;

        return new ModelResponse
        {
            Content = content,
            StopReason = stopReason,
            Usage = new TokenUsage
            {
                InputTokens = (int)response.Usage.InputTokens,
                OutputTokens = (int)response.Usage.OutputTokens
            },
            Model = response.Model ?? ""
        };
    }

    /// <summary>
    /// Fallback parser for tool_use content_block_start events from Anthropic-compatible APIs
    /// (e.g. BigModel/ZhiPu) that omit the "caller" field required by Anthropic SDK 12.9.0+.
    /// When TryPickContentBlockStart fails to parse such events, this reads the raw JSON directly.
    /// </summary>
    private static bool TryParseRawToolUseContentBlockStart(
        JsonElement rawJson,
        out long index,
        out string toolUseId,
        out string toolUseName)
    {
        index = 0;
        toolUseId = string.Empty;
        toolUseName = string.Empty;

        if (rawJson.ValueKind != JsonValueKind.Object)
            return false;
        if (!rawJson.TryGetProperty("type", out var typeEl) ||
            typeEl.GetString() != "content_block_start")
            return false;
        if (!rawJson.TryGetProperty("content_block", out var contentBlock))
            return false;
        if (!contentBlock.TryGetProperty("type", out var blockType) ||
            blockType.GetString() != "tool_use")
            return false;
        if (!rawJson.TryGetProperty("index", out var indexEl))
            return false;

        index = indexEl.GetInt64();
        if (contentBlock.TryGetProperty("id", out var idEl))
            toolUseId = idEl.GetString() ?? string.Empty;
        if (contentBlock.TryGetProperty("name", out var nameEl))
            toolUseName = nameEl.GetString() ?? string.Empty;

        return true;
    }

    /// <summary>
    /// Fallback parser: extracts input_tokens from a raw message_start event.
    /// Used when TryPickStart() fails on Anthropic-compatible APIs.
    /// </summary>
    private static bool TryParseRawMessageStartTokens(JsonElement rawJson, out int inputTokens)
    {
        inputTokens = 0;
        if (rawJson.ValueKind != JsonValueKind.Object) return false;
        if (!rawJson.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "message_start")
            return false;
        if (!rawJson.TryGetProperty("message", out var message)) return false;
        if (!message.TryGetProperty("usage", out var usage)) return false;
        if (!usage.TryGetProperty("input_tokens", out var inputEl)) return false;
        inputTokens = inputEl.TryGetInt32(out var v) ? v : 0;
        return inputTokens > 0;
    }

    /// <summary>
    /// Fallback parser: extracts input/output token counts from a raw message_delta event.
    /// Used when the SDK's MessageDeltaUsage returns zeros on Anthropic-compatible APIs.
    /// </summary>
    private static bool TryParseRawMessageDeltaTokens(JsonElement rawJson, out int inputTokens, out int outputTokens)
    {
        inputTokens = 0;
        outputTokens = 0;
        if (rawJson.ValueKind != JsonValueKind.Object) return false;
        if (!rawJson.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "message_delta")
            return false;
        if (!rawJson.TryGetProperty("usage", out var usage)) return false;
        if (usage.TryGetProperty("input_tokens", out var inputEl))
            inputEl.TryGetInt32(out inputTokens);
        if (usage.TryGetProperty("output_tokens", out var outputEl))
            outputEl.TryGetInt32(out outputTokens);
        return inputTokens > 0 || outputTokens > 0;
    }

    // Extract the raw stop_reason string from a message_delta SSE event. Used to
    // detect non-standard values the Anthropic SDK nullifies (see IsContextOverflowSignal).
    private static bool TryParseRawStopReason(JsonElement rawJson, out string stopReason)
    {
        stopReason = string.Empty;
        if (rawJson.ValueKind != JsonValueKind.Object) return false;
        if (!rawJson.TryGetProperty("delta", out var delta)) return false;
        if (!delta.TryGetProperty("stop_reason", out var el)) return false;
        if (el.ValueKind != JsonValueKind.String) return false;
        stopReason = el.GetString() ?? string.Empty;
        return !string.IsNullOrEmpty(stopReason);
    }

    // Known non-standard overflow signals from Anthropic-compatible providers.
    // GLM-5-Turbo: "model_context_window_exceeded" — returned on HTTP 200 with zero usage
    //   when the request exceeds the model's context window.
    // Extend this list as new providers are integrated; prefer exact match to avoid
    // misclassifying unrelated error strings as overflow.
    private static bool IsContextOverflowSignal(string stopReason)
    {
        return stopReason switch
        {
            "model_context_window_exceeded" => true,
            "context_length_exceeded" => true,
            "context_window_exceeded" => true,
            _ => false
        };
    }
}

/// <summary>
/// Options for configuring the Anthropic provider.
/// </summary>
public class AnthropicOptions
{
    /// <summary>
    /// The API key for authentication.
    /// </summary>
    public required string ApiKey { get; init; }

    /// <summary>
    /// The base URL for the API (default: https://api.anthropic.com).
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// The default model ID to use.
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Whether to enable beta features.
    /// </summary>
    public bool EnableBetaFeatures { get; init; }

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