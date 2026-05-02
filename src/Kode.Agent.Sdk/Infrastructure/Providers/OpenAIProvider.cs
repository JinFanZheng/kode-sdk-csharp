using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kode.Agent.Sdk.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using ContentBlock = Kode.Agent.Sdk.Core.Types.ContentBlock;
using FileContent = Kode.Agent.Sdk.Core.Types.FileContent;
using ImageContent = Kode.Agent.Sdk.Core.Types.ImageContent;
using Message = Kode.Agent.Sdk.Core.Types.Message;
using ThinkingContent = Kode.Agent.Sdk.Core.Types.ThinkingContent;
using TextContent = Kode.Agent.Sdk.Core.Types.TextContent;
using ToolResultContent = Kode.Agent.Sdk.Core.Types.ToolResultContent;
using ToolUseContent = Kode.Agent.Sdk.Core.Types.ToolUseContent;
using VideoContent = Kode.Agent.Sdk.Core.Types.VideoContent;

namespace Kode.Agent.Sdk.Infrastructure.Providers;

/// <summary>
/// OpenAI model provider implementation using official SDK.
/// </summary>
public sealed class OpenAIProvider : IModelProvider
{
    internal const string ThinkingMarkerStart = "__KODE_REASONING_START__";
    internal const string ThinkingMarkerEnd = "__KODE_REASONING_END__";

    // ChatCompletionOptions.StreamOptions is an internal type in the SDK.
    // We use reflection to set IncludeUsage=true so the API returns token usage
    // in streaming responses (as a trailing chunk after the finish_reason chunk).
    private static readonly System.Reflection.PropertyInfo? s_streamOptionsProp =
        typeof(ChatCompletionOptions).GetProperty("StreamOptions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    private static readonly System.Reflection.PropertyInfo? s_includeUsageProp =
        s_streamOptionsProp?.PropertyType.GetProperty("IncludeUsage");

    private readonly OpenAIClient _client;
    private readonly OpenAIOptions _options;
    private readonly RetryPolicy _retryPolicy;
    private readonly ILogger<OpenAIProvider>? _logger;

    public string ProviderName => "openai";

    public OpenAIProvider(OpenAIOptions options, ILogger<OpenAIProvider>? logger = null)
    {
        _options = options;
        _retryPolicy = options.RetryPolicy ?? RetryPolicy.Default;
        _logger = logger;

        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrEmpty(options.BaseUrl))
            clientOptions.Endpoint = new Uri(options.BaseUrl);
        if (options.CustomHeaders is { Count: > 0 })
        {
            foreach (var (key, value) in options.CustomHeaders)
            {
                if (string.Equals(key, "User-Agent", StringComparison.OrdinalIgnoreCase))
                    clientOptions.UserAgentApplicationId = value;
            }
        }

        // Inject MediaRewriteHttpHandler so video_url / file_url markers in content
        // parts are rewritten to their proper JSON shapes before leaving the process.
        // Timeout.InfiniteTimeSpan: streaming responses can run for several minutes;
        // the 100-second default causes mid-stream cancellation. The CancellationToken
        // passed to StreamAsync controls actual cancellation instead.
        clientOptions.Transport = new HttpClientPipelineTransport(
            new HttpClient(new MediaRewriteHttpHandler()) { Timeout = Timeout.InfiniteTimeSpan });

        _client = new OpenAIClient(
            new ApiKeyCredential(options.ApiKey),
            clientOptions);
    }

    /// <summary>
    /// For backward compatibility with HttpClient-based construction.
    /// </summary>
    public OpenAIProvider(HttpClient httpClient, OpenAIOptions options, ILogger<OpenAIProvider>? logger = null)
        : this(options, logger)
    {
    }

    /// <summary>
    /// Internal constructor for testing — accepts a fully-configured
    /// <see cref="OpenAIClientOptions"/> (including a mock transport).
    /// The caller is responsible for chaining <see cref="MediaRewriteHttpHandler"/>
    /// inside the transport if request-body transformation is required.
    /// </summary>
    internal OpenAIProvider(OpenAIOptions options, OpenAIClientOptions clientOptions, ILogger<OpenAIProvider>? logger = null)
    {
        _options = options;
        _retryPolicy = options.RetryPolicy ?? RetryPolicy.Default;
        _logger = logger;
        _client = new OpenAIClient(new ApiKeyCredential(options.ApiKey), clientOptions);
    }

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
        providerActivity?.SetTag("gen_ai.system", "openai");
        providerActivity?.SetTag("gen_ai.request.model", request.Model);

        var streamStopwatch = Stopwatch.StartNew();
        var ttftRecorded = false;

        _logger?.LogDebug("OpenAI stream starting: model={Model}, tools={ToolCount}",
            request.Model, request.Tools?.Count ?? 0);

        var chatClient = _client.GetChatClient(request.Model);
        var messages = BuildChatMessages(request);
        var options = BuildChatOptions(request);

        var toolCallBuilders = new Dictionary<int, (string Id, string Name, System.Text.StringBuilder Args)>();
        var thinkingParser = new ThinkingMarkerStreamingParser();
        var streamedReasoningDeltas = new Queue<string>();
        TokenUsage? finalUsage = null;

        MediaRewriteHttpHandler.ThinkingEnabled.Value = request.EnableThinking == true;
        MediaRewriteHttpHandler.StreamingReasoningDeltas.Value = streamedReasoningDeltas;
        try
        {

        await foreach (var update in chatClient.CompleteChatStreamingAsync(messages, options, cancellationToken))
        {
            while (streamedReasoningDeltas.Count > 0)
            {
                var reasoningDelta = streamedReasoningDeltas.Dequeue();
                if (string.IsNullOrEmpty(reasoningDelta))
                    continue;

                if (!ttftRecorded)
                {
                    ttftRecorded = true;
                    var ttftMs = streamStopwatch.ElapsedMilliseconds;
                    providerActivity?.SetTag("gen_ai.client.time_to_first_token_ms", ttftMs);
                    KodeAgentMetrics.ModelTtft.Record(ttftMs,
                        new TagList { { "model", request.Model }, { "provider", "openai" } });
                    _logger?.LogDebug("OpenAI TTFT: model={Model}, ttft_ms={TtftMs}", request.Model, ttftMs);
                }

                yield return new StreamChunk
                {
                    Type = StreamChunkType.ThinkingDelta,
                    ThinkingDelta = reasoningDelta
                };
            }

            foreach (var chunk in ConvertStreamUpdate(update, toolCallBuilders, thinkingParser))
            {
                if (!ttftRecorded && chunk.Type is StreamChunkType.TextDelta
                    or StreamChunkType.ThinkingDelta or StreamChunkType.ToolUseStart)
                {
                    ttftRecorded = true;
                    var ttftMs = streamStopwatch.ElapsedMilliseconds;
                    providerActivity?.SetTag("gen_ai.client.time_to_first_token_ms", ttftMs);
                    KodeAgentMetrics.ModelTtft.Record(ttftMs,
                        new TagList { { "model", request.Model }, { "provider", "openai" } });
                    _logger?.LogDebug("OpenAI TTFT: model={Model}, ttft_ms={TtftMs}", request.Model, ttftMs);
                }

                if (chunk.Type == StreamChunkType.MessageStop && chunk.Usage != null)
                    finalUsage = chunk.Usage;

                yield return chunk;
            }

            // With include_usage=true, OpenAI sends a trailing chunk (FinishReason=null, Usage!=null)
            // after the finish_reason chunk. ConvertStreamUpdate ignores it, so we capture it here.
            if (update.FinishReason == null && update.Usage != null)
            {
                finalUsage = new TokenUsage
                {
                    InputTokens = update.Usage.InputTokenCount,
                    OutputTokens = update.Usage.OutputTokenCount
                };
                yield return new StreamChunk { Type = StreamChunkType.MessageStop, Usage = finalUsage };
            }
        }

        while (streamedReasoningDeltas.Count > 0)
        {
            var reasoningDelta = streamedReasoningDeltas.Dequeue();
            if (string.IsNullOrEmpty(reasoningDelta))
                continue;

            yield return new StreamChunk
            {
                Type = StreamChunkType.ThinkingDelta,
                ThinkingDelta = reasoningDelta
            };
        }

        streamStopwatch.Stop();
        providerActivity?.SetTag("gen_ai.stream.duration_ms", streamStopwatch.ElapsedMilliseconds);
        if (finalUsage != null)
        {
            providerActivity?.SetTag("gen_ai.usage.input_tokens", finalUsage.InputTokens);
            providerActivity?.SetTag("gen_ai.usage.output_tokens", finalUsage.OutputTokens);
            _logger?.LogDebug(
                "OpenAI stream complete: model={Model}, input_tokens={InputTokens}, output_tokens={OutputTokens}, duration_ms={DurationMs}",
                request.Model, finalUsage.InputTokens, finalUsage.OutputTokens, streamStopwatch.ElapsedMilliseconds);
        }

        } // end try
        finally
        {
            MediaRewriteHttpHandler.ThinkingEnabled.Value = false;
            MediaRewriteHttpHandler.StreamingReasoningDeltas.Value = null;
        }
    }

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        var chatClient = _client.GetChatClient(request.Model);
        var messages = BuildChatMessages(request);
        var options = BuildChatOptions(request);

        return ProviderRetryHelper.ExecuteWithRetryAsync(
            async ct =>
            {
                MediaRewriteHttpHandler.ThinkingEnabled.Value = request.EnableThinking == true;
                try
                {
                    var result = await chatClient.CompleteChatAsync(messages, options, ct);
                    return ConvertToModelResponse(result.Value);
                }
                finally
                {
                    MediaRewriteHttpHandler.ThinkingEnabled.Value = false;
                }
            },
            _retryPolicy, _logger, ProviderName, cancellationToken);
    }

    public async Task<bool> ValidateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new ModelRequest
            {
                Model = "gpt-3.5-turbo",
                Messages = [Message.User("Hi")],
                MaxTokens = 1
            };

            await CompleteAsync(request, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "OpenAI provider validation failed");
            return false;
        }
    }

    private List<ChatMessage> BuildChatMessages(ModelRequest request)
    {
        var messages = new List<ChatMessage>();

        // Add system message if provided
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new SystemChatMessage(request.SystemPrompt));
        }

        // Preserve memory blocks (core-memory / context-summary) carried as system-role
        // messages from ContextManager.  Dropping them silently (as the old filter did)
        // defeats the three-layer compression and causes the model to re-overflow on the
        // same conversation.  OpenAI accepts multiple SystemChatMessage entries at the
        // head of the messages array.
        foreach (var msg in request.Messages)
        {
            if (msg.Role != MessageRole.System) continue;
            foreach (var text in msg.Content.OfType<TextContent>())
            {
                if (!string.IsNullOrWhiteSpace(text.Text))
                    messages.Add(new SystemChatMessage(text.Text));
            }
        }

        foreach (var msg in request.Messages)
        {
            if (msg.Role == MessageRole.System) continue;

            var chatMessage = ConvertMessage(msg);
            if (chatMessage != null)
            {
                messages.Add(chatMessage);
            }

            // Handle tool results separately
            foreach (var toolResult in msg.Content.OfType<ToolResultContent>())
            {
                messages.Add(new ToolChatMessage(toolResult.ToolUseId, JsonSerializer.Serialize(toolResult.Content) ?? "")); // Fixed by Nietzsche: use JSON serialization instead of ToString() for anonymous types
            }
        }

        return messages;
    }

    private static ChatMessage? ConvertMessage(Message msg)
    {
        if (msg.Role == MessageRole.User)
        {
            var hasMultiModal = msg.Content.OfType<ImageContent>().Any()
                || msg.Content.OfType<VideoContent>().Any()
                || msg.Content.OfType<FileContent>().Any();
            if (!hasMultiModal)
            {
                var text = string.Join("", msg.Content.OfType<TextContent>().Select(t => t.Text));
                // Skip empty user messages (e.g. messages that contain only ToolResultContent —
                // those are added separately as ToolChatMessage entries below).
                if (string.IsNullOrEmpty(text)) return null;
                return new UserChatMessage(text);
            }

            // Multi-modal user message: build content part list.
            // VideoContent and FileContent are not natively supported by the OpenAI SDK, so they
            // are encoded as specially-prefixed text parts.  MediaRewriteHttpHandler rewrites them
            // into the correct video_url / file_url JSON shapes at the HTTP layer before the
            // request leaves the process.
            var parts = new List<ChatMessageContentPart>();
            foreach (var block in msg.Content)
            {
                switch (block)
                {
                    case TextContent tc:
                        parts.Add(ChatMessageContentPart.CreateTextPart(tc.Text));
                        break;
                    case ImageContent img when img.Url is not null:
                        parts.Add(ChatMessageContentPart.CreateImagePart(new Uri(img.Url)));
                        break;
                    case ImageContent img when img.Data is not null:
                        parts.Add(ChatMessageContentPart.CreateImagePart(
                            BinaryData.FromBytes(Convert.FromBase64String(img.Data)),
                            img.MediaType ?? "image/png"));
                        break;
                    case VideoContent video:
                        parts.Add(ChatMessageContentPart.CreateTextPart(
                            MediaRewriteHttpHandler.VideoMarker + video.Url));
                        break;
                    case FileContent file:
                        parts.Add(ChatMessageContentPart.CreateTextPart(
                            MediaRewriteHttpHandler.FileMarker + file.Url));
                        break;
                }
            }
            return new UserChatMessage(parts);
        }

        if (msg.Role == MessageRole.Assistant)
        {
            var toolUses = msg.Content.OfType<ToolUseContent>().ToList();
            var textContent = string.Join("", msg.Content.OfType<TextContent>().Select(t => t.Text));
            var thinkingContent = string.Join("", msg.Content.OfType<ThinkingContent>().Select(t => t.Thinking));
            var assistantContent = string.IsNullOrEmpty(thinkingContent)
                ? textContent
                : ThinkingMarkerStart + thinkingContent + ThinkingMarkerEnd + textContent;

            if (toolUses.Count > 0)
            {
                var toolCalls = toolUses.Select(tu =>
                    ChatToolCall.CreateFunctionToolCall(
                        tu.Id,
                        tu.Name,
                        BinaryData.FromString(JsonSerializer.Serialize(tu.Input))
                    )).ToList();

                // Create assistant message with tool calls
                var assistantMessage = new AssistantChatMessage(toolCalls);

                // Always add content - OpenAI requires it even if empty
                if (!string.IsNullOrEmpty(assistantContent))
                {
                    assistantMessage.Content.Add(ChatMessageContentPart.CreateTextPart(assistantContent));
                }
                else
                {
                    // Add empty text to satisfy OpenAI API requirement
                    assistantMessage.Content.Add(ChatMessageContentPart.CreateTextPart(""));
                }

                return assistantMessage;
            }

            // For non-tool messages, return text content (use empty string if null)
            return new AssistantChatMessage(assistantContent ?? "");
        }

        return null;
    }

    private static void TryEnableStreamUsage(ChatCompletionOptions options)
    {
        if (s_streamOptionsProp == null || s_includeUsageProp == null) return;
        var streamOpts = Activator.CreateInstance(s_streamOptionsProp.PropertyType, nonPublic: true)!;
        s_includeUsageProp.SetValue(streamOpts, true);
        s_streamOptionsProp.SetValue(options, streamOpts);
    }

    private ChatCompletionOptions BuildChatOptions(ModelRequest request)
    {
        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = request.MaxTokens,
            Temperature = (float?)request.Temperature
        };
        TryEnableStreamUsage(options);

        if (request.StopSequences?.Count > 0)
        {
            foreach (var stop in request.StopSequences)
            {
                options.StopSequences.Add(stop);
            }
        }

        if (request.Tools?.Count > 0)
        {
            foreach (var tool in request.Tools)
            {
                var schema = tool.InputSchema is JsonElement je
                    ? BinaryData.FromString(je.GetRawText())
                    : BinaryData.FromString(JsonSerializer.Serialize(tool.InputSchema));

                options.Tools.Add(ChatTool.CreateFunctionTool(
                    tool.Name,
                    tool.Description,
                    schema));
            }
        }

        return options;
    }

    private IEnumerable<StreamChunk> ConvertStreamUpdate(
        StreamingChatCompletionUpdate update,
        Dictionary<int, (string Id, string Name, System.Text.StringBuilder Args)> toolCallBuilders,
        ThinkingMarkerStreamingParser thinkingParser)
    {
        // Text delta
        foreach (var contentPart in update.ContentUpdate)
        {
            if (!string.IsNullOrEmpty(contentPart.Text))
            {
                foreach (var segment in thinkingParser.Append(contentPart.Text))
                {
                    if (segment.IsThinking)
                    {
                        yield return new StreamChunk
                        {
                            Type = StreamChunkType.ThinkingDelta,
                            ThinkingDelta = segment.Text
                        };
                    }
                    else
                    {
                        yield return new StreamChunk
                        {
                            Type = StreamChunkType.TextDelta,
                            TextDelta = segment.Text
                        };
                    }
                }
            }
        }

        // Tool calls
        foreach (var toolUpdate in update.ToolCallUpdates)
        {
            var index = toolUpdate.Index;

            // New tool call
            if (!string.IsNullOrEmpty(toolUpdate.ToolCallId))
            {
                toolCallBuilders[index] = (toolUpdate.ToolCallId, toolUpdate.FunctionName ?? "", new System.Text.StringBuilder());

                yield return new StreamChunk
                {
                    Type = StreamChunkType.ToolUseStart,
                    ToolUse = new ToolUseChunk
                    {
                        Id = toolUpdate.ToolCallId,
                        Name = toolUpdate.FunctionName
                    }
                };
            }

            // Tool arguments delta
            var argsUpdate = toolUpdate.FunctionArgumentsUpdate?.ToString();
            if (!string.IsNullOrEmpty(argsUpdate))
            {
                if (toolCallBuilders.TryGetValue(index, out var builder))
                {
                    builder.Args.Append(argsUpdate);

                    yield return new StreamChunk
                    {
                        Type = StreamChunkType.ToolUseInputDelta,
                        ToolUse = new ToolUseChunk
                        {
                            Id = builder.Id,
                            InputDelta = argsUpdate
                        }
                    };
                }
            }
        }

        // Finish reason
        if (update.FinishReason != null)
        {
            foreach (var segment in thinkingParser.Flush())
            {
                if (string.IsNullOrEmpty(segment.Text))
                    continue;

                yield return new StreamChunk
                {
                    Type = segment.IsThinking ? StreamChunkType.ThinkingDelta : StreamChunkType.TextDelta,
                    ThinkingDelta = segment.IsThinking ? segment.Text : null,
                    TextDelta = segment.IsThinking ? null : segment.Text
                };
            }

            // Complete pending tool calls
            foreach (var (index, builder) in toolCallBuilders)
            {
                object? input = null;
                var argsJson = builder.Args.ToString();
                if (!string.IsNullOrEmpty(argsJson))
                {
                    try { input = JsonSerializer.Deserialize<object>(argsJson); }
                    catch (Exception ex) { _logger?.LogWarning(ex, "Failed to deserialize tool arguments JSON"); }
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

            var stopReason = update.FinishReason switch
            {
                ChatFinishReason.Stop => ModelStopReason.EndTurn,
                ChatFinishReason.Length => ModelStopReason.MaxTokens,
                ChatFinishReason.ToolCalls => ModelStopReason.ToolUse,
                ChatFinishReason.ContentFilter => ModelStopReason.EndTurn,
                _ => ModelStopReason.EndTurn
            };

            yield return new StreamChunk
            {
                Type = StreamChunkType.MessageStop,
                StopReason = stopReason,
                Usage = update.Usage != null
                    ? new TokenUsage
                    {
                        InputTokens = update.Usage.InputTokenCount,
                        OutputTokens = update.Usage.OutputTokenCount
                    }
                    : null
            };
        }
    }

    private static ModelResponse ConvertToModelResponse(ChatCompletion response)
    {
        var content = new List<ContentBlock>();

        foreach (var part in response.Content)
        {
            if (!string.IsNullOrEmpty(part.Text))
            {
                foreach (var segment in SplitThinkingMarkedText(part.Text))
                {
                    if (segment.IsThinking)
                    {
                        content.Add(new ThinkingContent { Thinking = segment.Text });
                    }
                    else
                    {
                        content.Add(new TextContent { Text = segment.Text });
                    }
                }
            }
        }

        foreach (var toolCall in response.ToolCalls)
        {
            object? input = null;
            var argsJson = toolCall.FunctionArguments?.ToString();
            if (!string.IsNullOrEmpty(argsJson))
            {
                try { input = JsonSerializer.Deserialize<object>(argsJson); }
                catch { }
            }

            content.Add(new ToolUseContent
            {
                Id = toolCall.Id,
                Name = toolCall.FunctionName,
                Input = input ?? new { }
            });
        }

        var stopReason = response.FinishReason switch
        {
            ChatFinishReason.Stop => ModelStopReason.EndTurn,
            ChatFinishReason.Length => ModelStopReason.MaxTokens,
            ChatFinishReason.ToolCalls => ModelStopReason.ToolUse,
            ChatFinishReason.ContentFilter => ModelStopReason.EndTurn,
            _ => ModelStopReason.EndTurn
        };

        return new ModelResponse
        {
            Content = content,
            StopReason = stopReason,
            Usage = new TokenUsage
            {
                InputTokens = response.Usage?.InputTokenCount ?? 0,
                OutputTokens = response.Usage?.OutputTokenCount ?? 0
            },
            Model = response.Model ?? ""
        };
    }

    internal static IEnumerable<(bool IsThinking, string Text)> SplitThinkingMarkedText(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        var cursor = 0;
        while (cursor < text.Length)
        {
            var start = text.IndexOf(ThinkingMarkerStart, cursor, StringComparison.Ordinal);
            if (start < 0)
            {
                var trailing = text[cursor..];
                if (!string.IsNullOrEmpty(trailing))
                    yield return (false, trailing);
                yield break;
            }

            if (start > cursor)
            {
                var leading = text[cursor..start];
                if (!string.IsNullOrEmpty(leading))
                    yield return (false, leading);
            }

            var contentStart = start + ThinkingMarkerStart.Length;
            var end = text.IndexOf(ThinkingMarkerEnd, contentStart, StringComparison.Ordinal);
            if (end < 0)
            {
                var remainder = text[start..];
                if (!string.IsNullOrEmpty(remainder))
                    yield return (false, remainder);
                yield break;
            }

            var thinking = text[contentStart..end];
            if (!string.IsNullOrEmpty(thinking))
                yield return (true, thinking);

            cursor = end + ThinkingMarkerEnd.Length;
        }
    }

    private sealed class ThinkingMarkerStreamingParser
    {
        private readonly StringBuilder _buffer = new();
        private bool _insideThinking;

        public IEnumerable<(bool IsThinking, string Text)> Append(string fragment)
        {
            if (string.IsNullOrEmpty(fragment))
                yield break;

            _buffer.Append(fragment);
            foreach (var segment in DrainBuffer(finalFlush: false))
                yield return segment;
        }

        public IEnumerable<(bool IsThinking, string Text)> Flush()
        {
            foreach (var segment in DrainBuffer(finalFlush: true))
                yield return segment;
        }

        private IEnumerable<(bool IsThinking, string Text)> DrainBuffer(bool finalFlush)
        {
            while (_buffer.Length > 0)
            {
                var marker = _insideThinking ? ThinkingMarkerEnd : ThinkingMarkerStart;
                var bufferText = _buffer.ToString();
                var markerIndex = bufferText.IndexOf(marker, StringComparison.Ordinal);

                if (markerIndex >= 0)
                {
                    if (markerIndex > 0)
                    {
                        yield return (_insideThinking, bufferText[..markerIndex]);
                    }

                    _buffer.Remove(0, markerIndex + marker.Length);
                    _insideThinking = !_insideThinking;
                    continue;
                }

                var safeLength = finalFlush ? _buffer.Length : GetSafeEmitLength(bufferText, marker);
                if (safeLength <= 0)
                    yield break;

                var emitText = bufferText[..safeLength];
                _buffer.Remove(0, safeLength);
                if (!string.IsNullOrEmpty(emitText))
                    yield return (_insideThinking, emitText);
            }
        }

        private static int GetSafeEmitLength(string text, string marker)
        {
            var maxProbe = Math.Min(text.Length, marker.Length - 1);
            for (var suffixLength = maxProbe; suffixLength > 0; suffixLength--)
            {
                if (marker.StartsWith(text[^suffixLength..], StringComparison.Ordinal))
                    return text.Length - suffixLength;
            }

            return text.Length;
        }
    }
}

/// <summary>
/// <see cref="DelegatingHandler"/> that rewrites specially-prefixed text content
/// parts into <c>video_url</c> and <c>file_url</c> JSON shapes before the HTTP
/// request is sent.  This is necessary because the OpenAI .NET SDK does not
/// natively support these GLM-compatible content-part types; they are encoded
/// as text parts with sentinel prefixes in <see cref="OpenAIProvider"/> and
/// decoded here at the HTTP layer.
/// </summary>
internal sealed class MediaRewriteHttpHandler(HttpMessageHandler? inner = null)
    : DelegatingHandler(inner ?? new HttpClientHandler())
{
    /// <summary>Sentinel prefix for a <see cref="VideoContent"/> URL.</summary>
    internal const string VideoMarker = "__KODE_VIDEO_URL__";

    /// <summary>Sentinel prefix for a <see cref="FileContent"/> URL.</summary>
    internal const string FileMarker = "__KODE_FILE_URL__";

    internal const string SseDataPrefix = "data: ";

    /// <summary>
    /// Side-channel flag: when true, inject <c>{"thinking":{"type":"enabled"}}</c> into
    /// the request body. Used by OpenAI-compatible providers (e.g. Kimi K2.5) that accept
    /// a <c>thinking</c> extra-body parameter but have no native SDK field for it.
    /// </summary>
    internal static readonly AsyncLocal<bool> ThinkingEnabled = new();
    internal static readonly AsyncLocal<Queue<string>?> StreamingReasoningDeltas = new();

    internal const string DefaultReasoningEffort = "high";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var transformed = TransformMediaMarkers(body);
            transformed = TransformAssistantReasoningMarkers(transformed);
            transformed = TransformThinking(transformed);
            if (!ReferenceEquals(transformed, body))
                request.Content = new StringContent(transformed, Encoding.UTF8, "application/json");
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (!ThinkingEnabled.Value || response.Content is null)
            return response;

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var enableThinking = ThinkingEnabled.Value;
            var reasoningDeltas = StreamingReasoningDeltas.Value;
            var transformedStream = new TransformingSseStream(
                sourceStream,
                line => TransformReasoningSseLine(line, enableThinking, reasoningDeltas));
            var replacement = new StreamContent(transformedStream);
            CopyContentHeaders(response.Content, replacement);
            response.Content = replacement;
            return response;
        }

        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) == true)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var transformed = TransformThinkingResponseJson(body);
            if (!ReferenceEquals(transformed, body))
            {
                var replacement = new StringContent(transformed, Encoding.UTF8, mediaType ?? "application/json");
                CopyContentHeaders(response.Content, replacement);
                response.Content = replacement;
            }
        }

        return response;
    }

    /// <summary>
    /// Injects <c>"thinking": {"type": "enabled"}</c> at the root of the JSON body
    /// when <see cref="ThinkingEnabled"/> is true. Returns the original string when
    /// thinking is disabled, to avoid unnecessary allocations on the hot path.
    /// </summary>
    internal static string TransformThinking(string json)
    {
        if (!ThinkingEnabled.Value) return json;
        if (!json.Contains("\"messages\"", StringComparison.Ordinal)) return json;

        var root = JsonNode.Parse(json);
        if (root is not JsonObject obj) return json;

        obj["thinking"] = JsonNode.Parse("{\"type\":\"enabled\"}");
        if (obj["reasoning_effort"] is null)
            obj["reasoning_effort"] = DefaultReasoningEffort;
        obj.Remove("reasoning_content");
        return root.ToJsonString();
    }

    internal static string TransformAssistantReasoningMarkers(string json)
    {
        if (!json.Contains(OpenAIProvider.ThinkingMarkerStart, StringComparison.Ordinal))
            return json;

        var root = JsonNode.Parse(json);
        if (root is not JsonObject rootObject) return json;

        var messages = rootObject["messages"]?.AsArray();
        if (messages is null) return json;

        var replayAsThinkingContentBlocks = ShouldReplayAssistantThinkingAsContentBlocks(rootObject);
        var changed = false;
        foreach (var msg in messages.OfType<JsonObject>())
        {
            if (!string.Equals(msg["role"]?.GetValue<string>(), "assistant", StringComparison.Ordinal))
                continue;

            if (msg["content"] is JsonValue stringContentNode
                && stringContentNode.TryGetValue<string>(out var contentText))
            {
                if (replayAsThinkingContentBlocks)
                {
                    var rebuiltContent = new JsonArray();
                    if (AppendThinkingContentParts(rebuiltContent, contentText))
                    {
                        msg["content"] = rebuiltContent.Count > 0 ? rebuiltContent : JsonValue.Create(string.Empty);
                        msg.Remove("reasoning_content");
                        changed = true;
                        continue;
                    }
                }
                else if (TrySplitAssistantReasoning(contentText, out var visibleText, out var reasoningText))
                {
                    msg["content"] = visibleText;
                    msg["reasoning_content"] = reasoningText;
                    changed = true;
                    continue;
                }
            }

            if (msg["content"] is not JsonArray contentParts)
                continue;

            var rebuiltParts = new JsonArray();
            var reasoningBuilder = replayAsThinkingContentBlocks ? null : new StringBuilder();
            var rebuilt = false;
            foreach (var partNode in contentParts)
            {
                if (partNode is not JsonObject part
                    || !string.Equals(part["type"]?.GetValue<string>(), "text", StringComparison.Ordinal)
                    || part["text"] is not JsonValue textNode
                    || !textNode.TryGetValue<string>(out var textPart))
                {
                    rebuiltParts.Add(partNode?.DeepClone());
                    continue;
                }

                if (replayAsThinkingContentBlocks)
                {
                    if (AppendThinkingContentParts(rebuiltParts, textPart))
                    {
                        rebuilt = true;
                        continue;
                    }

                    rebuiltParts.Add(partNode.DeepClone());
                    continue;
                }

                if (!TrySplitAssistantReasoning(textPart, out var visiblePartText, out var reasoningPartText))
                {
                    rebuiltParts.Add(partNode.DeepClone());
                    continue;
                }

                rebuilt = true;
                reasoningBuilder!.Append(reasoningPartText);
                if (!string.IsNullOrEmpty(visiblePartText))
                {
                    rebuiltParts.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = visiblePartText
                    });
                }
            }

            if (!rebuilt)
                continue;

            msg["content"] = rebuiltParts.Count > 0 ? rebuiltParts : JsonValue.Create(string.Empty);
            if (replayAsThinkingContentBlocks)
            {
                msg.Remove("reasoning_content");
            }
            else if (reasoningBuilder is not null && reasoningBuilder.Length > 0)
            {
                msg["reasoning_content"] = reasoningBuilder.ToString();
            }

            changed = true;
        }

        return changed ? rootObject.ToJsonString() : json;
    }

    private static bool ShouldReplayAssistantThinkingAsContentBlocks(JsonObject rootObject)
    {
        var model = rootObject["model"]?.GetValue<string>();
        return !string.IsNullOrWhiteSpace(model)
            && model.Contains("deepseek", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AppendThinkingContentParts(JsonArray target, string text)
    {
        if (string.IsNullOrEmpty(text)
            || !text.Contains(OpenAIProvider.ThinkingMarkerStart, StringComparison.Ordinal))
        {
            return false;
        }

        var changed = false;
        foreach (var segment in OpenAIProvider.SplitThinkingMarkedText(text))
        {
            changed = true;
            if (string.IsNullOrEmpty(segment.Text))
                continue;

            target.Add(segment.IsThinking
                ? new JsonObject
                {
                    ["type"] = "thinking",
                    ["thinking"] = segment.Text
                }
                : new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = segment.Text
                });
        }

        return changed;
    }

    private static bool TrySplitAssistantReasoning(string text, out string visibleText, out string reasoningText)
    {
        visibleText = text;
        reasoningText = string.Empty;
        if (string.IsNullOrEmpty(text)
            || !text.Contains(OpenAIProvider.ThinkingMarkerStart, StringComparison.Ordinal))
        {
            return false;
        }

        var visibleBuilder = new StringBuilder();
        var reasoningBuilder = new StringBuilder();
        var changed = false;
        foreach (var segment in OpenAIProvider.SplitThinkingMarkedText(text))
        {
            changed = true;
            if (segment.IsThinking)
                reasoningBuilder.Append(segment.Text);
            else
                visibleBuilder.Append(segment.Text);
        }

        visibleText = visibleBuilder.ToString();
        reasoningText = reasoningBuilder.ToString();
        return changed && reasoningBuilder.Length > 0;
    }

    internal static string TransformReasoningSseLine(string line)
        => TransformReasoningSseLine(line, ThinkingEnabled.Value, StreamingReasoningDeltas.Value);

    private static string TransformReasoningSseLine(string line, bool thinkingEnabled, Queue<string>? reasoningDeltas)
    {
        if (!line.StartsWith(SseDataPrefix, StringComparison.Ordinal)) return line;

        var payload = line[SseDataPrefix.Length..];
        if (payload.Length == 0 || string.Equals(payload, "[DONE]", StringComparison.Ordinal)) return line;

        var transformed = TransformReasoningPayloadJson(payload, streaming: true, thinkingEnabled, reasoningDeltas);
        return ReferenceEquals(transformed, payload) ? line : SseDataPrefix + transformed;
    }

    internal static string TransformThinkingResponseJson(string json)
        => TransformReasoningPayloadJson(json, streaming: false, ThinkingEnabled.Value, reasoningDeltas: null);

    private static string TransformReasoningPayloadJson(
        string json,
        bool streaming,
        bool thinkingEnabled,
        Queue<string>? reasoningDeltas)
    {
        if (!thinkingEnabled) return json;
        if (!json.Contains("reasoning_content", StringComparison.Ordinal)) return json;

        var root = JsonNode.Parse(json);
        if (root is not JsonObject obj) return json;
        if (obj["choices"] is not JsonArray choices) return json;

        var changed = false;
        foreach (var choiceNode in choices.OfType<JsonObject>())
        {
            var container = streaming
                ? choiceNode["delta"] as JsonObject
                : choiceNode["message"] as JsonObject;
            if (container is null) continue;

            var reasoning = container["reasoning_content"]?.GetValue<string>();
            if (string.IsNullOrEmpty(reasoning)) continue;

            var existingContent = container["content"]?.GetValue<string>() ?? string.Empty;
            if (streaming && reasoningDeltas is not null)
            {
                reasoningDeltas.Enqueue(reasoning);
                if (!string.IsNullOrEmpty(existingContent))
                    container["content"] = existingContent;
            }
            else
            {
                container["content"] = OpenAIProvider.ThinkingMarkerStart + reasoning + OpenAIProvider.ThinkingMarkerEnd + existingContent;
            }
            container.Remove("reasoning_content");
            changed = true;
        }

        return changed ? root.ToJsonString() : json;
    }

    private static void CopyContentHeaders(HttpContent source, HttpContent target)
    {
        foreach (var header in source.Headers)
            target.Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    /// <summary>
    /// Walks the serialised request JSON and replaces any text content parts whose
    /// text begins with <see cref="VideoMarker"/> or <see cref="FileMarker"/> with
    /// the correct <c>video_url</c> / <c>file_url</c> object shapes.
    /// Returns the original string (same reference) when no markers are found, to
    /// avoid unnecessary allocations on the hot path.
    /// </summary>
    internal static string TransformMediaMarkers(string json)
    {
        // Fast pre-check: skip JSON parsing when no markers are present.
        if (!json.Contains(VideoMarker, StringComparison.Ordinal)
            && !json.Contains(FileMarker, StringComparison.Ordinal))
            return json;

        var root = JsonNode.Parse(json);
        if (root is null) return json;

        var messages = root["messages"]?.AsArray();
        if (messages is null) return json;

        bool changed = false;
        foreach (var msg in messages)
        {
            if (msg?["content"] is not JsonArray parts) continue;

            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part?["type"]?.GetValue<string>() != "text") continue;

                var text = part["text"]?.GetValue<string>();
                if (text is null) continue;

                if (text.StartsWith(VideoMarker, StringComparison.Ordinal))
                {
                    var url = text[VideoMarker.Length..];
                    parts[i] = JsonNode.Parse(JsonSerializer.Serialize(
                        new { type = "video_url", video_url = new { url } }))!;
                    changed = true;
                }
                else if (text.StartsWith(FileMarker, StringComparison.Ordinal))
                {
                    var url = text[FileMarker.Length..];
                    parts[i] = JsonNode.Parse(JsonSerializer.Serialize(
                        new { type = "file_url", file_url = new { url } }))!;
                    changed = true;
                }
            }
        }

        return changed ? root.ToJsonString() : json;
    }
}

internal sealed class TransformingSseStream(Stream inner, Func<string, string> transformLine) : Stream
{
    private readonly StreamReader _reader = new(inner, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
    private byte[] _buffer = [];
    private int _offset;
    private bool _completed;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        => ReadCoreAsync(destination, cancellationToken);

    private async ValueTask<int> ReadCoreAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (destination.Length == 0) return 0;

        while (_offset >= _buffer.Length)
        {
            if (_completed) return 0;

            var line = await _reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                _completed = true;
                _buffer = [];
                _offset = 0;
                return 0;
            }

            var transformed = transformLine(line) + "\n";
            _buffer = Encoding.UTF8.GetBytes(transformed);
            _offset = 0;
        }

        var count = Math.Min(destination.Length, _buffer.Length - _offset);
        _buffer.AsMemory(_offset, count).CopyTo(destination);
        _offset += count;
        return count;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _reader.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await base.DisposeAsync();
    }
}

/// <summary>
/// Options for configuring the OpenAI provider.
/// </summary>
public class OpenAIOptions
{
    /// <summary>
    /// The API key for authentication.
    /// </summary>
    public required string ApiKey { get; init; }

    /// <summary>
    /// The base URL for the API (default: https://api.openai.com).
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// The organization ID.
    /// </summary>
    public string? Organization { get; init; }

    /// <summary>
    /// The default model to use.
    /// </summary>
    public string? DefaultModel { get; init; }

    /// <summary>
    /// Custom HTTP request headers to add to each request.
    /// Only User-Agent is supported via UserAgentApplicationId.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CustomHeaders { get; init; }

    /// <summary>
    /// Retry policy for transient errors (rate limits, overload, timeouts).
    /// Defaults to <see cref="RetryPolicy.Default"/> when null.
    /// </summary>
    public RetryPolicy? RetryPolicy { get; init; }
}
