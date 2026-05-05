using System.Text;
using System.Text.Json;
using Kode.Agent.Sdk.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Kode.Agent.Sdk.Core.Context;

/// <summary>
/// LLM-powered summarizer using IModelProvider.CompleteAsync.
/// Produces both a semantic summary and an updated core-memory block in one call.
/// Falls back to StaticContextSummarizer on any failure.
/// <br/>
/// References:
///   - MemGPT (arxiv 2310.08560): core-memory block always in context, updated by LLM
///   - LLMLingua-2 (arxiv 2403.12968): importance-aware compression
///   - Anthropic effective context engineering blog
/// </summary>
public class LlmContextSummarizer : IContextSummarizer
{
    // Input budget for removed messages fed to the LLM.
    // 3× the old 4000-char limit; leaves ~14k tokens headroom in a 16k input context.
    private const int MaxContextChars = 12_000;

    private const string DefaultPrompt =
        """
        Compress the removed conversation history. Respond in EXACTLY this XML, nothing else:

        <summary>
        Under 500 words. Include: task objective, completed steps, key findings, file paths.
        Omit: repetitive polling, verbose command output, intermediate failed attempts.
        </summary>

        <core-memory>
        ## Current Task
        [one sentence]

        ## Modified Files
        [bullets of important paths read or written]

        ## Key Decisions
        [bullets of important technical or product decisions]

        ## Next Steps
        [what remains, if known]
        </core-memory>
        """;

    private static readonly IContextSummarizer _fallback = new StaticContextSummarizer();

    private readonly IModelProvider _modelProvider;
    private readonly string? _primaryModel;
    private readonly ILogger<LlmContextSummarizer>? _logger;

    public LlmContextSummarizer(
        IModelProvider modelProvider,
        string? primaryModel = null,
        ILogger<LlmContextSummarizer>? logger = null)
    {
        _modelProvider = modelProvider;
        _primaryModel = primaryModel;
        _logger = logger;
    }

    public async Task<SummaryResult> SummarizeAsync(
        IReadOnlyList<Message> removedMessages,
        ContextManagerOptions options,
        CancellationToken cancellationToken = default)
    {
        // Defensive short-circuit: an empty removal batch produces the degenerate
        // "No conversation history was provided" reply from the LLM, which then
        // overwrites a healthy core-memory block with "[None]" placeholders and
        // stacks a meaningless summary. Return an empty result so the caller can
        // treat it as a no-op. (ContextManager now also guards at the call site.)
        if (removedMessages.Count == 0)
            return new SummaryResult(string.Empty, null);

        const int maxRetries = 3;
        const int baseDelayMs = 1000;

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                var model = options.CompressionModel ?? _primaryModel ?? "claude-haiku-4-5-20251001";
                var caps = _modelProvider.GetModelCapabilities(model);

                ModelRequest request;
                if (caps is { SupportsCacheAlignedSummary: true })
                {
                    request = BuildCacheAlignedRequest(model, removedMessages);
                }
                else
                {
                    var prompt = string.IsNullOrWhiteSpace(options.CompressionPrompt)
                        ? DefaultPrompt
                        : options.CompressionPrompt;

                    request = new ModelRequest
                    {
                        Model = model,
                        SystemPrompt = prompt,
                        Messages = [Message.User(BuildCompressionContext(removedMessages))],
                        MaxTokens = 1200,
                    };
                }

                var response = await _modelProvider.CompleteAsync(request, cancellationToken);
                var raw = response.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "";

                if (string.IsNullOrWhiteSpace(raw))
                    return await _fallback.SummarizeAsync(removedMessages, options, cancellationToken);

                return ParseResponse(raw);
            }
            catch (Exception ex) when (
                attempt < maxRetries - 1
                && !cancellationToken.IsCancellationRequested
                && ex is not OperationCanceledException
                && ex is not TaskCanceledException
                && IsTransientError(ex))
            {
                // Exponential backoff with jitter: 1s, 2s, 4s ± 25%
                var delayMs = baseDelayMs * (1 << attempt);
                var jitterMs = Random.Shared.Next(-delayMs / 4, (delayMs / 4) + 1);
                _logger?.LogWarning(ex,
                    "LLM summarization transient error (attempt {Attempt}/{Max}), retrying in {DelayMs}ms",
                    attempt + 1, maxRetries, delayMs + jitterMs);
                await Task.Delay(delayMs + jitterMs, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "LLM summarization failed, falling back to static summary");
                return await _fallback.SummarizeAsync(removedMessages, options, cancellationToken);
            }
        }

        // Exhausted retries — fall back to static
        _logger?.LogWarning("LLM summarization exhausted {MaxRetries} retries, falling back to static summary", maxRetries);
        return await _fallback.SummarizeAsync(removedMessages, options, cancellationToken);
    }

    /// <summary>
    /// Returns true for transient errors that should be retried (server errors,
    /// rate limits, timeouts). Authentication and client errors are NOT retried.
    /// </summary>
    private static bool IsTransientError(Exception ex)
    {
        if (ex is HttpRequestException httpEx)
        {
            var statusCode = (int?)httpEx.StatusCode;
            return statusCode is >= 500 or 429; // 5xx + rate limit
        }
        if (ex is TimeoutException)
            return true;
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
    }

    // --- response parsing ---

    private static SummaryResult ParseResponse(string raw)
    {
        var summary = ExtractXmlSection(raw, "summary");
        var coreMemory = ExtractXmlSection(raw, "core-memory");

        // If LLM didn't follow the format, treat the whole response as summary
        if (string.IsNullOrWhiteSpace(summary))
            summary = raw.Trim();

        return new SummaryResult(summary.Trim(), string.IsNullOrWhiteSpace(coreMemory) ? null : coreMemory.Trim());
    }

    private static string? ExtractXmlSection(string text, string tag)
    {
        var open = $"<{tag}>";
        var close = $"</{tag}>";
        var start = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += open.Length;
        var end = text.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? null : text[start..end];
    }

    // --- cache-aligned summary path ---

    /// <summary>
    /// Builds a cache-aligned summary request for models with transparent prefix caching
    /// (DeepSeek V4).  Replays the original removed messages verbatim so the prefix cache
    /// hits on the bulk of the request, then appends a summary instruction as the final
    /// user turn.  System prompt is null — the instruction is in the user message.
    /// </summary>
    private static ModelRequest BuildCacheAlignedRequest(
        string model,
        IReadOnlyList<Message> removedMessages)
    {
        var messages = new List<Message>(removedMessages.Count + 1);
        messages.AddRange(removedMessages);
        messages.Add(Message.User(CacheAlignedInstruction));

        return new ModelRequest
        {
            Model = model,
            SystemPrompt = null,  // key: no system prompt so prefix matches conversation
            Messages = messages,
            MaxTokens = 1200,
        };
    }

    private const string CacheAlignedInstruction =
        "Summarize the conversation above in a concise but comprehensive way. " +
        "Preserve key information, decisions made, exact file paths, commands, errors, " +
        "and tool-result facts needed to continue the work. " +
        "Tool outputs may be abbreviated only when they are repetitive. " +
        "Keep it under 900 words.\n\n" +
        "Respond in EXACTLY this XML, nothing else:\n\n" +
        "<summary>\nUnder 900 words. Include: task objective, completed steps, key findings, file paths.\n" +
        "Omit: repetitive polling, verbose command output, intermediate failed attempts.\n" +
        "</summary>\n\n" +
        "<core-memory>\n## Current Task\n[one sentence]\n\n## Modified Files\n[bullets]\n\n" +
        "## Key Decisions\n[bullets]\n\n## Next Steps\n[what remains]\n</core-memory>";

    // --- input construction ---

    /// <summary>
    /// Builds the compression input from removed messages.
    /// Prioritises user messages and deduplicates consecutive polling tool calls (bash_logs).
    /// Input is capped at <see cref="MaxContextChars"/> to keep the LLM call cheap.
    /// </summary>
    private static string BuildCompressionContext(IReadOnlyList<Message> removedMessages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Removed conversation history ===");

        var totalToolCalls = 0;
        var totalToolResults = 0;
        string? lastPollKey = null;

        foreach (var msg in removedMessages)
        {
            if (sb.Length > MaxContextChars) break;

            if (msg.Role == MessageRole.User)
            {
                lastPollKey = null;
                var text = string.Join(" ", msg.Content.OfType<TextContent>().Select(t => t.Text));
                if (!string.IsNullOrWhiteSpace(text))
                    sb.AppendLine($"[User]: {Preview(text, 400)}");

                // Render tool_result previews so a removal batch dominated by tool outputs
                // still produces a meaningful summary body instead of just the header.
                // Without this, tool_result-only user messages contribute nothing and the
                // LLM replies "No conversation history was provided".
                foreach (var tr in msg.Content.OfType<ToolResultContent>())
                {
                    if (sb.Length > MaxContextChars) break;
                    totalToolResults++;
                    var serialized = SerializeToolResultContent(tr.Content);
                    if (string.IsNullOrWhiteSpace(serialized)) continue;
                    var shortId = tr.ToolUseId.Length > 12 ? tr.ToolUseId[..12] : tr.ToolUseId;
                    var marker = tr.IsError ? "ToolError" : "ToolResult";
                    sb.AppendLine($"[{marker}:{shortId}]: {Preview(serialized, 300)}");
                }
            }
            else if (msg.Role == MessageRole.Assistant)
            {
                var text = string.Join(" ", msg.Content.OfType<TextContent>().Select(t => t.Text));
                if (!string.IsNullOrWhiteSpace(text))
                    sb.AppendLine($"[Assistant]: {Preview(text, 250)}");

                var tools = msg.Content.OfType<ToolUseContent>().ToList();
                if (tools.Count > 0)
                {
                    totalToolCalls += tools.Count;
                    var names = string.Join(", ", tools.Select(t => t.Name).Distinct());

                    // Deduplicate consecutive identical polling calls
                    if (tools.All(t => t.Name == "bash_logs"))
                    {
                        if (names == lastPollKey) continue;
                        lastPollKey = names;
                    }
                    else
                    {
                        lastPollKey = null;
                    }

                    sb.AppendLine($"[Tools]: {names}");
                }
            }
        }

        if (totalToolCalls > 0 || totalToolResults > 0)
            sb.AppendLine($"(Total {totalToolCalls} tool calls, {totalToolResults} tool results in removed history)");

        return sb.ToString();
    }

    private static string Preview(string text, int limit) =>
        text.Length > limit ? text[..limit] + "…" : text;

    // ToolResultContent.Content is typed as `object` and may arrive as:
    //   - string (plain text tool output, or a shrink placeholder from ContextManager)
    //   - JsonElement (round-tripped through JsonAgentStore — nested POCO becomes an Object kind)
    //   - POCO / anonymous type (fresh from tool execution in the same process)
    // For all shapes we want a textual projection that preserves signal. Raw .ToString() on a
    // POCO or JsonElement returns the type name, which would leave the summary empty.
    private static string SerializeToolResultContent(object? content)
    {
        if (content is null) return "";
        if (content is string s) return s;
        try { return JsonSerializer.Serialize(content); }
        catch { return content.ToString() ?? ""; }
    }
}
