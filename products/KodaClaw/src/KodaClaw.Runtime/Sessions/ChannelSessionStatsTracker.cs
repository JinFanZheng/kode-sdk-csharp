using System.Collections.Concurrent;
using Kode.Agent.Sdk.Core.Abstractions;

namespace KodaClaw.Runtime;

/// <summary>
/// Per-channel-session runtime token statistics.
/// 仅用于 /info 命令展示，重启后清零（不持久化）。
/// </summary>
public sealed record ChannelSessionStats(
    long LastRunInputTokens,
    long CumulativeInputTokens,
    long CumulativeOutputTokens,
    int TurnCount,
    DateTimeOffset LastActivityAt);

public interface IChannelSessionStatsTracker
{
    /// <summary>
    /// 记录一次 Agent run 的 token 用量。
    /// </summary>
    void Record(string sessionId, TokenUsage? usage);

    /// <summary>
    /// 读取 session 的统计；未记录返回 null。
    /// </summary>
    ChannelSessionStats? Get(string sessionId);

    /// <summary>
    /// 清零 session 统计（rotation 时调用）。
    /// </summary>
    void Reset(string sessionId);
}

public sealed class ChannelSessionStatsTracker : IChannelSessionStatsTracker
{
    private readonly ConcurrentDictionary<string, ChannelSessionStats> _stats = new();

    public void Record(string sessionId, TokenUsage? usage)
    {
        if (usage is null || string.IsNullOrEmpty(sessionId))
            return;

        _stats.AddOrUpdate(
            sessionId,
            _ => new ChannelSessionStats(
                LastRunInputTokens: usage.InputTokens,
                CumulativeInputTokens: usage.InputTokens,
                CumulativeOutputTokens: usage.OutputTokens,
                TurnCount: 1,
                LastActivityAt: DateTimeOffset.UtcNow),
            (_, existing) => existing with
            {
                LastRunInputTokens = usage.InputTokens,
                CumulativeInputTokens = existing.CumulativeInputTokens + usage.InputTokens,
                CumulativeOutputTokens = existing.CumulativeOutputTokens + usage.OutputTokens,
                TurnCount = existing.TurnCount + 1,
                LastActivityAt = DateTimeOffset.UtcNow,
            });
    }

    public ChannelSessionStats? Get(string sessionId)
        => _stats.TryGetValue(sessionId, out var s) ? s : null;

    public void Reset(string sessionId)
        => _stats.TryRemove(sessionId, out _);
}
