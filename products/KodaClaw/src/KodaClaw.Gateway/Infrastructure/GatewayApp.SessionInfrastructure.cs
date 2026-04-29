using KodaClaw.Contracts;
using KodaClaw.Runtime;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Store.Json;
using System.IO;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Runtime.Sessions;

public static partial class GatewayApp
{
    private static async Task<IReadOnlyList<SessionDetail>> LoadSessionsAsync(
        string workspaceRoot,
        string? activeMainSessionId,
        int limit,
        CancellationToken cancellationToken)
    {
        var store = CreateSessionStore(workspaceRoot);
        var sessionIds = await store.ListAsync(cancellationToken);
        if (sessionIds.Count == 0)
        {
            return [];
        }

        var details = new List<SessionDetail>(sessionIds.Count);
        foreach (var sessionId in sessionIds)
        {
            var detail = await LoadSessionDetailAsync(store, workspaceRoot, sessionId, activeMainSessionId, cancellationToken);
            if (detail is not null)
            {
                details.Add(detail);
            }
        }

        return details
            .OrderByDescending(static detail => detail.LastEventAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(static detail => detail.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(static detail => detail.SessionId, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    private static Task<SessionDetail?> LoadSessionDetailAsync(
        string workspaceRoot,
        string sessionId,
        string? activeMainSessionId,
        CancellationToken cancellationToken)
    {
        var store = CreateSessionStore(workspaceRoot);
        return LoadSessionDetailAsync(store, workspaceRoot, sessionId, activeMainSessionId, cancellationToken);
    }

    private static async Task<SessionDetail?> LoadSessionDetailAsync(
        IAgentStore store,
        string workspaceRoot,
        string sessionId,
        string? activeMainSessionId,
        CancellationToken cancellationToken)
    {
        var info = await store.LoadInfoAsync(sessionId, cancellationToken);
        if (info is null)
        {
            return null;
        }

        var messages = await store.LoadMessagesAsync(sessionId, cancellationToken);
        var toolCalls = await store.LoadToolCallRecordsAsync(sessionId, cancellationToken);

        var pendingApprovalCallIds = toolCalls
            .Where(static call =>
                call.State == ToolCallState.ApprovalRequired &&
                call.Approval.Required &&
                string.IsNullOrWhiteSpace(call.Approval.Decision))
            .Select(static call => call.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var messageCount = messages.Count > 0 ? messages.Count : info.MessageCount;
        var userMessageCount = messages.Count(static message => message.Role == MessageRole.User);
        var assistantMessageCount = messages.Count(static message => message.Role == MessageRole.Assistant);

        var rawTitle = messages
            .Where(static m => m.Role == MessageRole.User)
            .SelectMany(static m => m.Content.OfType<TextContent>().Select(static t => t.Text))
            .FirstOrDefault(static t => IsRealUserText(t));
        var title = rawTitle is { Length: > 60 } ? rawTitle[..60] + "\u2026" : rawTitle;
        var sessionDirectory = Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.SessionsDirectory, sessionId);
        var promptReport = await SessionPromptReportStore.TryReadAsync(
            sessionDirectory,
            cancellationToken);
        var promptReportHistory = await SessionPromptReportStore.TryReadHistoryAsync(
            sessionDirectory,
            cancellationToken);
        var promptReportDelta = BuildPromptReportDelta(promptReportHistory);

        return new SessionDetail(
            SessionId: sessionId,
            SessionKind: ResolveSessionKind(sessionId),
            Status: new SessionStatusSummary(
                IsActiveMainSession: string.Equals(activeMainSessionId, sessionId, StringComparison.Ordinal),
                BreakpointState: info.Breakpoint?.ToString(),
                MessageCount: messageCount,
                PendingApprovalCount: pendingApprovalCallIds.Length),
            CreatedAt: ParseDateTimeOffset(info.CreatedAt),
            LastEventAt: ParseBookmarkTimestamp(info.LastBookmark?.Timestamp),
            UserMessageCount: userMessageCount,
            AssistantMessageCount: assistantMessageCount,
            ToolCallCount: toolCalls.Count,
            LastSfpIndex: info.LastSfpIndex,
            PendingApprovalCallIds: pendingApprovalCallIds,
            PromptReport: promptReport,
            PromptReportDelta: promptReportDelta,
            RecentPromptReports: promptReportHistory,
            Title: title);
    }

    private static async Task<SessionMessagesResponse> LoadSessionMessagesAsync(
        string workspaceRoot,
        string sessionId,
        int limit,
        int skip,
        CancellationToken cancellationToken)
    {
        var store = CreateSessionStore(workspaceRoot);
        var messages = await store.LoadMessagesAsync(sessionId, cancellationToken);

        // Expand messages into display items:
        //   - real user messages (skip system-injected content)
        //   - assistant text messages
        //   - tool_activity items from assistant tool-use calls (one item per ToolUseContent)
        // Each assistant message may expand to: 0-1 text item + N tool_activity items.
        var allItems = new List<SessionMessageItem>();
        foreach (var m in messages)
        {
            if (m.Role == MessageRole.User)
            {
                var text = string.Concat(m.Content.OfType<TextContent>().Select(static t => t.Text));
                if (IsRealUserText(text))
                    allItems.Add(new SessionMessageItem(Id: "", Role: "user", Text: text, Timestamp: null));
            }
            else if (m.Role == MessageRole.Assistant)
            {
                var text = string.Concat(m.Content.OfType<TextContent>().Select(static t => t.Text));
                if (!string.IsNullOrWhiteSpace(text))
                    allItems.Add(new SessionMessageItem(Id: "", Role: "assistant", Text: text, Timestamp: null));

                foreach (var toolUse in m.Content.OfType<ToolUseContent>())
                {
                    var inputPreview = toolUse.Input is not null
                        ? System.Text.Json.JsonSerializer.Serialize(toolUse.Input)
                        : null;
                    allItems.Add(new SessionMessageItem(
                        Id: "",
                        Role: "tool_activity",
                        Text: "",
                        Timestamp: null,
                        ToolName: toolUse.Name,
                        InputPreview: inputPreview));
                }
            }
        }

        var totalCount = allItems.Count;

        var items = allItems
            .AsEnumerable()
            .Reverse()
            .Skip(skip)
            .Take(limit)
            .Select(static (item, i) => item with { Id = $"history-{i}" })
            .ToArray();

        var hasMore = skip + limit < totalCount;

        return new SessionMessagesResponse(
            Items: items,
            TotalCount: totalCount,
            HasMore: hasMore);
    }

    private static PromptReportDelta? BuildPromptReportDelta(IReadOnlyList<PromptReport>? history)
    {
        if (history is not { Count: > 1 })
        {
            return null;
        }

        var current = history[0];
        var previous = history[1];
        var previousFiles = previous.LoadedContextFiles.ToHashSet(StringComparer.Ordinal);
        var currentFiles = current.LoadedContextFiles.ToHashSet(StringComparer.Ordinal);

        return new PromptReportDelta(
            PreviousGeneratedAt: previous.GeneratedAt,
            CharacterCountDelta: current.CharacterCount - previous.CharacterCount,
            TruncationStateChanged: current.WasTruncated != previous.WasTruncated,
            AddedContextFiles: current.LoadedContextFiles
                .Where(path => !previousFiles.Contains(path))
                .ToArray(),
            RemovedContextFiles: previous.LoadedContextFiles
                .Where(path => !currentFiles.Contains(path))
                .ToArray());
    }

    private static SessionKind ResolveSessionKind(string sessionId)
    {
        if (sessionId.StartsWith("channel-", StringComparison.OrdinalIgnoreCase)
            && sessionId.Contains("-dm-", StringComparison.OrdinalIgnoreCase))
        {
            return SessionKind.ChannelDirectMessage;
        }

        if (sessionId.StartsWith("channel-", StringComparison.OrdinalIgnoreCase)
            && sessionId.Contains("-group-", StringComparison.OrdinalIgnoreCase))
        {
            return SessionKind.ChannelGroup;
        }

        if (sessionId.StartsWith("auto-", StringComparison.OrdinalIgnoreCase))
        {
            return SessionKind.Automation;
        }

        if (sessionId.StartsWith("plugin-", StringComparison.OrdinalIgnoreCase))
        {
            return SessionKind.Plugin;
        }

        return SessionKind.Main;
    }

    private static IAgentStore CreateSessionStore(string workspaceRoot)
    {
        var sessionsRoot = Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.SessionsDirectory);
        return new JsonAgentStore(sessionsRoot);
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        return DateTimeOffset.TryParse(rawValue, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? ParseBookmarkTimestamp(long? rawTimestamp)
    {
        if (rawTimestamp is null || rawTimestamp.Value <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(rawTimestamp.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns true if the text is a genuine user message, not a system-injected block.
    /// Filters out:
    ///   - empty / whitespace
    ///   - &lt;system-reminder&gt;  — SDK reminder wrapper (skill activation, todo, etc.)
    ///   - &lt;skill_instructions  — skill body injected via RemindAsync
    ///   - [tool_result orphaned] — SDK sanitization of orphaned tool results
    /// </summary>
    private static bool IsRealUserText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.TrimStart();
        return !trimmed.StartsWith("<system-reminder>", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("<skill_instructions", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("[tool_result orphaned]", StringComparison.OrdinalIgnoreCase);
    }

    private static SessionStorageUsage ComputeSessionTypeUsage(string sessionsRoot, string prefix)
    {
        if (!Directory.Exists(sessionsRoot))
        {
            return new SessionStorageUsage { Count = 0, SizeBytes = 0 };
        }

        var dirs = Directory.GetDirectories(sessionsRoot)
            .Where(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        long totalBytes = 0;
        foreach (var dir in dirs)
        {
            try
            {
                totalBytes += new DirectoryInfo(dir)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(static f => f.Length);
            }
            catch
            {
                // 跳过无法访问的目录
            }
        }

        return new SessionStorageUsage { Count = dirs.Count, SizeBytes = totalBytes };
    }
}
