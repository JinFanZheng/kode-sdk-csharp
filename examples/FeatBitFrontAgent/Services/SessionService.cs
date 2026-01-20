using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.WebApiAssistant.Models.Entities;
using System.Collections.Concurrent;

namespace Kode.Agent.WebApiAssistant.Services;

/// <summary>
/// 会话管理服务实现
/// </summary>
public class SessionService : ISessionService
{
    private readonly IAgentStore _store;
    private readonly ILogger<SessionService> _logger;
    private readonly ConcurrentDictionary<string, Session> _cache = new();

    public SessionService(IAgentStore store, ILogger<SessionService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public Task<Session> CreateSessionAsync(string userId, string? title = null)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var agentId = $"session_{sessionId}";

        var session = new Session
        {
            SessionId = sessionId,
            UserId = userId,
            Title = title ?? "新对话",
            AgentId = agentId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MessageCount = 0
        };

        _cache[sessionId] = session;
        _logger.LogInformation("Created new session: {SessionId} for user: {UserId}", sessionId, userId);

        return Task.FromResult(session);
    }

    public Task<Session?> GetSessionAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.FromResult<Session?>(null);
        }

        _cache.TryGetValue(sessionId, out var session);
        return Task.FromResult<Session?>(session);
    }

    public Task<IReadOnlyList<Session>> ListSessionsAsync(string userId)
    {
        var sessions = _cache.Values
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<Session>>(sessions);
    }

    public Task DeleteSessionAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.CompletedTask;
        }

        if (_cache.TryRemove(sessionId, out var session))
        {
            _logger.LogInformation("Deleted session: {SessionId}", sessionId);
        }

        return Task.CompletedTask;
    }

    public Task UpdateSessionTitleAsync(string sessionId, string title)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.CompletedTask;
        }

        if (_cache.TryGetValue(sessionId, out var session))
        {
            session.Title = title;
            session.UpdatedAt = DateTime.UtcNow;
            _logger.LogInformation("Updated session title: {SessionId}", sessionId);
        }

        return Task.CompletedTask;
    }

    public async Task<Session> GetOrCreateSessionAsync(string userId, string? sessionId = null)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var existingSession = await GetSessionAsync(sessionId);
            if (existingSession != null && existingSession.UserId == userId)
            {
                existingSession.UpdatedAt = DateTime.UtcNow;
                return existingSession;
            }
        }

        // 列出用户的所有会话
        var sessions = await ListSessionsAsync(userId);
        if (sessions.Count > 0)
        {
            // 返回最近更新的会话
            var latestSession = sessions.OrderByDescending(s => s.UpdatedAt).First();
            latestSession.UpdatedAt = DateTime.UtcNow;
            return latestSession;
        }

        // 创建新会话
        return await CreateSessionAsync(userId);
    }

    /// <summary>
    /// 增加会话消息计数
    /// </summary>
    public void IncrementMessageCount(string sessionId)
    {
        if (_cache.TryGetValue(sessionId, out var session))
        {
            session.MessageCount++;
            session.UpdatedAt = DateTime.UtcNow;
        }
    }
}
