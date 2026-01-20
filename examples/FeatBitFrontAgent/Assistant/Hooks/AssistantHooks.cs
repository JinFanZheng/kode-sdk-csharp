using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Hooks;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Tools;
using Kode.Agent.WebApiAssistant.Assistant;
using Kode.Agent.WebApiAssistant.Assistant.Hooks.Policies;
using Kode.Agent.WebApiAssistant.Assistant.Hooks.Utils;
using Microsoft.Extensions.Logging;

namespace Kode.Agent.WebApiAssistant.Assistant.Hooks;

/// <summary>
/// Compose all assistant policies into a single hooks implementation.
/// Following the TypeScript buildAssistantHooks pattern.
/// </summary>
public static class AssistantHooks
{
    /// <summary>
    /// Build assistant hooks with all policies composed.
    /// </summary>
    public static BuildAssistantHooksResult Build(
        AgentDependencies deps,
        BuildAssistantHooksOptions options,
        ILogger logger)
    {
        // Get MCP tool names
        var toolRegistry = deps.ToolRegistry as ToolRegistry;
        var allToolNames = toolRegistry?.List().ToList() ?? new List<string>();
        var mcpToolNames = allToolNames
            .Where(name => name.StartsWith("mcp__"))
            .ToList();
        var hasMcpTools = mcpToolNames.Count > 0;
        var hasChromeDevtoolsTools = mcpToolNames.Any(name => name.StartsWith("mcp__chrome-devtools__"));

        // Create profile store
        var profileStore = new ProfileStore(options.DataDir, logger);

        // Create leak protection policy
        var keepSkillInstructions = new HashSet<string>
        {
            "memory", "verify", "news", "weather", "fx", "flight", "rail",
            "itinerary", "hotel", "commute", "food"
        };
        var leakPolicy = new LeakProtectionPolicy(keepSkillInstructions, logger);

        // Create network policy
        string lastUserTextForNetwork = "";
        var networkPolicy = new NetworkPolicy(
            hasMcpTools,
            () => lastUserTextForNetwork,
            120_000,
            logger);

        // Create browse policy
        var browsePolicy = new BrowsePolicy(hasChromeDevtoolsTools, logger);

        // Create verify policy
        var verifyPolicy = new VerifyPolicy(
            profileStore,
            leakPolicy.RegisterInjectedMessage,
            MessageUtils.GetLastUserTextFromMessages,
            MessageUtils.GetLastUserTextFromAgent,
            logger);

        // Create memory recall policy
        var memoryRecallPolicy = new MemoryRecallPolicy(
            leakPolicy.RegisterInjectedMessage,
            MessageUtils.GetLastUserTextFromMessages,
            MessageUtils.GetLastUserTextFromAgent,
            logger);

        // Create env injector policy
        var envInjectorPolicy = new EnvInjectorPolicy(options.DataDir, logger);

        // Compose all hooks into a single IHooks implementation
        var composedHooks = new Kode.Agent.Sdk.Core.Hooks.Hooks
        {
            PreModel = async (request) =>
            {
                var messages = request.Messages;

                // Call preModel hooks in order
                if (verifyPolicy.PreModel != null)
                    await verifyPolicy.PreModel(messages);
                if (memoryRecallPolicy.PreModel != null)
                    await memoryRecallPolicy.PreModel(messages);
                if (leakPolicy.PreModel != null)
                    await leakPolicy.PreModel(messages);
            },

            PostModel = async (response) =>
            {
                // Call postModel hooks in order
                if (verifyPolicy.PostModel != null)
                    await verifyPolicy.PostModel(response);
                if (leakPolicy.PostModel != null)
                    await leakPolicy.PostModel(response);
            },

            PreToolUse = async (call, ctx) =>
            {
                // First, inject environment variables (doesn't return decision)
                if (envInjectorPolicy.PreToolUse != null)
                {
                    await envInjectorPolicy.PreToolUse(call, ctx);
                }

                // Call preToolUse hooks in order, return first non-null decision
                HookDecision? decision = null;

                if (verifyPolicy.PreToolUse != null)
                {
                    decision = await verifyPolicy.PreToolUse(call, ctx);
                    if (decision != null) return decision;
                }

                if (memoryRecallPolicy.PreToolUse != null)
                {
                    decision = await memoryRecallPolicy.PreToolUse(call, ctx);
                    if (decision != null) return decision;
                }

                if (browsePolicy.PreToolUse != null)
                {
                    decision = await browsePolicy.PreToolUse(call, ctx);
                    if (decision != null) return decision;
                }

                // Update last user text for network policy
                lastUserTextForNetwork = MessageUtils.GetLastUserTextFromAgent(ctx.Agent);

                if (networkPolicy.PreToolUse != null)
                {
                    decision = await networkPolicy.PreToolUse(call, ctx);
                    if (decision != null) return decision;
                }

                return null;
            },

            PostToolUse = async (outcome, ctx) =>
            {
                // First, normalize MCP error semantics with network policy
                PostHookResult? currentResult = null;

                if (networkPolicy.PostToolUse != null)
                {
                    currentResult = await networkPolicy.PostToolUse(outcome, ctx);
                }

                // Then apply browse fallback if applicable
                if (browsePolicy.PostToolUse != null)
                {
                    var browseResult = await browsePolicy.PostToolUse(outcome, ctx);
                    if (browseResult != null)
                    {
                        // Browse policy takes precedence
                        return browseResult;
                    }
                }

                return currentResult;
            },

            MessagesChanged = null
        };

        return new BuildAssistantHooksResult
        {
            Hooks = composedHooks,
            McpToolNames = mcpToolNames
        };
    }
}
