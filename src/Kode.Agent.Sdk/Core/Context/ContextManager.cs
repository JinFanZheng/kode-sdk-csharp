using System.Text.Json;
using Kode.Agent.Sdk.Core.Agent;
using Microsoft.Extensions.Logging;

namespace Kode.Agent.Sdk.Core.Context;

/// <summary>
/// Context usage analysis result.
/// </summary>
public record ContextUsage(
    int TotalTokens,
    int MessageCount,
    bool ShouldCompress
);

/// <summary>
/// Compression result.
/// RetainedMessages is the fully-reconstructed message list ready to replace the agent's
/// message buffer (pinned summaries + core-memory block + recent messages).
/// </summary>
public record CompressionResult(
    Message Summary,
    IReadOnlyList<Message> RemovedMessages,
    IReadOnlyList<Message> RetainedMessages,
    string WindowId,
    string CompressionId,
    double Ratio
);

/// <summary>
/// Context manager options.
/// </summary>
public record ContextManagerOptions
{
    /// <summary>
    /// Maximum tokens before triggering compression.
    /// </summary>
    public int MaxTokens { get; init; } = 50_000;

    /// <summary>
    /// Target tokens after compression (for regular messages; pinned budget is subtracted automatically).
    /// </summary>
    public int CompressToTokens { get; init; } = 30_000;

    /// <summary>
    /// Model to use for LLM compression summary. Null means use the agent's primary model.
    /// </summary>
    public string? CompressionModel { get; init; }

    /// <summary>
    /// System prompt for LLM compression summary. Empty means use the built-in default prompt.
    /// </summary>
    public string CompressionPrompt { get; init; } = "";

    /// <summary>
    /// When true, the LLM summarizer is asked to maintain a core-memory block (always-in-context
    /// task state: objective, modified files, key decisions). Inspired by MemGPT (arxiv 2310.08560).
    /// </summary>
    public bool EnableCoreMemory { get; init; } = true;

    /// <summary>
    /// Minimum number of recent regular messages always retained regardless of token budget.
    /// Guards against the edge case where the pinned summary stack grows large enough to consume
    /// the entire CompressToTokens budget, leaving the agent with no recent context.
    /// Default: 6 (≈ 3 user-assistant pairs).
    /// </summary>
    public int MinRecentMessages { get; init; } = 6;

    /// <summary>
    /// Maximum depth of the stacked summary messages before triggering recursive merge.
    /// When the stack reaches this limit, all existing summaries are merged into one via the
    /// summarizer, preventing unbounded token accumulation from the summary stack itself.
    /// Default: 5.
    /// </summary>
    public int MaxSummaryDepth { get; init; } = 5;

    /// <summary>
    /// Options for compressing oversized individual tool results before they are written
    /// into the message history. Disabled by default; opt-in per session.
    /// </summary>
    public ToolResultCompressionOptions? ToolResultCompression { get; init; }
}

/// <summary>
/// History window for storing compressed context.
/// </summary>
public record HistoryWindow
{
    public required string Id { get; init; }
    public required IReadOnlyList<Message> Messages { get; init; }
    public IReadOnlyList<Timeline> Events { get; init; } = Array.Empty<Timeline>();
    public required HistoryWindowStats Stats { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>
/// History window statistics.
/// </summary>
public record HistoryWindowStats(
    int MessageCount,
    int TokenCount,
    int EventCount = 0
);

/// <summary>
/// Compression record for auditing.
/// </summary>
public record CompressionRecord
{
    public required string Id { get; init; }
    public required string WindowId { get; init; }
    public required CompressionConfig Config { get; init; }
    public required string Summary { get; init; }
    public required double Ratio { get; init; }
    public IReadOnlyList<string>? RecoveredFiles { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>
/// Compression configuration.
/// </summary>
public record CompressionConfig(
    string Model,
    string Prompt,
    int Threshold
);

/// <summary>
/// Recovered file snapshot (used by store/history).
/// </summary>
public record RecoveredFile
{
    public required string Path { get; init; }
    public required string Content { get; init; }
    public long Mtime { get; init; }
    public required long Timestamp { get; init; }
}

/// <summary>
/// Manages context window and compression using a three-layer architecture:
/// <list type="bullet">
///   <item>Layer 1 – Core-memory block: always in context, updated by LLM each compression.</item>
///   <item>Layer 2 – Summary stack: pinned system messages that accumulate across compressions.</item>
///   <item>Layer 3 – Recent messages: selected by token budget and importance score.</item>
/// </list>
///
/// References:
///   - MemGPT (arxiv 2310.08560): tiered virtual memory for LLM agents
///   - LLMLingua-2 (arxiv 2403.12968): importance-based token selection
///   - Tokenization fairness (arxiv 2305.15425): CJK chars ≈ 1.5× tokens
/// </summary>
public class ContextManager
{
    private readonly IAgentStore _store;
    private readonly string _agentId;
    private readonly ContextManagerOptions _options;
    private readonly IContextSummarizer _summarizer;
    private readonly ILogger<ContextManager>? _logger;

    // XML tags used to identify pinned system messages.
    private const string SummaryTag = "<context-summary";
    private const string CoreMemoryTag = "<core-memory";

    public ContextManager(
        IAgentStore store,
        string agentId,
        ContextManagerOptions? options = null,
        IContextSummarizer? summarizer = null,
        ILogger<ContextManager>? logger = null)
    {
        _store = store;
        _agentId = agentId;
        _options = options ?? new ContextManagerOptions();
        _summarizer = summarizer ?? new StaticContextSummarizer();
        _logger = logger;
    }

    /// <summary>
    /// Analyze context usage with CJK-aware token estimation.
    /// CJK characters (Chinese/Japanese/Korean) count as ~1.5 tokens each;
    /// other characters use the standard 4:1 ratio.
    /// Source: arxiv 2305.15425 — measured 1.76× for Mandarin vs English baseline.
    /// </summary>
    /// <param name="messages">Conversation messages to analyze.</param>
    /// <param name="systemPromptTokens">
    /// Pre-computed token estimate for the system prompt. Added to the total so that
    /// compression is triggered before the system prompt itself crowds out all headroom.
    /// Pass 0 (default) when the system prompt is already included in <paramref name="messages"/>
    /// as a system-role message, or when the estimate is unavailable.
    /// </param>
    public ContextUsage Analyze(IReadOnlyList<Message> messages, int systemPromptTokens = 0)
    {
        var totalTokens = systemPromptTokens;

        foreach (var message in messages)
        {
            totalTokens += 4; // per-message overhead
            foreach (var block in message.Content)
            {
                var text = block switch
                {
                    TextContent t => t.Text,
                    ToolUseContent tu => JsonSerializer.Serialize(tu.Input),
                    ToolResultContent tr => tr.Content?.ToString() ?? "",
                    _ => ""
                };
                totalTokens += EstimateTextTokens(text);
            }
        }

        return new ContextUsage(
            TotalTokens: totalTokens,
            MessageCount: messages.Count,
            ShouldCompress: totalTokens > (int)(_options.MaxTokens * 0.9)
        );
    }

    /// <summary>
    /// Convenience wrapper: estimates token count for the given system prompt text
    /// using the same CJK-aware algorithm as <see cref="Analyze"/>.
    /// Call this once after building the system prompt, then pass the result to each
    /// <see cref="Analyze"/> call to keep the budget accurate.
    /// </summary>
    public static int EstimateSystemPromptTokens(string? systemPrompt) =>
        string.IsNullOrEmpty(systemPrompt) ? 0 : EstimateTextTokens(systemPrompt);

    /// <summary>
    /// Compress context using the three-layer architecture.
    /// <br/>
    /// Flow:
    /// <list type="number">
    ///   <item>Separate pinned messages (summaries + core-memory) from regular messages.</item>
    ///   <item>Select regular messages to retain by token budget and importance score.</item>
    ///   <item>Generate semantic summary (and optionally update core-memory) via summarizer.</item>
    ///   <item>Stack the new summary on top of existing summaries (never delete old ones).</item>
    ///   <item>Reconstruct: [core-memory?] + [summary stack] + [retained recent messages].</item>
    /// </list>
    /// </summary>
    public async Task<CompressionResult?> CompressAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<Timeline> events,
        IFilePool? filePool = null,
        ISandbox? sandbox = null,
        int systemPromptTokens = 0,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var usage = Analyze(messages, systemPromptTokens);
        // force=true skips the threshold check — used when the model already returned empty,
        // meaning the actual token count exceeds the window regardless of our estimate.
        if (!force && !usage.ShouldCompress)
            return null;

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windowId = $"window-{timestamp}";
        var compressionId = $"comp-{timestamp}";

        // ── 1. Save history window ────────────────────────────────────────────
        await SaveHistoryWindowAsync(new HistoryWindow
        {
            Id = windowId,
            Messages = messages,
            Events = events,
            Stats = new HistoryWindowStats(messages.Count, usage.TotalTokens, events.Count),
            Timestamp = timestamp
        }, cancellationToken);

        // ── 2. Separate pinned vs regular messages ────────────────────────────
        // Pinned = system messages containing a summary or core-memory XML tag.
        // These are NEVER removed; they form the persistent memory stack.
        // Single-pass categorisation: avoid 4× iteration + duplicate GetText() calls.
        Message? existingCoreMsg = null;
        var summaryStack = new List<Message>();
        var regularMessages = new List<Message>(messages.Count);
        var pinnedTokens = 0;
        foreach (var msg in messages)
        {
            var kind = ClassifyPinned(msg);
            switch (kind)
            {
                case PinnedKind.Summary:
                    summaryStack.Add(msg);
                    pinnedTokens += EstimateMessageTokens(msg);
                    break;
                case PinnedKind.CoreMemory:
                    existingCoreMsg ??= msg;
                    pinnedTokens += EstimateMessageTokens(msg);
                    break;
                default:
                    regularMessages.Add(msg);
                    break;
            }
        }
        // Reserve some headroom so the new summary itself fits within CompressToTokens.
        var regularBudget = Math.Max(0, _options.CompressToTokens - pinnedTokens - systemPromptTokens - 1200);

        // A single tool_result larger than this cap is elided even when it sits
        // in the minRecent protection window. Picking budget/4 keeps normal
        // fs_read payloads untouched while catching pathological cases like a
        // parallel_research result that dwarfs the rest of the conversation.
        var singleMessageCap = Math.Max(1_000, regularBudget / 4);

        // ── 4. Select regular messages by importance + token budget ───────────
        var (retainedRegular, removedMessages) = SelectMessagesByBudget(
            regularMessages, regularBudget, _options.MinRecentMessages, singleMessageCap);

        // ── 5. Sanitize orphan tool results in the retained set ───────────────
        retainedRegular = SanitizeOrphanToolResults(retainedRegular);

        // ── 6. Generate summary (and optionally update core-memory) ───────────
        var summaryResult = await _summarizer.SummarizeAsync(removedMessages, _options, cancellationToken);

        // ── 7. Build new summary message (stacked, never replaced) ───────────
        var newSummaryMsg = Message.System(
            $"<context-summary timestamp=\"{DateTimeOffset.UtcNow:O}\" window=\"{windowId}\">\n{summaryResult.Summary}\n</context-summary>"
        );

        // ── 7b. Merge summary stack if depth limit reached ────────────────────
        // Prevents the summary stack from accumulating unbounded tokens across many compressions.
        // When the limit is hit, all existing summaries are recursively merged into one.
        // The merge may also return an updated core-memory block (P4 fix).
        string? mergedCoreMemoryUpdate = null;
        if (summaryStack.Count >= _options.MaxSummaryDepth)
        {
            var originalDepth = summaryStack.Count;
            var (mergedMsg, coreUpdate) = await MergeSummaryStackAsync(summaryStack, cancellationToken);
            summaryStack = [mergedMsg];
            mergedCoreMemoryUpdate = coreUpdate;
            _logger?.LogInformation(
                "Merged {Depth} summary messages into one (MaxSummaryDepth={Limit})",
                originalDepth, _options.MaxSummaryDepth);
        }

        // ── 8. Update or preserve core-memory block ───────────────────────────
        // Priority: current-compression update > merge update > keep existing.
        var effectiveCoreUpdate = summaryResult.CoreMemoryUpdate ?? mergedCoreMemoryUpdate;
        Message? coreMsg = null;
        if (_options.EnableCoreMemory && !string.IsNullOrWhiteSpace(effectiveCoreUpdate))
        {
            coreMsg = Message.System(
                $"<core-memory updated=\"{DateTimeOffset.UtcNow:O}\">\n{effectiveCoreUpdate}\n</core-memory>"
            );
        }
        else if (existingCoreMsg != null)
        {
            coreMsg = existingCoreMsg; // keep unchanged
        }

        // ── 9. Reconstruct full message list ──────────────────────────────────
        // Order: [core-memory?] [summary-1] … [summary-N] [new-summary] [recent…]
        var reconstructed = new List<Message>();
        if (coreMsg != null) reconstructed.Add(coreMsg);
        reconstructed.AddRange(summaryStack);
        reconstructed.Add(newSummaryMsg);
        reconstructed.AddRange(retainedRegular);

        // ── 10. Save compression record ───────────────────────────────────────
        var ratio = (double)retainedRegular.Count / Math.Max(1, regularMessages.Count);
        await SnapshotAccessedFilesAsync(filePool, sandbox, timestamp, cancellationToken);

        var summaryPreview = summaryResult.Summary.Length > 500
            ? summaryResult.Summary[..500]
            : summaryResult.Summary;

        await SaveCompressionRecordAsync(new CompressionRecord
        {
            Id = compressionId,
            WindowId = windowId,
            Config = new CompressionConfig(
                _options.CompressionModel ?? "default",
                _options.CompressionPrompt,
                _options.MaxTokens
            ),
            Summary = summaryPreview,
            Ratio = ratio,
            Timestamp = timestamp
        }, cancellationToken);

        _logger?.LogInformation(
            "Compressed context: {Removed} regular messages removed, {Retained} retained " +
            "(ratio {Ratio:P}); summary stack depth {Depth}; core-memory {CoreStatus}",
            removedMessages.Count, retainedRegular.Count, ratio,
            summaryStack.Count + 1,
            coreMsg != null ? "updated" : "none");

        return new CompressionResult(
            Summary: newSummaryMsg,
            RemovedMessages: removedMessages,
            RetainedMessages: reconstructed,
            WindowId: windowId,
            CompressionId: compressionId,
            Ratio: ratio
        );
    }

    // ── Public query methods ──────────────────────────────────────────────────

    public Task<IReadOnlyList<HistoryWindow>> LoadHistoryAsync(CancellationToken cancellationToken = default)
        => _store.LoadHistoryWindowsAsync(_agentId, cancellationToken);

    public Task<IReadOnlyList<CompressionRecord>> LoadCompressionsAsync(CancellationToken cancellationToken = default)
        => _store.LoadCompressionRecordsAsync(_agentId, cancellationToken);

    public Task<IReadOnlyList<RecoveredFile>> LoadRecoveredFilesAsync(CancellationToken cancellationToken = default)
        => _store.LoadRecoveredFilesAsync(_agentId, cancellationToken);

    // ── Message selection ─────────────────────────────────────────────────────

    /// <summary>
    /// Selects messages to retain within the given token budget using importance scoring.
    /// Low-score messages (polling calls, old assistant responses) are removed first.
    /// The last <paramref name="minRecentCount"/> messages are always retained regardless of budget,
    /// ensuring the agent retains at least some recent context even when the pinned summary stack
    /// is very large.
    /// <br/>
    /// tool_use / tool_result pairs are treated as atomic units: removing a tool_use message
    /// also removes its paired tool_result message (and vice versa). This prevents the
    /// SanitizeOrphanToolResults pass from having to rewrite tool results that lost their pair.
    /// <br/>
    /// Scoring formula inspired by LLMLingua-2 budget controller:
    ///   Score = Recency(0-40) + Role(0-30) + ToolType(-20 to +20)
    /// </summary>
    private static (List<Message> retained, List<Message> removed) SelectMessagesByBudget(
        IReadOnlyList<Message> messages, int tokenBudget, int minRecentCount = 0, int? singleMessageTokenCap = null)
    {
        if (messages.Count == 0)
            return (new List<Message>(), new List<Message>());

        // ── Pre-pass: shrink oversized tool_result payloads ──────────────────
        // A single tool_result larger than singleMessageTokenCap is replaced with
        // an elided placeholder even when the message is in the minRecent window.
        // Without this, one ~300 KB parallel_research result can anchor permanent
        // context overflow that normal compression cannot fix (the oversized
        // message sits inside the protected recent window).
        var working = messages;
        if (singleMessageTokenCap is int cap && cap > 0)
        {
            List<Message>? shrunk = null;
            for (var i = 0; i < messages.Count; i++)
            {
                var replaced = ShrinkOversizedToolResults(messages[i], cap);
                if (!ReferenceEquals(replaced, messages[i]))
                {
                    shrunk ??= messages.ToList();
                    shrunk[i] = replaced;
                }
            }
            if (shrunk != null)
                working = shrunk;
        }

        var total = working.Sum(EstimateMessageTokens);
        if (total <= tokenBudget)
            return (working.ToList(), new List<Message>());

        // ── Build tool_use/tool_result pair maps ──────────────────────────────
        // toolUseIndex[toolUseId] = message index that contains the tool_use block.
        var toolUseIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        // toolResultIndex[toolUseId] = message index that contains the matching tool_result block.
        var toolResultIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < working.Count; i++)
        {
            foreach (var block in working[i].Content)
            {
                if (block is ToolUseContent tu)
                    toolUseIndex[tu.Id] = i;
                else if (block is ToolResultContent tr)
                    toolResultIndex[tr.ToolUseId] = i;
            }
        }

        // ── Protected set ─────────────────────────────────────────────────────
        // The last minRecentCount messages are protected — never removed regardless of budget.
        var protectedStart = Math.Max(0, working.Count - minRecentCount);
        var protectedIndices = new HashSet<int>(
            Enumerable.Range(protectedStart, working.Count - protectedStart));

        // Score each message; lower score = candidate for removal first.
        var scored = working
            .Select((msg, i) => (msg, index: i, score: ScoreMessage(msg, i, working.Count),
                tokens: EstimateMessageTokens(msg)))
            .ToList();

        // ── Greedily remove lowest-score non-protected messages ────────────────
        var toRemove = new HashSet<int>();
        var currentTokens = total;

        foreach (var candidate in scored.OrderBy(s => s.score))
        {
            if (currentTokens <= tokenBudget) break;
            if (protectedIndices.Contains(candidate.index)) continue;
            if (toRemove.Contains(candidate.index)) continue; // already removed as a pair

            // Atomically remove the tool_use/tool_result pair if both are present.
            var paired = FindPairedMessageIndex(candidate.msg, candidate.index, toolUseIndex, toolResultIndex);

            // Skip if the paired message is protected — we must keep both or neither.
            if (paired.HasValue && protectedIndices.Contains(paired.Value)) continue;

            toRemove.Add(candidate.index);
            currentTokens -= candidate.tokens;

            if (paired.HasValue && !toRemove.Contains(paired.Value))
            {
                toRemove.Add(paired.Value);
                currentTokens -= scored[paired.Value].tokens;
            }
        }

        var retained = scored
            .Where(s => !toRemove.Contains(s.index))
            .OrderBy(s => s.index)
            .Select(s => s.msg)
            .ToList();

        var removed = scored
            .Where(s => toRemove.Contains(s.index))
            .OrderBy(s => s.index)
            .Select(s => s.msg)
            .ToList();

        return (retained, removed);
    }

    /// <summary>
    /// Finds the paired message index for a tool_use ↔ tool_result relationship.
    /// Returns null if the message has no pair or the pair is not in the index.
    /// </summary>
    private static int? FindPairedMessageIndex(
        Message msg,
        int msgIndex,
        Dictionary<string, int> toolUseIndex,
        Dictionary<string, int> toolResultIndex)
    {
        // If this message contains a tool_use, find the message with the matching tool_result.
        foreach (var block in msg.Content)
        {
            if (block is ToolUseContent tu && toolResultIndex.TryGetValue(tu.Id, out var resultIdx) && resultIdx != msgIndex)
                return resultIdx;
        }

        // If this message contains a tool_result, find the message with the matching tool_use.
        foreach (var block in msg.Content)
        {
            if (block is ToolResultContent tr && toolUseIndex.TryGetValue(tr.ToolUseId, out var useIdx) && useIdx != msgIndex)
                return useIdx;
        }

        return null;
    }

    /// <summary>
    /// Importance score for a single message (higher = keep, lower = remove first).
    /// Components:
    ///   - Recency   [0–40]: proportional to position in the message list
    ///   - Role      [0–30]: user instructions > assistant text > other
    ///   - Tool type [-20–+20]: write/workspace ops positive; pure polling negative
    /// </summary>
    private static int ScoreMessage(Message msg, int index, int total)
    {
        var recency = (int)((double)index / total * 40);

        var roleScore = msg.Role switch
        {
            MessageRole.User => 30,
            MessageRole.Assistant => 15,
            _ => 0
        };

        var toolScore = 0;
        var toolNames = msg.Content.OfType<ToolUseContent>().Select(t => t.Name).ToList();
        if (toolNames.Count > 0)
        {
            if (toolNames.Any(n => n.StartsWith("fs_write", StringComparison.OrdinalIgnoreCase)
                                || n.StartsWith("workspace_", StringComparison.OrdinalIgnoreCase)))
                toolScore = 20;   // mutations are important to preserve
            else if (toolNames.All(n => string.Equals(n, "bash_logs", StringComparison.OrdinalIgnoreCase)))
                toolScore = -20;  // pure polling has no lasting value
        }

        return recency + roleScore + toolScore;
    }

    // ── Token estimation ──────────────────────────────────────────────────────

    /// <summary>
    /// CJK-aware token estimation for a single message.
    /// Per-message overhead is 4 tokens (role + formatting metadata).
    /// </summary>
    private static int EstimateMessageTokens(Message msg)
    {
        var tokens = 4;
        foreach (var block in msg.Content)
        {
            var text = block switch
            {
                TextContent t => t.Text,
                ToolUseContent tu => SafeSerializeForEstimate(tu.Input),
                ToolResultContent tr => SafeSerializeForEstimate(tr.Content),
                _ => ""
            };
            tokens += EstimateTextTokens(text);
        }
        return tokens;
    }

    // object.ToString() on a List / JsonElement / anonymous type returns the type name,
    // not the actual payload — this caused a ~300 KB tool_result to be estimated at zero
    // tokens, preventing compression from ever triggering. Serialize to JSON for a faithful
    // byte-count proxy; fall back to ToString() only if serialization throws.
    private static string SafeSerializeForEstimate(object? value)
    {
        if (value is null) return "";
        if (value is string s) return s;
        try { return JsonSerializer.Serialize(value); }
        catch { return value.ToString() ?? ""; }
    }

    // Returns the message unchanged if no tool_result block exceeds the cap; otherwise
    // returns a copy where each oversized tool_result has its Content replaced with an
    // elided marker (ToolUseId is preserved so tool_use/tool_result pairing stays intact).
    private static Message ShrinkOversizedToolResults(Message msg, int tokenCap)
    {
        if (tokenCap <= 0) return msg;

        List<ContentBlock>? rewritten = null;
        for (var i = 0; i < msg.Content.Count; i++)
        {
            if (msg.Content[i] is ToolResultContent tr)
            {
                var serialized = SafeSerializeForEstimate(tr.Content);
                var blockTokens = EstimateTextTokens(serialized);
                if (blockTokens > tokenCap)
                {
                    rewritten ??= new List<ContentBlock>(msg.Content);
                    rewritten[i] = tr with
                    {
                        Content = $"[tool_result elided by ContextManager: {serialized.Length:N0} bytes / ~{blockTokens:N0} tokens, tool_use_id={tr.ToolUseId}]"
                    };
                }
            }
        }
        return rewritten is null ? msg : msg with { Content = rewritten };
    }

    /// <summary>
    /// Estimates token count for a string.
    /// CJK Unified Ideographs, Hiragana, Katakana, and Hangul count as 2.0 tokens/char.
    /// Other characters use the English baseline of 0.25 tokens/char (4:1 ratio).
    /// Source: "Language Model Tokenizers Introduce Unfairness Between Languages"
    ///         (Petrov et al., NeurIPS 2023, arxiv 2305.15425) — Mandarin measured at 1.76×.
    ///         We use 2.0× (up from 1.5×) to cover GLM-series and other tokenizers that exceed
    ///         the 1.76× figure, reducing the risk of context overflow on Chinese-heavy sessions.
    /// </summary>
    private static int EstimateTextTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var cjk = 0;
        foreach (var c in text)
        {
            if ((c >= 0x4E00 && c <= 0x9FFF)   // CJK Unified Ideographs
             || (c >= 0x3040 && c <= 0x30FF)   // Hiragana + Katakana
             || (c >= 0xAC00 && c <= 0xD7AF))  // Hangul Syllables
                cjk++;
        }

        var other = text.Length - cjk;
        return (int)(cjk * 2.0 + other * 0.25) + 1;
    }

    // ── Pinned message detection ──────────────────────────────────────────────

    // Single-pass classification: inspects content blocks once instead of allocating a joined string
    // and scanning it twice (previously IsSummaryMessage + IsCoreMemoryMessage each called GetText()).
    private enum PinnedKind { None, Summary, CoreMemory }

    private static PinnedKind ClassifyPinned(Message msg)
    {
        if (msg.Role != MessageRole.System) return PinnedKind.None;
        foreach (var block in msg.Content)
        {
            if (block is TextContent t)
            {
                var text = t.Text;
                if (text.Contains(SummaryTag, StringComparison.Ordinal)) return PinnedKind.Summary;
                if (text.Contains(CoreMemoryTag, StringComparison.Ordinal)) return PinnedKind.CoreMemory;
            }
        }
        return PinnedKind.None;
    }

    // Still used by MergeSummaryStackAsync to read summary body text.
    private static string GetText(Message msg)
    {
        // Short-circuit the common single-TextContent case to avoid List+Join allocation.
        if (msg.Content.Count == 1 && msg.Content[0] is TextContent only) return only.Text;
        return string.Join("", msg.Content.OfType<TextContent>().Select(t => t.Text));
    }

    // ── Existing helpers (unchanged) ──────────────────────────────────────────

    /// <summary>
    /// Sanitize orphan tool results that lost their paired tool_use due to compression.
    /// </summary>
    private static List<Message> SanitizeOrphanToolResults(List<Message> messages)
    {
        var toolUseIds = new HashSet<string>();
        foreach (var msg in messages)
            foreach (var block in msg.Content.OfType<ToolUseContent>())
                toolUseIds.Add(block.Id);

        var result = new List<Message>();
        foreach (var msg in messages)
        {
            var newContent = new List<ContentBlock>();
            var modified = false;

            foreach (var block in msg.Content)
            {
                if (block is ToolResultContent tr && !toolUseIds.Contains(tr.ToolUseId))
                {
                    newContent.Add(new TextContent
                    {
                        Text = $"[Previous tool result: {tr.Content?.ToString() ?? "(empty)"}]"
                    });
                    modified = true;
                }
                else
                {
                    newContent.Add(block);
                }
            }

            result.Add(modified ? msg with { Content = newContent } : msg);
        }

        return result;
    }

    /// <summary>
    /// Recursively merges a summary stack into a single summary message.
    /// Each existing summary is wrapped as a user message so the summarizer can process it
    /// through its normal pipeline (LLM path or static fallback).
    /// The resulting merged summary carries a <c>merged="true"</c> attribute to aid debugging.
    /// <br/>
    /// Returns both the merged summary message and any core-memory update produced by the LLM,
    /// so the caller can propagate the update to the core-memory block (P4 fix).
    /// </summary>
    private async Task<(Message mergedSummary, string? coreMemoryUpdate)> MergeSummaryStackAsync(
        IReadOnlyList<Message> summaryStack,
        CancellationToken cancellationToken)
    {
        // Wrap each existing summary text as a user message so SummarizeAsync can process it.
        var fakeMessages = summaryStack
            .Select(s => Message.User($"[Previous summary]:\n{GetText(s)}"))
            .ToList();

        var result = await _summarizer.SummarizeAsync(fakeMessages, _options, cancellationToken);

        var windowId = $"merged-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var mergedMsg = Message.System(
            $"<context-summary merged=\"true\" timestamp=\"{DateTimeOffset.UtcNow:O}\" window=\"{windowId}\">\n{result.Summary}\n</context-summary>"
        );
        return (mergedMsg, result.CoreMemoryUpdate);
    }

    private async Task SnapshotAccessedFilesAsync(
        IFilePool? filePool, ISandbox? sandbox, long timestamp, CancellationToken ct)
    {
        if (filePool == null || sandbox == null) return;

        foreach (var f in filePool.GetAccessedFiles().Take(5))
        {
            try
            {
                var content = await sandbox.ReadFileAsync(f.Path, ct);
                await _store.SaveRecoveredFileAsync(_agentId,
                    new RecoveredFile { Path = f.Path, Content = content, Mtime = f.ModifiedTime, Timestamp = timestamp },
                    ct);
            }
            catch (Exception ex)
            {
                await _store.SaveRecoveredFileAsync(_agentId,
                    new RecoveredFile { Path = f.Path, Content = $"// Failed to read: {ex.Message}", Mtime = f.ModifiedTime, Timestamp = timestamp },
                    ct);
            }
        }
    }

    private async Task SaveHistoryWindowAsync(HistoryWindow window, CancellationToken ct)
    {
        await _store.SaveHistoryWindowAsync(_agentId, window, ct);
        _logger?.LogDebug("Saved history window {WindowId}", window.Id);
    }

    private async Task SaveCompressionRecordAsync(CompressionRecord record, CancellationToken ct)
    {
        await _store.SaveCompressionRecordAsync(_agentId, record, ct);
        _logger?.LogDebug("Saved compression record {RecordId}", record.Id);
    }
}

/// <summary>
/// Interface for file access tracking.
/// </summary>
public interface IFilePool
{
    IReadOnlyList<AccessedFile> GetAccessedFiles();
}

/// <summary>
/// Accessed file information.
/// </summary>
public record AccessedFile(string Path, long ModifiedTime);
