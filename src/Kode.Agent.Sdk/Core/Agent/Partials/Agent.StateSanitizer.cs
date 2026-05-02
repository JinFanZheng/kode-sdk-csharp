using Kode.Agent.Sdk.Core.Events;
using Microsoft.Extensions.Logging;

namespace Kode.Agent.Sdk.Core.Agent;

// Context-repair / seal helpers that operate on _messages + _toolRunner to restore
// tool_use ↔ tool_result pairing, cap legacy oversized results, and collapse duplicate
// reminders. Called from resume (Agent.StateRecovery) and from the crash-recovery path
// in lifecycle. None of these methods talk to the model; they only mutate local state
// (and persist via _dependencies.Store) before the next turn.
public sealed partial class Agent
{
    private sealed record SealPayload(object Payload, string Message);

    private SealPayload BuildSealPayload(string state, string toolUseId, string fallbackNote, ToolCallRecord? record = null)
    {
        var baseMessage = state switch
        {
            "APPROVAL_REQUIRED" => "工具在等待审批时会话中断，系统已自动封口。",
            "APPROVED" => "工具已通过审批但尚未执行，系统已自动封口。",
            "EXECUTING" => "工具执行过程中会话中断，系统已自动封口。",
            "PENDING" => "工具刚准备执行时会话中断，系统已自动封口。",
            _ => fallbackNote
        };

        var recommendations = state switch
        {
            "APPROVAL_REQUIRED" => new[] { "确认审批是否仍然需要", "如需继续，请重新触发工具并完成审批" },
            "APPROVED" => new[] { "确认工具输入是否仍然有效", "如需执行，请重新触发工具" },
            "EXECUTING" => new[] { "检查工具可能产生的副作用", "确认外部系统状态后再重试" },
            "PENDING" => new[] { "确认工具参数是否正确", "再次触发工具以继续流程" },
            _ => new[] { "检查封口说明并决定是否重试工具" }
        };

        var detail = new Dictionary<string, object?>
        {
            ["status"] = state,
            ["startedAt"] = record?.StartedAt,
            ["approval"] = record?.Approval,
            ["toolId"] = toolUseId,
            ["note"] = baseMessage
        };

        var payload = new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["error"] = baseMessage,
            ["data"] = detail,
            ["recommendations"] = recommendations
        };

        return new SealPayload(payload, baseMessage);
    }

    private void SealNonTerminalToolRecords(string note)
    {
        var terminal = new HashSet<ToolCallState>
        {
            ToolCallState.Completed,
            ToolCallState.Failed,
            ToolCallState.Denied,
            ToolCallState.Sealed
        };

        foreach (var record in _toolRunner.ActiveToolCalls)
        {
            if (terminal.Contains(record.State)) continue;

            var state = record.State switch
            {
                ToolCallState.ApprovalRequired => "APPROVAL_REQUIRED",
                ToolCallState.Approved => "APPROVED",
                ToolCallState.Executing => "EXECUTING",
                ToolCallState.Pending => "PENDING",
                _ => record.State.ToString().ToUpperInvariant()
            };

            var sealedPayload = BuildSealPayload(state, record.Id, note, record);
            _toolRunner.SealToolCall(record.Id, sealedPayload.Message, sealedPayload.Payload);
        }
    }

    private async Task<IReadOnlyList<ToolCallSnapshot>> AutoSealDanglingToolUsesAsync(string reason, CancellationToken cancellationToken)
    {
        var sealedSnapshots = new List<ToolCallSnapshot>();
        // Tracks modifications: (messageIndex, blocksToAdd) where blocksToAdd are prepended to an existing user message.
        var modifications = new List<(int Index, List<ContentBlock> Blocks)>();
        // Tracks insertions: (insertAtIndex, blocks) where a new user message is created.
        var insertions = new List<(int Index, List<ContentBlock> Blocks)>();
        var alreadyHandled = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < _messages.Count; i++)
        {
            var msg = _messages[i];
            if (msg.Role != MessageRole.Assistant) continue;

            var localToolUses = msg.Content.OfType<ToolUseContent>().ToList();
            if (localToolUses.Count == 0) continue;

            // Check which tool_results are present in the IMMEDIATELY NEXT message.
            // Anthropic requires every tool_use to have a matching tool_result in the
            // immediately following message — not just somewhere later in the history.
            var nextIndex = i + 1;
            var nextIsUserMsg = nextIndex < _messages.Count && _messages[nextIndex].Role == MessageRole.User;
            var toolResultsInNext = new HashSet<string>(StringComparer.Ordinal);
            if (nextIsUserMsg)
            {
                foreach (var res in _messages[nextIndex].Content.OfType<ToolResultContent>())
                    toolResultsInNext.Add(res.ToolUseId);
            }

            var missingBlocks = new List<ContentBlock>();
            foreach (var use in localToolUses)
            {
                if (alreadyHandled.Contains(use.Id)) continue;
                if (toolResultsInNext.Contains(use.Id)) continue; // already in the immediate next message

                _toolRunner.RegisterToolCall(use.Id, use.Name, use.Input);
                var existing = _toolRunner.GetToolCall(use.Id);
                var sealedPayload = BuildSealPayload("TOOL_RESULT_MISSING", use.Id, reason, existing);
                _toolRunner.SealToolCall(use.Id, sealedPayload.Message, sealedPayload.Payload);
                var snapshot = _toolRunner.GetSnapshot(use.Id);
                if (snapshot != null) sealedSnapshots.Add(snapshot);

                missingBlocks.Add(new ToolResultContent
                {
                    ToolUseId = use.Id,
                    Content = sealedPayload.Payload,
                    IsError = true
                });
                alreadyHandled.Add(use.Id);
            }

            if (missingBlocks.Count == 0) continue;

            if (nextIsUserMsg)
            {
                // The immediately next message is already a user message — prepend the missing
                // tool_results to it so ALL tool_uses are covered in that single message.
                // This avoids creating consecutive user messages, which violates the Anthropic
                // API constraint that tool_results must be in the message IMMEDIATELY after the
                // assistant message that issued the tool_use.
                modifications.Add((nextIndex, missingBlocks));
            }
            else
            {
                // No user message immediately after — insert a new one.
                insertions.Add((nextIndex, missingBlocks));
            }
        }

        if (modifications.Count == 0 && insertions.Count == 0) return sealedSnapshots;

        // Apply modifications in reverse index order so indices stay valid.
        foreach (var (idx, blocksToAdd) in modifications.OrderByDescending(m => m.Index))
        {
            var existing = _messages[idx];
            var newContent = new List<ContentBlock>(blocksToAdd); // missing results first
            newContent.AddRange(existing.Content);               // then original content
            _messages[idx] = existing with { Content = newContent };
        }

        // Apply insertions in reverse index order.
        foreach (var (insertAt, blocks) in insertions.OrderByDescending(ins => ins.Index))
        {
            _messages.Insert(insertAt, new Message { Role = MessageRole.User, Content = blocks });
        }

        await _hookManager.RunMessagesChangedAsync(_messages, cancellationToken);
        return sealedSnapshots;
    }

    private async Task<int> SanitizeOrphanToolResultsAsync(
        CancellationToken cancellationToken,
        string note = "Sanitized orphan tool_result blocks (missing tool_use).")
    {
        var toolUseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var msg in _messages.Where(m => m.Role == MessageRole.Assistant))
        {
            foreach (var use in msg.Content.OfType<ToolUseContent>())
            {
                toolUseIds.Add(use.Id);
            }
        }

        var changedAny = false;
        var converted = 0;
        for (var i = 0; i < _messages.Count; i++)
        {
            var msg = _messages[i];
            if (msg.Role != MessageRole.User) continue;

            var changed = false;
            var next = new List<ContentBlock>();
            foreach (var block in msg.Content)
            {
                if (block is ToolResultContent tr && !toolUseIds.Contains(tr.ToolUseId))
                {
                    changed = true;
                    converted++;
                    var preview = PreviewToolResult(tr.Content, 1400);
                    next.Add(new TextContent
                    {
                        Text = $"[tool_result orphaned] tool_use_id={tr.ToolUseId}{(tr.IsError ? " (error)" : "")}\n{preview}"
                    });
                }
                else
                {
                    next.Add(block);
                }
            }

            if (changed)
            {
                changedAny = true;
                _messages[i] = msg with { Content = next };
            }
        }

        if (changedAny)
        {
            try
            {
                await _dependencies.Store.SaveMessagesAsync(AgentId, _messages, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to persist messages after context repair");
            }

            _eventBus.EmitMonitor(new ContextRepairEvent
            {
                Type = "context_repair",
                Reason = "orphan_tool_result",
                Converted = converted,
                Note = note
            });
        }

        return converted;
    }

    // Anthropic requires exactly one tool_result per tool_use. Older repair paths or
    // persisted buggy sessions may leave duplicate tool_result blocks behind; keep the
    // earliest occurrence and drop later duplicates so the next model call remains valid.
    private async Task<int> SanitizeDuplicateToolResultsAsync(
        CancellationToken cancellationToken,
        string note = "Sanitized duplicate tool_result blocks (same tool_use_id repeated).")
    {
        var seenToolResultIds = new HashSet<string>(StringComparer.Ordinal);
        var removed = 0;
        var changedAny = false;

        for (var i = 0; i < _messages.Count; i++)
        {
            var msg = _messages[i];
            if (msg.Role != MessageRole.User) continue;

            var changed = false;
            var next = new List<ContentBlock>(msg.Content.Count);
            foreach (var block in msg.Content)
            {
                if (block is ToolResultContent tr)
                {
                    if (!seenToolResultIds.Add(tr.ToolUseId))
                    {
                        changed = true;
                        removed++;
                        continue;
                    }
                }

                next.Add(block);
            }

            if (!changed) continue;

            changedAny = true;
            if (next.Count == 0)
            {
                _messages.RemoveAt(i);
                i--;
            }
            else
            {
                _messages[i] = msg with { Content = next };
            }
        }

        if (changedAny)
        {
            try
            {
                await _dependencies.Store.SaveMessagesAsync(AgentId, _messages, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to persist messages after duplicate tool_result repair");
            }

            _eventBus.EmitMonitor(new ContextRepairEvent
            {
                Type = "context_repair",
                Reason = "duplicate_tool_result",
                Converted = removed,
                Note = note
            });
        }

        return removed;
    }

    // Collapses runs of identical user-role reminder messages into a single copy.
    // Triggered at resume for sessions that predate the MessageQueue DedupKey and
    // accumulated 10+ copies of the same file-change reminder. Only collapses exact
    // byte-equal payloads and only within a run of consecutive user messages, so
    // real user turns or tool_results can never be collapsed.
    private int CollapseConsecutiveDuplicateReminders()
    {
        var removed = 0;
        for (var i = _messages.Count - 1; i > 0; i--)
        {
            var cur = _messages[i];
            var prev = _messages[i - 1];
            if (cur.Role != MessageRole.User || prev.Role != MessageRole.User) continue;
            if (!IsSingleTextReminder(cur, out var curText)) continue;
            if (!IsSingleTextReminder(prev, out var prevText)) continue;
            if (!string.Equals(curText, prevText, StringComparison.Ordinal)) continue;
            _messages.RemoveAt(i);
            removed++;
        }
        return removed;
    }

    private static bool IsSingleTextReminder(Message msg, out string text)
    {
        text = "";
        if (msg.Content.Count != 1) return false;
        if (msg.Content[0] is not TextContent t) return false;
        if (!t.Text.StartsWith("<system-reminder>", StringComparison.Ordinal)) return false;
        text = t.Text;
        return true;
    }

    /// <summary>
    /// Walks <c>_messages</c> once and offloads any legacy oversized <see cref="ToolResultContent"/>
    /// payload to the configured artifact-backed compressor. Idempotent: the compressor skips
    /// content that is already a placeholder, already below threshold, or produced by a verbatim
    /// tool. Failures are swallowed so resume never aborts on legacy data.
    /// </summary>
    /// <returns>(count, bytes) pair: number of payloads replaced and the total original byte size.</returns>
    private async Task<(int count, long bytes)> OffloadLegacyOversizedToolResultsAsync(CancellationToken cancellationToken)
    {
        if (_toolResultCompressor is null) return (0, 0);
        var options = _config.Context?.ToolResultCompression;
        if (options is null || !options.Enabled) return (0, 0);

        // Build toolUseId → toolName index by scanning forward. A tool_result at index i can
        // only refer to a tool_use on a prior Assistant message, so a single pass suffices.
        var toolNameById = new Dictionary<string, string>(StringComparer.Ordinal);
        var replacedCount = 0;
        long replacedBytes = 0;

        for (var i = 0; i < _messages.Count; i++)
        {
            var msg = _messages[i];

            if (msg.Role == MessageRole.Assistant)
            {
                foreach (var block in msg.Content)
                {
                    if (block is ToolUseContent tu && !string.IsNullOrEmpty(tu.Id))
                        toolNameById[tu.Id] = tu.Name;
                }
                continue;
            }

            if (msg.Role != MessageRole.User) continue;

            List<ContentBlock>? rebuilt = null;
            for (var j = 0; j < msg.Content.Count; j++)
            {
                var block = msg.Content[j];
                if (block is not ToolResultContent tr)
                {
                    rebuilt?.Add(block);
                    continue;
                }

                if (!toolNameById.TryGetValue(tr.ToolUseId, out var toolName))
                {
                    // Unknown tool (tool_use gone or not yet seen): safest to leave alone.
                    rebuilt?.Add(block);
                    continue;
                }

                try
                {
                    var replacement = await _toolResultCompressor.TryOffloadLegacyContentAsync(
                        toolName, tr.Content, options, cancellationToken);

                    if (replacement is null)
                    {
                        rebuilt?.Add(block);
                        continue;
                    }

                    // Record approximate original byte size for diagnostics.
                    var originalBytes = tr.Content switch
                    {
                        null => 0,
                        string s => s.Length,
                        _ => TryEstimateBytes(tr.Content)
                    };
                    replacedBytes += originalBytes;
                    replacedCount++;

                    rebuilt ??= new List<ContentBlock>(msg.Content.Take(j));
                    rebuilt.Add(tr with { Content = replacement });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Per-block failure must not derail resume. Leave the original inline;
                    // the compressor's internal logger has already recorded the failure.
                    rebuilt?.Add(block);
                }
            }

            if (rebuilt is not null)
            {
                _messages[i] = msg with { Content = rebuilt };
            }
        }

        return (replacedCount, replacedBytes);
    }

    private static int TryEstimateBytes(object content)
    {
        try { return System.Text.Json.JsonSerializer.Serialize(content).Length; }
        catch { return 0; }
    }

    /// <summary>
    /// Removes trailing user turns that were flushed but never answered by the model.
    /// These arise when a run fails (e.g. empty response, network error) after
    /// <see cref="MessageQueue.FlushAsync"/> has already appended the user message but
    /// before an assistant response was produced.  If not cleaned up, every subsequent
    /// request carries the un-answered turn, causing models to see consecutive user
    /// messages and often producing more empty or malformed responses.
    ///
    /// Rule: collect non-tool-result User messages that appear after the last Assistant
    /// message.  If there are two or more, the earlier ones are dangling — remove them
    /// and keep only the latest (the current request that was just flushed).
    /// Tool-result messages (User role, <see cref="ToolResultContent"/> blocks) are
    /// excluded to avoid breaking tool_use / tool_result pairing.
    /// </summary>
    /// <returns>Number of messages removed.</returns>
    private int SanitizeDanglingUserTurns()
    {
        // Index of the first message after the last assistant response (0 when no assistant exists).
        var afterLastAssistant = _messages.FindLastIndex(m => m.Role == MessageRole.Assistant) + 1;

        // Collect indices of non-tool-result User messages after the last assistant.
        var candidateIndices = new List<int>();
        for (var i = afterLastAssistant; i < _messages.Count; i++)
        {
            var msg = _messages[i];
            if (msg.Role == MessageRole.User && !msg.Content.Any(c => c is ToolResultContent))
                candidateIndices.Add(i);
        }

        // Need at least 2: the dangling one(s) + the current request (keep the last, remove the rest).
        if (candidateIndices.Count < 2) return 0;

        // Remove in reverse-index order to keep indices valid.
        var toRemove = candidateIndices
            .Take(candidateIndices.Count - 1)
            .OrderByDescending(i => i)
            .ToList();

        foreach (var idx in toRemove)
            _messages.RemoveAt(idx);

        return toRemove.Count;
    }

    private static string PreviewToolResult(object? value, int limit)
    {
        try
        {
            var text = value is string s ? s : System.Text.Json.JsonSerializer.Serialize(value);
            return text.Length > limit ? text[..limit] + "…" : text;
        }
        catch
        {
            var text = value?.ToString() ?? "";
            return text.Length > limit ? text[..limit] + "…" : text;
        }
    }
}
