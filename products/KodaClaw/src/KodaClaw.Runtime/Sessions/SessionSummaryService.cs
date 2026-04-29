using System.Text;
using System.Text.Json;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Workspace;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;

namespace KodaClaw.Runtime.Sessions;

/// <summary>
/// Context provided when requesting a session summary.
/// </summary>
public sealed record MemorySessionSummaryContext(
    string SessionId,
    string SessionType,
    string? BindingId,
    IReadOnlyList<Message> Messages,
    bool IsResumed = false);

/// <summary>
/// Structured session summary produced by LLM analysis.
/// </summary>
public sealed record MemorySessionSummary(
    string SessionId,
    string Date,
    string SessionType,
    IReadOnlyList<string> Topics,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> UserExpressions,
    IReadOnlyList<string> FollowUps,
    string? PrivacyLevel = "normal",
    string? Supersedes = null);

/// <summary>
/// Service that generates structured session summaries using LLM.
/// </summary>
public interface IMemorySessionSummaryService
{
    Task<MemorySessionSummary?> GenerateSummaryAsync(
        MemorySessionSummaryContext context,
        CancellationToken cancellationToken = default);
}

public sealed class MemorySessionSummaryService : IMemorySessionSummaryService
{
    private const int MinMessageCount = 5;
    private const int MaxInputChars = 12000;
    private const int HeadChars = 2000;
    private const int TailChars = 10000;
    private const int MaxRetryAttempts = 2;
    private static readonly int[] RetryDelaysMs = [2000, 5000];
    private const int MaxPendingAttempts = 5;
    private const int PendingExpiryDays = 7;

    private readonly IWorkspaceService _workspaceService;
    private readonly IModelProvider? _modelProvider;
    private readonly IRuntimeConfigurationResolver? _runtimeConfigurationResolver;
    private readonly IDiagnosticsService? _diagnosticsService;

    public MemorySessionSummaryService(
        IWorkspaceService workspaceService,
        IModelProvider? modelProvider = null,
        IRuntimeConfigurationResolver? runtimeConfigurationResolver = null,
        IDiagnosticsService? diagnosticsService = null)
    {
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _modelProvider = modelProvider;
        _runtimeConfigurationResolver = runtimeConfigurationResolver;
        _diagnosticsService = diagnosticsService;
    }

    public async Task<MemorySessionSummary?> GenerateSummaryAsync(
        MemorySessionSummaryContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!ShouldGenerateSummary(context))
        {
            return null;
        }

        if (_modelProvider is null)
        {
            return null;
        }

        var conversationText = ExtractConversationText(context.Messages);
        if (string.IsNullOrWhiteSpace(conversationText))
        {
            return null;
        }

        string? priorSummaryFile = null;
        if (context.IsResumed)
        {
            priorSummaryFile = FindPriorSummaryFile(context.SessionId);
        }

        // Retry loop: up to MaxRetryAttempts retries (total MaxRetryAttempts + 1 attempts)
        Exception? lastException = null;
        for (var attempt = 0; attempt <= MaxRetryAttempts; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    _diagnosticsService?.Record(new DiagnosticEvent(
                        Id: Guid.NewGuid().ToString("N"),
                        Source: "koda.runtime.session_summary",
                        EventType: "session_summary.generation_retrying",
                        Level: "info",
                        Message: $"Retrying summary generation (attempt {attempt + 1}/{MaxRetryAttempts + 1})",
                        Timestamp: DateTimeOffset.UtcNow,
                        SessionId: context.SessionId));

                    await Task.Delay(RetryDelaysMs[attempt - 1], cancellationToken);
                }

                var summary = await GenerateWithLlmAsync(context, conversationText, priorSummaryFile, cancellationToken);
                if (summary is not null)
                {
                    await WriteSummaryFileAsync(summary, cancellationToken);
                }

                return summary;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        // All attempts failed — write pending file for deferred retry
        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: "koda.runtime.session_summary",
            EventType: "session_summary.generation_failed",
            Level: "warning",
            Message: $"All {MaxRetryAttempts + 1} attempts failed: {lastException?.GetBaseException().Message}",
            Timestamp: DateTimeOffset.UtcNow,
            SessionId: context.SessionId));

        await WritePendingFileAsync(context, conversationText, cancellationToken);
        return null;
    }

    internal static bool ShouldGenerateSummary(MemorySessionSummaryContext context)
    {
        if (string.Equals(context.SessionType, "automation", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var userMessageCount = context.Messages.Count(m => m.Role == MessageRole.User);
        if (userMessageCount < MinMessageCount)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Detects whether the user explicitly requested privacy in the conversation.
    /// Only matches explicit keywords — no semantic inference to avoid false positives.
    /// </summary>
    internal static string DetectPrivacyLevel(string conversationText)
    {
        // Check for explicit privacy markers in user messages
        ReadOnlySpan<string> privacyMarkers =
        [
            "不要记录", "别记这个", "这个保密", "不用记",
            "private session", "don't record this", "keep this private",
        ];

        foreach (var marker in privacyMarkers)
        {
            if (conversationText.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return "private";
            }
        }

        return "normal";
    }

    internal static string ExtractConversationText(IReadOnlyList<Message> messages)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            string role;
            if (msg.Role == MessageRole.User)
            {
                role = "User";
            }
            else if (msg.Role == MessageRole.Assistant)
            {
                role = "Assistant";
            }
            else
            {
                continue;
            }

            foreach (var block in msg.Content)
            {
                if (block is TextContent text && !string.IsNullOrWhiteSpace(text.Text))
                {
                    sb.AppendLine($"[{role}]: {text.Text}");
                }
            }
        }

        var fullText = sb.ToString();
        if (fullText.Length <= MaxInputChars)
        {
            return fullText;
        }

        // Head + tail truncation to preserve both early context and recent decisions
        var head = fullText[..HeadChars];
        var tail = fullText[^TailChars..];
        return $"{head}\n\n[...中间部分省略...]\n\n{tail}";
    }

    private async Task<MemorySessionSummary?> GenerateWithLlmAsync(
        MemorySessionSummaryContext context,
        string conversationText,
        string? priorSummaryFile,
        CancellationToken cancellationToken)
    {
        var privacyLevel = DetectPrivacyLevel(conversationText);

        if (privacyLevel == "private")
        {
            return new MemorySessionSummary(
                SessionId: context.SessionId,
                Date: DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"),
                SessionType: context.SessionType,
                Topics: ["(private session)"],
                Keywords: [],
                Decisions: [],
                UserExpressions: [],
                FollowUps: [],
                PrivacyLevel: "private");
        }

        var resumedSection = "";
        if (priorSummaryFile is not null)
        {
            resumedSection = """

            重要上下文：这是一个 resumed session（用户恢复了之前的历史会话后继续对话）。
            该 session 之前已生成过摘要。请特别注意：
            - 如果用户推翻或修正了之前的决策，在 decisions 中明确标注"（修正）"前缀
            - 如果用户补充了之前的讨论，在 decisions 中标注"（补充）"前缀
            - 普通的新讨论不需要标注前缀

            """;
        }

        var prompt = $$"""
            分析以下对话并生成结构化摘要。仅输出 JSON，不要包含其他文本。

            JSON 格式：
            {
              "topics": ["主题1", "主题2"],
              "keywords": ["关键词1", "关键词2"],
              "decisions": ["做出的决策或结论"],
              "user_expressions": ["用户的重要原话或观点"],
              "follow_ups": ["待跟进事项"]
            }

            规则：
            - topics: 2-5 个主题
            - keywords: 3-8 个关键词
            - decisions: 只记录明确的决策，没有则为空数组
            - user_expressions: 只记录用户的重要原话，没有则为空数组
            - follow_ups: 只记录明确的待办，没有则为空数组
            {{resumedSection}}
            隐私规则（严格遵守）：
            - 绝对不要在摘要中包含密码、API Key、token、私钥、信用卡号等敏感信息
            - 如果对话中出现了敏感凭据，只记录"涉及凭据配置"等模糊描述，不要记录具体值
            - 用户明确表示"不用记"、"别记这个"、"这个保密"的内容，完全忽略不纳入摘要

            对话内容：
            {{conversationText}}
            """;

        var model = ResolveModel();
        var response = await _modelProvider!.CompleteAsync(
            new ModelRequest
            {
                Model = model,
                Messages = [Message.User(prompt)],
                MaxTokens = 1024,
                Temperature = 0.2,
            },
            cancellationToken);

        var responseText = string.Concat(
            response.Content.OfType<TextContent>().Select(c => c.Text)).Trim();

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        var summary = ParseSummaryJson(context, responseText);
        if (summary is not null && priorSummaryFile is not null)
        {
            summary = summary with { Supersedes = Path.GetFileName(priorSummaryFile) };
        }

        return summary;
    }

    internal static MemorySessionSummary? ParseSummaryJson(MemorySessionSummaryContext context, string json)
    {
        try
        {
            // Strip markdown code fences if present
            json = json.Trim();
            if (json.StartsWith("```"))
            {
                var firstNewline = json.IndexOf('\n');
                if (firstNewline >= 0)
                {
                    json = json[(firstNewline + 1)..];
                }

                if (json.EndsWith("```"))
                {
                    json = json[..^3];
                }

                json = json.Trim();
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new MemorySessionSummary(
                SessionId: context.SessionId,
                Date: DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"),
                SessionType: context.SessionType,
                Topics: ReadStringArray(root, "topics"),
                Keywords: ReadStringArray(root, "keywords"),
                Decisions: ReadStringArray(root, "decisions"),
                UserExpressions: ReadStringArray(root, "user_expressions"),
                FollowUps: ReadStringArray(root, "follow_ups"));
        }
        catch
        {
            return null;
        }
    }

    private async Task WriteSummaryFileAsync(MemorySessionSummary summary, CancellationToken cancellationToken)
    {
        var sessionsDir = Path.Combine(
            _workspaceService.RootPath,
            KodaClawWorkspaceLayout.MemorySessionsDirectory);
        Directory.CreateDirectory(sessionsDir);

        var shortId = summary.SessionId.Length > 8
            ? summary.SessionId[..8]
            : summary.SessionId;
        var timestamp = DateTimeOffset.UtcNow.ToString("HHmmss");
        var fileName = $"{summary.Date}-{summary.SessionType}-{shortId}-{timestamp}.md";
        var filePath = Path.Combine(sessionsDir, fileName);

        var sb = new StringBuilder();
        sb.AppendLine("# 会话摘要");
        sb.AppendLine();
        sb.AppendLine("## 基本信息");
        sb.AppendLine($"- 会话ID: {summary.SessionId}");
        sb.AppendLine($"- 日期: {summary.Date}");
        sb.AppendLine($"- 类型: {summary.SessionType}");
        if (summary.Supersedes is not null)
        {
            sb.AppendLine($"- 修正前摘要: {summary.Supersedes}");
        }
        if (summary.PrivacyLevel == "private")
        {
            sb.AppendLine($"- 隐私级别: private");
        }

        sb.AppendLine();

        if (summary.Topics.Count > 0)
        {
            sb.AppendLine("## 主题");
            foreach (var topic in summary.Topics)
            {
                sb.AppendLine($"- {topic}");
            }

            sb.AppendLine();
        }

        if (summary.Keywords.Count > 0)
        {
            sb.AppendLine("## 关键词");
            sb.AppendLine(string.Join(", ", summary.Keywords));
            sb.AppendLine();
        }

        if (summary.Decisions.Count > 0)
        {
            sb.AppendLine("## 重要决策");
            foreach (var decision in summary.Decisions)
            {
                sb.AppendLine($"- {decision}");
            }

            sb.AppendLine();
        }

        if (summary.UserExpressions.Count > 0)
        {
            sb.AppendLine("## 用户表达");
            foreach (var expr in summary.UserExpressions)
            {
                sb.AppendLine($"- \"{expr}\"");
            }

            sb.AppendLine();
        }

        if (summary.FollowUps.Count > 0)
        {
            sb.AppendLine("## 待跟进");
            foreach (var item in summary.FollowUps)
            {
                sb.AppendLine($"- [ ] {item}");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## 来源");
        sb.AppendLine($"- 原始会话: sessions/{summary.SessionId}");

        await File.WriteAllTextAsync(filePath, sb.ToString(), cancellationToken);

        await _workspaceService.TryCommitWorkspaceAsync(
            $"workspace(memory)[agent]: session summary {summary.Date} {summary.SessionType}",
            cancellationToken);
    }

    private string? FindPriorSummaryFile(string sessionId)
    {
        var sessionsDir = Path.Combine(
            _workspaceService.RootPath,
            KodaClawWorkspaceLayout.MemorySessionsDirectory);

        if (!Directory.Exists(sessionsDir))
        {
            return null;
        }

        var shortId = sessionId.Length > 8 ? sessionId[..8] : sessionId;

        // Find the most recent summary file for this session (by filename sort — contains date+time)
        var match = Directory.EnumerateFiles(sessionsDir, $"*-{shortId}-*.md")
            .OrderDescending()
            .FirstOrDefault();

        return match;
    }

    private string ResolveModel()
    {
        if (_runtimeConfigurationResolver is not null)
        {
            var snapshot = _runtimeConfigurationResolver.Resolve();
            if (!string.IsNullOrWhiteSpace(snapshot.DefaultModel))
            {
                return snapshot.DefaultModel.Trim();
            }
        }

        return "koda-main";
    }

    private async Task WritePendingFileAsync(
        MemorySessionSummaryContext context,
        string conversationText,
        CancellationToken cancellationToken)
    {
        try
        {
            var pendingDir = Path.Combine(
                _workspaceService.RootPath,
                KodaClawWorkspaceLayout.MemorySessionsPendingDirectory);
            Directory.CreateDirectory(pendingDir);

            var pendingFile = Path.Combine(pendingDir, $"{context.SessionId}.json");
            var pending = new PendingSummaryEntry
            {
                SessionId = context.SessionId,
                SessionType = context.SessionType,
                BindingId = context.BindingId,
                ConversationText = conversationText,
                CreatedAt = DateTimeOffset.UtcNow,
                AttemptCount = MaxRetryAttempts + 1,
                IsResumed = context.IsResumed,
            };

            var json = JsonSerializer.Serialize(pending, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(pendingFile, json, cancellationToken);

            _diagnosticsService?.Record(new DiagnosticEvent(
                Id: Guid.NewGuid().ToString("N"),
                Source: "koda.runtime.session_summary",
                EventType: "session_summary.pending_written",
                Level: "info",
                Message: $"Pending summary file written for session {context.SessionId}",
                Timestamp: DateTimeOffset.UtcNow,
                SessionId: context.SessionId));
        }
        catch (Exception ex)
        {
            _diagnosticsService?.Record(new DiagnosticEvent(
                Id: Guid.NewGuid().ToString("N"),
                Source: "koda.runtime.session_summary",
                EventType: "session_summary.pending_write_failed",
                Level: "warning",
                Message: ex.GetBaseException().Message,
                Timestamp: DateTimeOffset.UtcNow,
                SessionId: context.SessionId));
        }
    }

    /// <summary>
    /// Retries pending summaries from the .pending directory.
    /// Called at session startup to recover from previous failures.
    /// </summary>
    public async Task TryRetryPendingSummariesAsync(CancellationToken cancellationToken = default)
    {
        var pendingDir = Path.Combine(
            _workspaceService.RootPath,
            KodaClawWorkspaceLayout.MemorySessionsPendingDirectory);

        if (!Directory.Exists(pendingDir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(pendingDir, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken);
                var pending = JsonSerializer.Deserialize<PendingSummaryEntry>(json);
                if (pending is null)
                {
                    File.Delete(file);
                    continue;
                }

                // Expire old pending files (always, even without model provider)
                if (pending.AttemptCount >= MaxPendingAttempts ||
                    pending.CreatedAt.AddDays(PendingExpiryDays) < DateTimeOffset.UtcNow)
                {
                    File.Delete(file);
                    _diagnosticsService?.Record(new DiagnosticEvent(
                        Id: Guid.NewGuid().ToString("N"),
                        Source: "koda.runtime.session_summary",
                        EventType: "session_summary.pending_abandoned",
                        Level: "info",
                        Message: $"Pending summary abandoned for session {pending.SessionId} (attempts: {pending.AttemptCount}, age: {(DateTimeOffset.UtcNow - pending.CreatedAt).TotalDays:F1}d)",
                        Timestamp: DateTimeOffset.UtcNow,
                        SessionId: pending.SessionId));
                    continue;
                }

                // Cannot retry without a model provider
                if (_modelProvider is null)
                {
                    continue;
                }

                string? priorSummaryFile = null;
                if (pending.IsResumed)
                {
                    priorSummaryFile = FindPriorSummaryFile(pending.SessionId);
                }

                var context = new MemorySessionSummaryContext(
                    SessionId: pending.SessionId,
                    SessionType: pending.SessionType,
                    BindingId: pending.BindingId,
                    Messages: [],
                    IsResumed: pending.IsResumed);

                var summary = await GenerateWithLlmAsync(context, pending.ConversationText, priorSummaryFile, cancellationToken);
                if (summary is not null)
                {
                    await WriteSummaryFileAsync(summary, cancellationToken);
                    File.Delete(file);

                    _diagnosticsService?.Record(new DiagnosticEvent(
                        Id: Guid.NewGuid().ToString("N"),
                        Source: "koda.runtime.session_summary",
                        EventType: "session_summary.pending_retry_succeeded",
                        Level: "info",
                        Message: $"Pending summary retry succeeded for session {pending.SessionId}",
                        Timestamp: DateTimeOffset.UtcNow,
                        SessionId: pending.SessionId));
                }
                else
                {
                    // Increment attempt count
                    pending = pending with { AttemptCount = pending.AttemptCount + 1 };
                    var updatedJson = JsonSerializer.Serialize(pending, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(file, updatedJson, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _diagnosticsService?.Record(new DiagnosticEvent(
                    Id: Guid.NewGuid().ToString("N"),
                    Source: "koda.runtime.session_summary",
                    EventType: "session_summary.pending_retry_failed",
                    Level: "warning",
                    Message: ex.GetBaseException().Message,
                    Timestamp: DateTimeOffset.UtcNow,
                    SessionId: null));
            }
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.Add(value);
                }
            }
        }

        return result;
    }
}

/// <summary>
/// Serializable pending summary entry for deferred retry.
/// </summary>
internal sealed record PendingSummaryEntry
{
    public string SessionId { get; init; } = "";
    public string SessionType { get; init; } = "";
    public string? BindingId { get; init; }
    public string ConversationText { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public int AttemptCount { get; init; }
    public bool IsResumed { get; init; }
}
