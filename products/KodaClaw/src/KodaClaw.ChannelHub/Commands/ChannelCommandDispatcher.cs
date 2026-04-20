using System.Text;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Agent;
using Kode.Agent.Sdk.Core.Types;
using KodaClaw.Contracts;
using KodaClaw.Runtime;

namespace KodaClaw.ChannelHub.Commands;

/// <summary>
/// Dispatches control commands intercepted by <see cref="ChannelCommandParser"/>.
/// Returns true when the command was handled and the Orchestrator should return immediately.
/// </summary>
public sealed class ChannelCommandDispatcher
{
    private readonly IChannelSessionService _channelSessionService;
    private readonly ChannelDeliveryDispatchService _deliveryDispatchService;
    private readonly IWorkspaceService? _workspaceService;
    private readonly IModelProvider? _modelProvider;
    private readonly ISandboxFactory? _sandboxFactory;
    private readonly IProviderAccountRepository? _providerAccountRepository;

    public ChannelCommandDispatcher(
        IChannelSessionService channelSessionService,
        ChannelDeliveryDispatchService deliveryDispatchService,
        IWorkspaceService? workspaceService = null,
        IModelProvider? modelProvider = null,
        ISandboxFactory? sandboxFactory = null,
        IProviderAccountRepository? providerAccountRepository = null)
    {
        _channelSessionService = channelSessionService
            ?? throw new ArgumentNullException(nameof(channelSessionService));
        _deliveryDispatchService = deliveryDispatchService
            ?? throw new ArgumentNullException(nameof(deliveryDispatchService));
        _workspaceService = workspaceService;
        _modelProvider = modelProvider;
        _sandboxFactory = sandboxFactory;
        _providerAccountRepository = providerAccountRepository;
    }

    /// <summary>
    /// Dispatches the control command described by <paramref name="parsed"/>.
    /// Returns <c>true</c> when the command was handled (turn should be short-circuited).
    /// Returns <c>false</c> when the command is not a control command (turn should proceed normally).
    /// </summary>
    public async Task<bool> DispatchAsync(
        ParsedChannelCommand parsed,
        string sessionId,
        ChannelAccount account,
        ThreadBinding binding,
        CancellationToken cancellationToken)
    {
        if (!parsed.ControlKind.HasValue)
            return false;

        string? replyText = parsed.ControlKind switch
        {
            ChannelControlCommandKind.NewSession => await HandleNewSessionAsync(binding, parsed.ControlArg, cancellationToken),
            ChannelControlCommandKind.Status => await HandleStatusAsync(sessionId, cancellationToken),
            ChannelControlCommandKind.Stop => await HandleStopAsync(sessionId, cancellationToken),
            ChannelControlCommandKind.Help => BuildHelpMessage(),
            ChannelControlCommandKind.Compact => await HandleCompactAsync(sessionId, cancellationToken),
            ChannelControlCommandKind.Tools => await HandleToolsAsync(sessionId, cancellationToken),
            ChannelControlCommandKind.WhoAmI => await HandleWhoAmIAsync(cancellationToken),
            ChannelControlCommandKind.SideQuestion => await HandleSideQuestionAsync(parsed.ControlArg, sessionId, cancellationToken),
            ChannelControlCommandKind.Model => await HandleModelAsync(sessionId, parsed.ControlArg, cancellationToken),
            _ => null,
        };

        if (replyText is not null)
        {
            try
            {
                await _deliveryDispatchService.SendNotificationAsync(
                    account, binding, replyText, cancellationToken: cancellationToken);
            }
            catch
            {
                // Best-effort; caller's logger will handle the warning.
            }
        }

        return true;
    }

    // ── Command handlers ─────────────────────────────────────────────────────────

    private async Task<string> HandleNewSessionAsync(ThreadBinding binding, string? controlArg, CancellationToken ct)
    {
        string? modelOverride = null;
        string? modelDisplayName = null;

        if (controlArg is not null && _providerAccountRepository is not null)
        {
            var allModels = await _providerAccountRepository.ListAllModelsAsync(ct);
            var enabled = allModels.Where(m => m.Enabled).ToList();

            AccountModel? resolved = null;

            if (int.TryParse(controlArg, out var idx) && idx >= 1 && idx <= enabled.Count)
            {
                resolved = enabled[idx - 1];
            }
            else
            {
                resolved = enabled.FirstOrDefault(m =>
                    string.Equals(m.ModelId, controlArg, StringComparison.OrdinalIgnoreCase))
                    ?? enabled.FirstOrDefault(m =>
                    string.Equals(m.DisplayName, controlArg, StringComparison.OrdinalIgnoreCase));
            }

            if (resolved is null)
                return $"未找到模型 \"{controlArg}\"，请发送 /model list 查看可用列表。";

            modelOverride = resolved.ModelId;
            modelDisplayName = resolved.DisplayName;
        }
        else if (controlArg is not null && _providerAccountRepository is null)
        {
            return "模型注册表不可用，无法按指定模型创建会话。";
        }

        await _channelSessionService.RotateSessionAsync(binding, modelOverride, ct);

        return modelDisplayName is not null
            ? $"已开启新会话，使用模型 {modelDisplayName} ({modelOverride})。"
            : "已开启新会话。";
    }

    private async Task<string> HandleModelAsync(string sessionId, string? controlArg, CancellationToken ct)
    {
        if (controlArg is null)
        {
            var modelId = await _channelSessionService.GetSessionModelAsync(sessionId, ct);
            if (modelId is null)
                return "当前会话尚未创建，将使用默认模型。";

            if (_providerAccountRepository is not null)
            {
                var allModels = await _providerAccountRepository.ListAllModelsAsync(ct);
                var accountModel = allModels.FirstOrDefault(m => m.ModelId == modelId);
                if (accountModel is not null)
                {
                    var account = await _providerAccountRepository.GetAccountByIdAsync(accountModel.AccountId, ct);
                    var providerLabel = account?.ProviderKind switch
                    {
                        ModelProviderKind.Anthropic           => "Anthropic",
                        ModelProviderKind.AnthropicCompatible => "Anthropic兼容",
                        ModelProviderKind.OpenAI              => "OpenAI",
                        ModelProviderKind.OpenAICompatible    => "OpenAI兼容",
                        _                                     => account?.ProviderKind.ToString() ?? "Unknown"
                    };
                    return $"当前模型：{accountModel.DisplayName} [{accountModel.ModelId}]  {providerLabel}";
                }
            }
            return $"当前模型：{modelId}";
        }

        if (controlArg == "list")
        {
            if (_providerAccountRepository is null)
                return "模型注册表不可用。";

            var allModels = await _providerAccountRepository.ListAllModelsAsync(ct);
            var accounts = await _providerAccountRepository.ListAccountsAsync(ct);
            var accountLookup = accounts.ToDictionary(a => a.Id);
            var enabled = allModels.Where(m => m.Enabled).ToList();
            if (enabled.Count == 0)
                return "暂无可用模型。";

            var currentModelId = await _channelSessionService.GetSessionModelAsync(sessionId, ct);

            var sb = new StringBuilder();
            sb.AppendLine($"🤖 可用模型（共 {enabled.Count} 个）");
            sb.AppendLine();
            for (int i = 0; i < enabled.Count; i++)
            {
                var m = enabled[i];
                var providerLabel = accountLookup.TryGetValue(m.AccountId, out var acct) ? acct.ProviderKind switch
                {
                    ModelProviderKind.Anthropic           => "Anthropic",
                    ModelProviderKind.AnthropicCompatible => "Anthropic兼容",
                    ModelProviderKind.OpenAI              => "OpenAI",
                    ModelProviderKind.OpenAICompatible    => "OpenAI兼容",
                    _                                     => acct.ProviderKind.ToString()
                } : "Unknown";
                var current = m.ModelId == currentModelId ? "  ★ 当前" : "";
                sb.AppendLine($"  {i + 1}. {m.DisplayName,-20} [{m.ModelId}]  {providerLabel}{current}");
            }
            sb.AppendLine();
            sb.Append("发送 /new <序号> 或 /new <模型ID> 切换模型并开始新会话");
            return sb.ToString();
        }

        return "未知子命令。支持：/model（当前模型）、/model list（所有模型）。";
    }

    private async Task<string> HandleStatusAsync(string sessionId, CancellationToken ct)
    {
        var state = await _channelSessionService.GetSessionStateAsync(sessionId, ct);
        return FormatSessionStatusMessage(state);
    }

    private async Task<string> HandleStopAsync(string sessionId, CancellationToken ct)
    {
        return await _channelSessionService.StopCurrentTurnAsync(sessionId, ct);
    }

    private async Task<string> HandleCompactAsync(string sessionId, CancellationToken ct)
    {
        return await _channelSessionService.CompressSessionContextAsync(sessionId, ct);
    }

    private async Task<string> HandleToolsAsync(string sessionId, CancellationToken ct)
    {
        var tools = await _channelSessionService.GetSessionToolNamesAsync(sessionId, ct);
        if (tools.Count == 0)
            return "当前会话暂无可用工具（会话尚未创建或无工具配置）。";

        var sb = new StringBuilder();
        sb.AppendLine($"🔧 当前会话工具（共 {tools.Count} 个）");
        sb.AppendLine();
        foreach (var tool in tools.OrderBy(t => t))
            sb.AppendLine($"  • {tool}");
        return sb.ToString().TrimEnd();
    }

    private async Task<string> HandleWhoAmIAsync(CancellationToken ct)
    {
        if (_workspaceService is null)
            return "无法读取身份信息（WorkspaceService 未配置）。";

        try
        {
            var snapshot = await _workspaceService.GetSnapshotAsync(ct);
            var identityPath = Path.Combine(
                snapshot.RootPath,
                KodaClawWorkspaceLayout.WorkspaceDirectory,
                KodaClawWorkspaceLayout.IdentityFile);

            if (!File.Exists(identityPath))
                return "IDENTITY.md 未找到，身份尚未配置。";

            var content = await File.ReadAllTextAsync(identityPath, ct);
            // Return a preview of the first 600 chars to avoid flooding the channel
            if (content.Length > 600)
                content = content[..600] + "\n\n…（省略剩余内容）";

            return content;
        }
        catch (Exception)
        {
            return "读取身份信息时出错，请检查 workspace/IDENTITY.md。";
        }
    }

    private async Task<string?> HandleSideQuestionAsync(string? question, string sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(question))
            return "用法：/btw <问题>  — 向 Agent 提一个不影响主会话上下文的旁路问题。";

        if (_modelProvider is null)
            return "旁路问题需要模型 Provider，当前未配置（modelProvider unavailable）。";

        // Load identity and soul for context (best-effort)
        string identity = "";
        string soul = "";
        if (_workspaceService is not null)
        {
            try
            {
                var snapshot = await _workspaceService.GetSnapshotAsync(ct);
                var workspaceDir = Path.Combine(snapshot.RootPath, KodaClawWorkspaceLayout.WorkspaceDirectory);
                var identityPath = Path.Combine(workspaceDir, KodaClawWorkspaceLayout.IdentityFile);
                var soulPath = Path.Combine(workspaceDir, KodaClawWorkspaceLayout.SoulFile);
                if (File.Exists(identityPath)) identity = await File.ReadAllTextAsync(identityPath, ct);
                if (File.Exists(soulPath)) soul = await File.ReadAllTextAsync(soulPath, ct);
            }
            catch
            {
                // Non-critical — proceed with empty identity/soul
            }
        }

        // Read main session history, filter to text-only, then format as a plain-text block
        // injected into the system prompt. This avoids the model mimicking tool-call patterns
        // it sees in conversation history (e.g. channel_send).
        var mainMessages = await LoadMainSessionMessagesAsync(sessionId, ct);
        var contextMessages = FilterAndTrimMessages(mainMessages);
        var historyText = FormatMessagesAsText(contextMessages);
        var systemPrompt = BuildSideQuestionSystemPrompt(identity, soul, historyText);

        var sandboxFactory = _sandboxFactory ?? new Kode.Agent.Sdk.Infrastructure.Sandbox.LocalSandboxFactory();
        var ephemeralStore = new EphemeralAgentStore();
        var ephemeralRegistry = new EphemeralToolRegistry();
        var deps = new AgentDependencies
        {
            Store = ephemeralStore,
            SandboxFactory = sandboxFactory,
            ToolRegistry = ephemeralRegistry,
            ModelProvider = _modelProvider,
        };

        var config = new AgentConfig
        {
            Model = "koda-main",
            SystemPrompt = systemPrompt,
            MaxIterations = 3,
            Tools = [],
        };

        var ephemeralId = Guid.NewGuid().ToString("N");
        try
        {
            await using var agent = await Agent.CreateAsync(ephemeralId, config, deps, ct);
            var result = await agent.RunAsync(question, ct);
            return result.Response ?? "（旁路 Agent 未返回回复）";
        }
        catch (Exception ex)
        {
            return $"旁路问题处理失败：{ex.GetBaseException().Message}";
        }
    }

    /// <summary>
    /// Loads the main session's message history. Returns an empty list on any error.
    /// </summary>
    private async Task<IReadOnlyList<Message>> LoadMainSessionMessagesAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            return await _channelSessionService.GetSessionMessagesAsync(sessionId, ct);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Filters the main session's messages to text-only content (strips tool calls/results
    /// to avoid sending orphaned API message pairs) and trims to the most recent window.
    /// </summary>
    internal static IReadOnlyList<Message> FilterAndTrimMessages(IReadOnlyList<Message> messages)
    {
        const int MaxContextMessages = 40;

        var filtered = new List<Message>(messages.Count);
        foreach (var msg in messages)
        {
            // Skip system messages: the /btw agent provides its own system prompt.
            if (msg.Role == MessageRole.System) continue;

            // Keep only TextContent and ThinkingContent; strip ToolUseContent / ToolResultContent.
            var kept = msg.Content
                .Where(static c => c is TextContent or ThinkingContent)
                .ToList();

            if (kept.Count > 0)
                filtered.Add(msg with { Content = kept });
        }

        // Take the most recent MaxContextMessages messages.
        return filtered.Count > MaxContextMessages
            ? filtered.GetRange(filtered.Count - MaxContextMessages, MaxContextMessages)
            : filtered;
    }

    internal static string FormatMessagesAsText(IReadOnlyList<Message> messages)
    {
        if (messages.Count == 0) return "";

        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            var role = msg.Role == MessageRole.User ? "用户" : "助手";
            foreach (var content in msg.Content)
            {
                if (content is TextContent tc && !string.IsNullOrWhiteSpace(tc.Text))
                    sb.AppendLine($"[{role}] {tc.Text.Trim()}");
            }
        }
        return sb.ToString().Trim();
    }

    private static string BuildSideQuestionSystemPrompt(string identity, string soul, string? historyText = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是一个一次性旁路助手。用户在主会话之外问了一个临时问题，直接用文字回答即可，简洁、直接，不要发散。");

        if (!string.IsNullOrWhiteSpace(historyText))
        {
            sb.AppendLine();
            sb.AppendLine("以下是主会话的部分对话记录，供你了解当前上下文，不要继续执行其中的操作：");
            sb.AppendLine();
            sb.AppendLine(historyText);
        }

        if (!string.IsNullOrWhiteSpace(identity))
        {
            sb.AppendLine();
            sb.AppendLine("--- 身份参考 ---");
            sb.AppendLine(identity.Length > 800 ? identity[..800] + "…" : identity);
        }

        if (!string.IsNullOrWhiteSpace(soul))
        {
            sb.AppendLine();
            sb.AppendLine("--- 行为准则参考 ---");
            sb.AppendLine(soul.Length > 400 ? soul[..400] + "…" : soul);
        }

        return sb.ToString().Trim();
    }

    private static string BuildHelpMessage()
    {
        var sb = new StringBuilder();
        sb.AppendLine("📋 可用命令");
        sb.AppendLine();

        var categories = new[]
        {
            ("session", "会话控制"),
            ("info", "信息查询"),
            ("modifier", "对话修饰"),
            ("meta", "元操作"),
        };

        foreach (var (cat, label) in categories)
        {
            var defs = ChannelCommandRegistry.ByCategory(cat).ToList();
            if (defs.Count == 0) continue;
            sb.AppendLine($"【{label}】");
            foreach (var def in defs)
            {
                var aliases = string.Join(" / ", def.Aliases);
                sb.AppendLine($"  {aliases} — {def.Description}");
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    internal static string FormatSessionStatusMessage(AgentSessionState? state)
    {
        if (state is null)
            return "会话暂时不可用（可能正在初始化或轮转中），请稍后重试。";

        return state.RuntimeState switch
        {
            AgentRuntimeState.Working => FormatWorkingState(state),
            AgentRuntimeState.Paused => FormatPausedState(state),
            AgentRuntimeState.Ready => FormatReadyState(state),
            _ => $"当前状态未知，已执行 {state.StepCount} 步。",
        };
    }

    private static string FormatWorkingState(AgentSessionState state)
    {
        var toolSuffix = state.CurrentToolName is not null
            && state.BreakpointState is BreakpointState.ToolPending
                or BreakpointState.PreTool
                or BreakpointState.ToolExecuting
            ? $"（{state.CurrentToolName}）"
            : string.Empty;

        var bpText = state.BreakpointState switch
        {
            BreakpointState.Ready or BreakpointState.PreModel => "思考中",
            BreakpointState.StreamingModel => "回复中",
            BreakpointState.ToolPending
                or BreakpointState.PreTool
                or BreakpointState.ToolExecuting => $"调用工具{toolSuffix}",
            BreakpointState.AwaitingApproval => "等待审批",
            BreakpointState.PostTool => "整理结果",
            _ => state.BreakpointState.ToString(),
        };

        var sb = new StringBuilder();
        sb.Append($"正在处理中，已执行 {state.StepCount} 步 — 当前阶段：{bpText}");

        if (state.TurnStartedAt.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - state.TurnStartedAt.Value;
            sb.Append($"（已持续 {FormatElapsed(elapsed)}）");
        }

        if (state.MaxIterations > 0)
            sb.Append($"\n迭代进度：{state.IterationCount}/{state.MaxIterations}");

        if (state.PendingQueueCount > 0)
            sb.Append($"\n排队消息：{state.PendingQueueCount} 条待处理");

        return sb.ToString();
    }

    private static string FormatPausedState(AgentSessionState state)
    {
        var toolInfo = state.CurrentToolName is not null ? $"（{state.CurrentToolName}）" : "";
        var sb = new StringBuilder();
        sb.Append($"已暂停，已执行 {state.StepCount} 步（等待审批{toolInfo}）");

        if (state.TurnStartedAt.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - state.TurnStartedAt.Value;
            sb.Append($"，本轮已持续 {FormatElapsed(elapsed)}");
        }

        sb.Append('。');

        if (state.PendingQueueCount > 0)
            sb.Append($"\n排队消息：{state.PendingQueueCount} 条待处理");

        return sb.ToString();
    }

    private static string FormatReadyState(AgentSessionState state)
    {
        var sb = new StringBuilder();
        sb.Append("空闲，随时可以处理新消息");

        if (state.LastActivityAt.HasValue)
        {
            var elapsed = DateTimeOffset.UtcNow - state.LastActivityAt.Value;
            sb.Append($"（上次活跃：{FormatElapsed(elapsed)}前）");
        }

        sb.Append('。');

        if (state.MessageCount > 0)
            sb.Append($"\n本会话共 {state.MessageCount} 条消息");

        return sb.ToString();
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 60)
            return $"{(int)elapsed.TotalSeconds} 秒";
        if (elapsed.TotalMinutes < 60)
            return $"{(int)elapsed.TotalMinutes} 分钟";
        return $"{(int)elapsed.TotalHours} 小时 {elapsed.Minutes} 分钟";
    }
}
