using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Hooks;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Kode.Agent.WebApiAssistant.Assistant.Hooks.Policies;

/// <summary>
/// Browse policy for web reader tools and chrome-devtools integration.
/// </summary>
public record BrowsePolicy(
    bool HasChromeDevtoolsTools,
    ILogger? Logger = null)
{
    private static bool IsWebReaderToolName(string name) =>
        name.StartsWith("mcp__") && System.Text.RegularExpressions.Regex.IsMatch(name, @"__webReader", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool IsChromeDevtoolsToolName(string name) =>
        name.StartsWith("mcp__chrome-devtools__");

    private static bool IsForbiddenUrl(string url, out string? reason)
    {
        reason = null;
        try
        {
            var uri = new Uri(url);
            var protocol = uri.Scheme.ToLower();
            if (protocol != "http" && protocol != "https")
            {
                reason = $"protocol {protocol} is not allowed";
                return true;
            }

            var hostname = uri.Host.ToLower();
            if (string.IsNullOrEmpty(hostname))
            {
                reason = "missing hostname";
                return true;
            }

            // Block localhost/loopback
            if (hostname == "localhost" || hostname == "127.0.0.1" || hostname == "::1")
            {
                reason = "localhost is not allowed";
                return true;
            }

            // Block link-local metadata
            if (hostname == "169.254.169.254")
            {
                reason = "link-local metadata host is not allowed";
                return true;
            }

            // Block private IPs (best-effort)
            var privateIpMatch = System.Text.RegularExpressions.Regex.Match(hostname, @"^(\d+)\.(\d+)\.(\d+)\.(\d+)$");
            if (privateIpMatch.Success)
            {
                var a = int.Parse(privateIpMatch.Groups[1].Value);
                var b = int.Parse(privateIpMatch.Groups[2].Value);
                if (a == 10 || (a == 172 && b >= 16 && b <= 31) || (a == 192 && b == 168) || a == 127 || a == 0)
                {
                    reason = "private IP not allowed";
                    return true;
                }
            }

            // Block .local domains
            if (hostname.EndsWith(".local"))
            {
                reason = ".local domains not allowed";
                return true;
            }

            return false;
        }
        catch
        {
            reason = "invalid URL";
            return true;
        }
    }

    private static string? FindUrlLikeString(JsonElement args)
    {
        // Check for url/href/link properties
        foreach (var prop in new[] { "url", "href", "link", "pageUrl", "targetUrl" })
        {
            if (args.TryGetProperty(prop, out var value))
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    var s = value.GetString();
                    if (!string.IsNullOrEmpty(s) && (s.StartsWith("http://") || s.StartsWith("https://")))
                        return s;
                }
            }
        }

        // If args itself is a string URL
        if (args.ValueKind == JsonValueKind.String)
        {
            var s = args.GetString();
            if (!string.IsNullOrEmpty(s) && (s.StartsWith("http://") || s.StartsWith("https://")))
                return s;
        }

        return null;
    }

    /// <summary>
    /// Pre-tool-use hook - blocks unsafe URLs for chrome-devtools.
    /// </summary>
    public Func<ToolCall, ToolContext, Task<HookDecision?>>? PreToolUse => async (call, ctx) =>
    {
        if (IsChromeDevtoolsToolName(call.Name))
        {
            var url = FindUrlLikeString(call.Input);
            if (url != null && IsForbiddenUrl(url, out var reason))
            {
                return HookDecision.Deny($"Blocked unsafe URL for browsing: {reason}");
            }
        }

        await Task.CompletedTask;
        return null;
    };

    /// <summary>
    /// Post-tool-use hook - suggests chrome-devtools fallback for failed web_reader calls.
    /// </summary>
    public Func<ToolOutcome, ToolContext, Task<PostHookResult?>>? PostToolUse => async (outcome, ctx) =>
    {
        if (!HasChromeDevtoolsTools) return null;
        if (!IsWebReaderToolName(outcome.Name ?? "")) return null;

        // Suggest fallback if web_reader failed
        if (outcome.IsError)
        {
            return PostHookResult.Pass(); // Simplified - just pass for now
        }

        await Task.CompletedTask;
        return null;
    };
}
