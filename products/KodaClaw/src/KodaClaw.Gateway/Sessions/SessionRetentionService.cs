using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Workspace;

namespace KodaClaw.Gateway;

/// <summary>
/// Cleans up session folders:
/// - auto-* sessions: by task grouping, retaining the most recent N runs within D days.
/// - main-* sessions: if a memory summary exists and session is older than 30 days (active session always skipped).
/// - channel-* sessions: if SUMMARY.md exists and session is older than 30 days.
/// </summary>
internal sealed class SessionRetentionService
{
    private const int SummarizedSessionRetentionDays = 30;

    private readonly IWorkspaceService _workspaceService;
    private readonly IDiagnosticsService? _diagnosticsService;
    private readonly ILogger<SessionRetentionService>? _logger;

    public SessionRetentionService(
        IWorkspaceService workspaceService,
        IDiagnosticsService? diagnosticsService = null,
        ILogger<SessionRetentionService>? logger = null)
    {
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _diagnosticsService = diagnosticsService;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var appConfig = await _workspaceService.LoadAppConfigAsync(cancellationToken);
        var retentionDays = appConfig.AutoSessionRetentionDays;
        var maxPerTask = appConfig.AutoSessionRetentionMaxPerTask;

        var sessionsRoot = Path.Combine(_workspaceService.RootPath, KodaClawWorkspaceLayout.SessionsDirectory);
        if (!Directory.Exists(sessionsRoot))
        {
            return;
        }

        var deleted = 0;
        deleted += await CleanAutoSessionsAsync(sessionsRoot, retentionDays, maxPerTask, cancellationToken);
        deleted += await CleanSummarizedSessionsAsync(sessionsRoot, appConfig.ActiveMainSessionId, cancellationToken);

        if (deleted > 0)
        {
            _diagnosticsService?.Record(new DiagnosticEvent(
                Id: Guid.NewGuid().ToString("N"),
                Source: "koda.gateway.session_retention",
                EventType: "session_retention.cleaned",
                Level: "info",
                Message: $"Session retention cleaned {deleted} expired session folders",
                Timestamp: DateTimeOffset.UtcNow,
                SessionId: null));
        }
    }

    private Task<int> CleanAutoSessionsAsync(
        string sessionsRoot,
        int retentionDays,
        int maxPerTask,
        CancellationToken cancellationToken)
    {
        var autoFolders = Directory.GetDirectories(sessionsRoot)
            .Where(d => Path.GetFileName(d).StartsWith("auto-", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (autoFolders.Count == 0)
        {
            return Task.FromResult(0);
        }

        // 只处理已完成的（有 meta.json 的），跳过正在执行中的
        var completed = autoFolders
            .Where(d => File.Exists(Path.Combine(d, "meta.json")))
            .ToList();

        _logger?.LogInformation(
            "SessionRetention: found {Total} auto- folders, {Completed} completed. retentionDays={Days}, maxPerTask={Max}",
            autoFolders.Count, completed.Count, retentionDays, maxPerTask);

        // 按任务 ID 分组（文件夹名格式：auto-{timestamp}-{taskId}-{suffix}）
        var groups = completed
            .GroupBy(d => ExtractTaskId(Path.GetFileName(d)))
            .ToList();

        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        var deleted = 0;

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 按创建时间降序排列（时间戳在文件夹名中，可直接字典序排序）
            var sorted = group
                .OrderByDescending(d => Path.GetFileName(d))
                .ToList();

            for (var i = 0; i < sorted.Count; i++)
            {
                var dir = sorted[i];
                var folderName = Path.GetFileName(dir);

                // 保留规则：排名在 maxPerTask 内 且 创建时间在 cutoff 之后，两者都满足才保留
                var withinCount = i < maxPerTask;
                var createdAt = ParseCreatedAt(dir) ?? ExtractTimestampFromName(folderName);
                var withinDays = createdAt > cutoff;

                if (withinCount && withinDays)
                {
                    continue;
                }

                deleted += TryDeleteSessionFolder(dir);
            }
        }

        _logger?.LogInformation("SessionRetention: deleted {Deleted} expired auto- session folders", deleted);
        return Task.FromResult(deleted);
    }

    private Task<int> CleanSummarizedSessionsAsync(
        string sessionsRoot,
        string? activeMainSessionId,
        CancellationToken cancellationToken)
    {
        var summariesDir = Path.Combine(
            _workspaceService.RootPath,
            KodaClawWorkspaceLayout.MemorySessionsDirectory);

        // Build a set of session IDs that have memory summaries
        var summarizedSessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(summariesDir))
        {
            foreach (var file in Directory.GetFiles(summariesDir, "*.md"))
            {
                var sessionId = ExtractSessionIdFromSummaryFile(Path.GetFileNameWithoutExtension(file));
                if (sessionId is not null)
                {
                    summarizedSessionIds.Add(sessionId);
                }
            }
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-SummarizedSessionRetentionDays);
        var deleted = 0;

        var allFolders = Directory.GetDirectories(sessionsRoot);
        foreach (var dir in allFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderName = Path.GetFileName(dir);

            var isMain = folderName.StartsWith("main-", StringComparison.OrdinalIgnoreCase);
            var isChannel = folderName.StartsWith("channel-", StringComparison.OrdinalIgnoreCase);
            if (!isMain && !isChannel)
            {
                continue;
            }

            // Active main session is always skipped
            if (isMain && string.Equals(folderName, activeMainSessionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Must have a memory summary or a SUMMARY.md inside the session folder
            var hasSummary = summarizedSessionIds.Contains(folderName)
                || File.Exists(Path.Combine(dir, "SUMMARY.md"));
            if (!hasSummary)
            {
                continue;
            }

            // Must be older than threshold
            var createdAt = ParseCreatedAt(dir) ?? ExtractTimestampFromName(folderName);
            if (createdAt > cutoff)
            {
                continue;
            }

            deleted += TryDeleteSessionFolder(dir);
        }

        if (deleted > 0)
        {
            _logger?.LogInformation("SessionRetention: deleted {Deleted} expired main/channel session folders with summaries", deleted);
        }

        return Task.FromResult(deleted);
    }

    /// <summary>
    /// Extract session ID from summary filename.
    /// Summary files are named like: 2026-03-25-main-20260325173802-abc123.md
    /// The session ID part is: main-20260325173802-abc123
    /// </summary>
    internal static string? ExtractSessionIdFromSummaryFile(string fileNameWithoutExtension)
    {
        // Format: {date}-{session-type}-{rest}
        // e.g. "2026-03-25-main-20260325173802-abc123" → "main-20260325173802-abc123"
        // Find the first occurrence of "main-" or "channel-" to extract the session ID portion
        var mainIdx = fileNameWithoutExtension.IndexOf("main-", StringComparison.OrdinalIgnoreCase);
        if (mainIdx >= 0)
        {
            return fileNameWithoutExtension[mainIdx..];
        }

        var channelIdx = fileNameWithoutExtension.IndexOf("channel-", StringComparison.OrdinalIgnoreCase);
        if (channelIdx >= 0)
        {
            return fileNameWithoutExtension[channelIdx..];
        }

        return null;
    }

    private int TryDeleteSessionFolder(string dir)
    {
        var folderName = Path.GetFileName(dir);
        try
        {
            Directory.Delete(dir, recursive: true);
            _logger?.LogDebug("SessionRetention: deleted {Folder}", folderName);
            TryDeleteArtifactsFolder(folderName);
            return 1;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SessionRetention: failed to delete {Folder}", folderName);
            return 0;
        }
    }

    private void TryDeleteArtifactsFolder(string sessionId)
    {
        try
        {
            var artifactsDir = Path.Combine(
                _workspaceService.RootPath,
                KodaClawWorkspaceLayout.CacheDirectory,
                "artifacts",
                sessionId);
            if (Directory.Exists(artifactsDir))
            {
                Directory.Delete(artifactsDir, recursive: true);
                _logger?.LogDebug(
                    "SessionRetention: cascaded artifacts delete for {Folder}", sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "SessionRetention: failed to cascade-delete artifacts for {Folder}",
                sessionId);
        }
    }

    /// <summary>
    /// 从文件夹名 auto-{timestamp}-{taskId}-{suffix} 中提取 taskId。
    /// </summary>
    private static string ExtractTaskId(string folderName)
    {
        // 格式: auto-20260324143022-daily-check-a3f2b1c4
        // 跳过前缀 "auto-" 和时间戳段，剩余部分视为 taskId（最后 8 位随机后缀不参与分组）
        var parts = folderName.Split('-');
        if (parts.Length < 4)
        {
            return folderName;
        }

        // parts[0] = "auto", parts[1] = timestamp, parts[^1] = 8位随机后缀
        // 中间部分拼回来作为 taskId
        return string.Join("-", parts[2..^1]);
    }

    /// <summary>
    /// 从 meta.json 的 createdAt 字段读取创建时间（更精确）。
    /// </summary>
    private static DateTimeOffset? ParseCreatedAt(string sessionDir)
    {
        try
        {
            var metaPath = Path.Combine(sessionDir, "meta.json");
            var json = File.ReadAllText(metaPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("createdAt", out var createdAtEl)
                && createdAtEl.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(createdAtEl.GetString(), out var result))
            {
                return result;
            }
        }
        catch
        {
            // 解析失败时降级为文件夹名时间戳
        }

        return null;
    }

    /// <summary>
    /// 从文件夹名中解析时间戳作为兜底（格式：auto-yyyyMMddHHmmss-...）。
    /// </summary>
    private static DateTimeOffset ExtractTimestampFromName(string folderName)
    {
        try
        {
            var parts = folderName.Split('-');
            if (parts.Length >= 2
                && DateTimeOffset.TryParseExact(
                    parts[1], "yyyyMMddHHmmss",
                    null, System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var result))
            {
                return result;
            }
        }
        catch
        {
            // ignore
        }

        return DateTimeOffset.MinValue;
    }
}
