using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Hooks;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Kode.Agent.WebApiAssistant.Assistant.Hooks.Policies;

/// <summary>
/// Network policy to prefer MCP tools over bash for web requests.
/// </summary>
public record NetworkPolicy(
    bool HasMcpTools,
    Func<string> GetLastUserText,
    int McpFailureWindowMs = 120_000,
    ILogger? Logger = null)
{
    private long _lastMcpFailureAt;

    private static bool LooksLikeNetworkFetch(JsonElement args)
    {
        // Check if input has "cmd" property
        if (args.ValueKind != JsonValueKind.Object) return false;
        if (!args.TryGetProperty("cmd", out var cmdElement)) return false;
        var cmd = cmdElement.GetString();
        if (string.IsNullOrEmpty(cmd)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(cmd, @"\b(curl|wget)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ||
               System.Text.RegularExpressions.Regex.IsMatch(cmd, @"\bhttps?://\S+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool UserExplicitlyAskedForCurl(string userText)
    {
        if (string.IsNullOrEmpty(userText)) return false;
        var t = userText.ToLower();
        return t.Contains("curl") || t.Contains("wget") ||
               t.Contains("原始响应") || t.Contains("原始内容") || t.Contains("headers");
    }

    /// <summary>
    /// Pre-tool-use hook.
    /// </summary>
    public Func<ToolCall, ToolContext, Task<HookDecision?>>? PreToolUse => async (call, ctx) =>
    {
        if (call.Name == "bash_run")
        {
            if (LooksLikeNetworkFetch(call.Input))
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var inFailureWindow = _lastMcpFailureAt > 0 && now - _lastMcpFailureAt <= McpFailureWindowMs;
                var lastUser = GetLastUserText();
                var explicitly = UserExplicitlyAskedForCurl(lastUser);

                if (HasMcpTools && !inFailureWindow && !explicitly)
                {
                    return HookDecision.Deny("先别用终端直接抓网页哈。优先用网页检索/阅读去拿信息；只有当那边不可用或刚失败过，才用命令行兜底。");
                }
            }
        }

        await Task.CompletedTask;
        return null;
    };

    /// <summary>
    /// Post-tool-use hook.
    /// </summary>
    public Func<ToolOutcome, ToolContext, Task<PostHookResult?>>? PostToolUse => async (outcome, ctx) =>
    {
        // Normalize MCP error semantics
        if (outcome.Name != null && outcome.Name.StartsWith("mcp__"))
        {
            if (outcome.IsError)
            {
                _lastMcpFailureAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                // Already marked as error, just pass through
                return PostHookResult.Pass();
            }
        }

        await Task.CompletedTask;
        return null;
    };
}
