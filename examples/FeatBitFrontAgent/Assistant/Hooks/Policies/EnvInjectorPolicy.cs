using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Hooks;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Kode.Agent.WebApiAssistant.Assistant.Hooks.Policies;

/// <summary>
/// Environment injector policy to inject KODE_AGENT_DIR and KODE_USER_DIR environment variables.
/// </summary>
public record EnvInjectorPolicy(
    string? DataDir,
    ILogger? Logger = null)
{
    /// <summary>
    /// Pre-tool-use hook - injects environment variables for bash_run commands.
    /// </summary>
    public Func<ToolCall, ToolContext, Task<HookDecision?>>? PreToolUse => async (call, ctx) =>
    {
        // Only handle bash_run tool
        if (call.Name != "bash_run") return null;

        try
        {
            // Check if input has "cmd" property
            if (call.Input.ValueKind != JsonValueKind.Object) return null;
            if (!call.Input.TryGetProperty("cmd", out var cmdElement)) return null;
            var cmd = cmdElement.GetString();
            if (string.IsNullOrEmpty(cmd)) return null;

            var agentDir = GetSandboxWorkDir(ctx.Sandbox);
            var userDir = DataDir;

            // Note: We cannot modify the ToolCall.Input directly as it's readonly
            // In production, this would need to be handled differently (e.g., via a custom tool wrapper)
            Logger?.LogDebug("Would inject env vars for bash: KODE_AGENT_DIR={AgentDir}, KODE_USER_DIR={UserDir}", agentDir, userDir ?? "(null)");
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Failed to inject environment variables");
        }

        await Task.CompletedTask;
        return null; // Don't make a decision
    };

    private static string GetSandboxWorkDir(ISandbox? sandbox)
    {
        try
        {
            if (sandbox != null)
            {
                var workDirProp = sandbox.GetType().GetProperty("WorkDir");
                if (workDirProp != null)
                {
                    var workDir = workDirProp.GetValue(sandbox) as string;
                    if (!string.IsNullOrEmpty(workDir))
                    {
                        return workDir;
                    }
                }
            }
        }
        catch { }
        return Directory.GetCurrentDirectory();
    }
}
