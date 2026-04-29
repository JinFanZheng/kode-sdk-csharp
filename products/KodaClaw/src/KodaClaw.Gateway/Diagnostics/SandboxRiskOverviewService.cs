using KodaClaw.Contracts;
using KodaClaw.Contracts.Approvals;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Plugins;
using KodaClaw.Contracts.Settings;
using KodaClaw.Contracts.System;
using KodaClaw.PluginHost.Permissions;

namespace KodaClaw.Gateway;

internal sealed class SandboxRiskOverviewService
{
    private const int OverviewLimit = 200;
    private const int MaxRiskItems = 6;

    private readonly ISettingsRepository _settingsRepository;
    private readonly IPluginRegistryRepository _pluginRegistryRepository;
    private readonly IThreadBindingRepository _threadBindingRepository;
    private readonly IChannelAccountRepository _channelAccountRepository;
    private readonly IApprovalRepository _approvalRepository;

    public SandboxRiskOverviewService(
        ISettingsRepository settingsRepository,
        IPluginRegistryRepository pluginRegistryRepository,
        IThreadBindingRepository threadBindingRepository,
        IChannelAccountRepository channelAccountRepository,
        IApprovalRepository approvalRepository)
    {
        _settingsRepository = settingsRepository;
        _pluginRegistryRepository = pluginRegistryRepository;
        _threadBindingRepository = threadBindingRepository;
        _channelAccountRepository = channelAccountRepository;
        _approvalRepository = approvalRepository;
    }

    public async Task<SandboxRiskOverviewResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsRepository.GetAsync(cancellationToken);
        var plugins = await _pluginRegistryRepository.ListAsync(
            new PluginQuery(Limit: OverviewLimit),
            cancellationToken);
        var bindings = await _threadBindingRepository.ListAsync(
            new ChannelQuery(Limit: OverviewLimit),
            cancellationToken);
        var pendingApprovals = await _approvalRepository.ListAsync(
            new ApprovalQuery(
                Status: ApprovalStatus.Pending,
                Kind: ApprovalKind.ChannelDelivery,
                Limit: OverviewLimit),
            cancellationToken);

        var accountsById = await LoadAccountsByIdAsync(bindings, cancellationToken);
        var pendingApprovalsBySession = pendingApprovals
            .Where(static approval => !string.IsNullOrWhiteSpace(approval.SessionId))
            .GroupBy(static approval => approval.SessionId!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(static approval => approval.UpdatedAt).First(),
                StringComparer.Ordinal);

        var pluginOverview = BuildPluginRiskOverview(plugins);
        var channelOverview = BuildChannelRiskOverview(bindings, accountsById, pendingApprovalsBySession, pendingApprovals.Count);
        var approvalPosture = BuildApprovalPosture(settings);

        return new SandboxRiskOverviewResponse(
            GeneratedAt: DateTimeOffset.UtcNow,
            ExecutionProfiles: BuildExecutionProfiles(),
            ApprovalPosture: approvalPosture,
            PluginRisk: pluginOverview,
            ChannelRisk: channelOverview,
            OperatorWarnings: BuildOperatorWarnings(pluginOverview, channelOverview, approvalPosture));
    }

    private async Task<IReadOnlyDictionary<string, ChannelAccount?>> LoadAccountsByIdAsync(
        IReadOnlyList<ThreadBinding> bindings,
        CancellationToken cancellationToken)
    {
        var accounts = new Dictionary<string, ChannelAccount?>(StringComparer.Ordinal);
        foreach (var accountId in bindings
                     .Select(static binding => binding.AccountId)
                     .Distinct(StringComparer.Ordinal))
        {
            accounts[accountId] = await _channelAccountRepository.GetByIdAsync(accountId, cancellationToken);
        }

        return accounts;
    }

    private static IReadOnlyList<SandboxExecutionProfile> BuildExecutionProfiles()
    {
        return
        [
            new SandboxExecutionProfile(
                Key: "local-boundary",
                DisplayName: "Local sandbox (current)",
                Active: true,
                Supported: true,
                BoundaryEnforced: true,
                BestEffort: true,
                Summary: "KodaClaw currently runs sessions on the host with a per-session working directory boundary.",
                BlastRadius: "Best-effort guardrails only: commands still use the host toolchain, host OS permissions, and writable workspace paths.",
                Guardrails:
                [
                    "Per-session working directory is enforced.",
                    "Boundary enforcement is enabled for main, automation, and channel sessions.",
                    "Plugin trust and channel approval surfaces still add separate gates."
                ],
                ResidualRisks:
                [
                    "Host-installed tools and host credentials remain in scope.",
                    "Mounted workspace files stay writable.",
                    "This is not VM-grade isolation."
                ]),
            new SandboxExecutionProfile(
                Key: "docker-isolation",
                DisplayName: "Docker sandbox (SDK-supported)",
                Active: false,
                Supported: true,
                BoundaryEnforced: true,
                BestEffort: false,
                Summary: "The underlying SDK can move command execution into a dedicated container.",
                BlastRadius: "Stronger command isolation, but mounted working directories and allowlisted paths remain writable unless mounted read-only.",
                Guardrails:
                [
                    "Command execution can be isolated to a container.",
                    "Mounted-path visibility is narrower than host-local execution.",
                    "Network isolation can be configured separately."
                ],
                ResidualRisks:
                [
                    "Not active in the current KodaClaw runtime profile.",
                    "Mounted paths still carry real blast radius.",
                    "Approval and policy gates are still recommended even with container isolation."
                ]),
        ];
    }

    private static SandboxApprovalPosture BuildApprovalPosture(KodaClawSettings settings)
    {
        var quietHoursWindow = settings.QuietHoursEnabled &&
            !string.IsNullOrWhiteSpace(settings.QuietHoursStartLocalTime) &&
            !string.IsNullOrWhiteSpace(settings.QuietHoursEndLocalTime)
            ? $"{settings.QuietHoursStartLocalTime} - {settings.QuietHoursEndLocalTime}"
            : null;

        var advisory = settings.RequireApprovalForExternalActions
            ? "This is a persisted operator preference. Runtime tools, plugins, and channels can still enforce their own approval gates."
            : "External-action approval is disabled as a persisted operator preference. Runtime tools, plugins, and channels may still add their own approval gates.";

        return new SandboxApprovalPosture(
            RequireApprovalForExternalActions: settings.RequireApprovalForExternalActions,
            NotificationsEnabled: settings.NotificationsEnabled,
            QuietHoursEnabled: settings.QuietHoursEnabled,
            QuietHoursWindow: quietHoursWindow,
            PersistedPreferenceOnly: true,
            Advisory: advisory);
    }

    private static PluginRiskOverview BuildPluginRiskOverview(IReadOnlyList<PluginRecord> plugins)
    {
        var items = plugins
            .Select(BuildPluginRiskItem)
            .ToArray();

        var highRiskCount = items.Count(static item => item.HighRiskReasons.Count > 0);
        var advisory = plugins.Count == 0
            ? "No plugins are registered yet, so plugin blast radius is currently dormant."
            : highRiskCount > 0
                ? $"{highRiskCount} plugin(s) currently request high-risk scopes. Review requested permissions together with trust and enablement state."
                : "Registered plugins currently stay inside low/medium-risk scopes, but plugin permissions still widen the operator blast radius beyond the core product.";

        return new PluginRiskOverview(
            TotalCount: plugins.Count,
            SignedCount: plugins.Count(static plugin => plugin.TrustState == PluginTrustState.Signed),
            TrustedCount: plugins.Count(static plugin => plugin.TrustState == PluginTrustState.Trusted),
            UntrustedCount: plugins.Count(static plugin => plugin.TrustState == PluginTrustState.Untrusted),
            HighRiskCount: highRiskCount,
            NetworkEnabledCount: plugins.Count(static plugin => plugin.Manifest.Permissions.Network),
            BackgroundCount: plugins.Count(static plugin => plugin.Manifest.Permissions.Background),
            BroadFilesystemCount: plugins.Count(HasBroadFilesystemScope),
            SecretAccessCount: plugins.Count(static plugin => plugin.Manifest.Permissions.Secrets is { Count: > 0 }),
            ChannelAccessCount: plugins.Count(static plugin => plugin.Manifest.Permissions.Channels is { Count: > 0 }),
            RiskItems: items
                .OrderByDescending(static item => item.HighRiskReasons.Count > 0)
                .ThenByDescending(static item => item.Enabled)
                .ThenBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Take(MaxRiskItems)
                .ToArray(),
            Advisory: advisory);
    }

    private static PluginRiskItem BuildPluginRiskItem(PluginRecord plugin)
    {
        var normalizedPermissions = PluginPermissionPolicy.Normalize(plugin.Manifest.Permissions);
        var risk = PluginPermissionPolicy.SummarizeRisk(normalizedPermissions);

        return new PluginRiskItem(
            PluginId: plugin.Id,
            DisplayName: plugin.Manifest.Name,
            TrustState: plugin.TrustState,
            Enabled: plugin.Enabled,
            RuntimeState: plugin.RuntimeState,
            RequestedScopes: BuildRequestedScopes(normalizedPermissions),
            HighRiskReasons: risk.HighRiskReasons,
            MediumRiskReasons: risk.MediumRiskReasons,
            TrustEvidenceSummary: plugin.TrustEvidence?.Summary);
    }

    private static IReadOnlyList<string> BuildRequestedScopes(PluginPermissionSet permissions)
    {
        var scopes = new List<string>();

        if (permissions.Network)
        {
            scopes.Add("network outbound");
        }

        if (permissions.Background)
        {
            scopes.Add("background execution");
        }

        if (permissions.Notifications)
        {
            scopes.Add("desktop notifications");
        }

        if (permissions.Filesystem is { Count: > 0 })
        {
            scopes.Add($"filesystem: {string.Join(", ", permissions.Filesystem)}");
        }

        if (permissions.Channels is { Count: > 0 })
        {
            scopes.Add($"channels: {string.Join(", ", permissions.Channels)}");
        }

        if (permissions.Secrets is { Count: > 0 })
        {
            scopes.Add($"secrets: {string.Join(", ", permissions.Secrets)}");
        }

        if (permissions.UiPanels is { Count: > 0 })
        {
            scopes.Add($"ui panels: {string.Join(", ", permissions.UiPanels)}");
        }

        return scopes;
    }

    private static bool HasBroadFilesystemScope(PluginRecord plugin)
    {
        return plugin.Manifest.Permissions.Filesystem?.Any(IsBroadFilesystemScope) == true;
    }

    private static bool IsBroadFilesystemScope(string path)
    {
        return string.Equals(path, "workspace", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, ".", StringComparison.Ordinal)
            || string.Equals(path, "*", StringComparison.Ordinal);
    }

    private static ChannelRiskOverview BuildChannelRiskOverview(
        IReadOnlyList<ThreadBinding> bindings,
        IReadOnlyDictionary<string, ChannelAccount?> accountsById,
        IReadOnlyDictionary<string, Approval> pendingApprovalsBySession,
        int pendingApprovalCount)
    {
        var items = bindings
            .Select(binding =>
            {
                accountsById.TryGetValue(binding.AccountId, out var account);
                pendingApprovalsBySession.TryGetValue(binding.SessionId, out var approval);
                return BuildChannelRiskItem(binding, account?.State ?? ChannelAccountState.Disconnected, approval);
            })
            .ToArray();

        var autoSendCount = items.Count(static item => item.DeliveryMode == DeliveryMode.AutoSend);
        var draftApprovalCount = items.Count(static item => item.DeliveryMode == DeliveryMode.DraftApproval);
        var requireApprovalCount = items.Count(static item => item.DeliveryMode == DeliveryMode.RequireApproval);
        var outboundCapableThreadCount = items.Count(static item => item.SupportsOutbound);

        var advisory = bindings.Count == 0
            ? "No channel threads are active yet, so outbound channel risk is currently dormant."
            : autoSendCount > 0
                ? $"{autoSendCount} thread(s) can auto-send without a review stop. Treat them as the widest outbound blast radius."
                : "Current defaults keep direct messages in draft approval and groups in explicit approval. Pending approvals still represent real outbound intent.";

        return new ChannelRiskOverview(
            TotalThreads: bindings.Count,
            OutboundCapableThreadCount: outboundCapableThreadCount,
            AutoSendCount: autoSendCount,
            DraftApprovalCount: draftApprovalCount,
            RequireApprovalCount: requireApprovalCount,
            PendingApprovalCount: pendingApprovalCount,
            RiskItems: items
                .OrderByDescending(static item => item.DeliveryMode == DeliveryMode.AutoSend)
                .ThenByDescending(static item => item.HasPendingApproval)
                .ThenByDescending(static item => item.SupportsOutbound)
                .ThenBy(static item => item.DisplayTitle, StringComparer.OrdinalIgnoreCase)
                .Take(MaxRiskItems)
                .ToArray(),
            Advisory: advisory);
    }

    private static ChannelRiskItem BuildChannelRiskItem(
        ThreadBinding binding,
        ChannelAccountState accountState,
        Approval? pendingApproval)
    {
        return new ChannelRiskItem(
            BindingId: binding.Id,
            DisplayTitle: ResolveDisplayTitle(binding),
            ConnectorKind: binding.ConnectorKind,
            SupportsOutbound: SupportsOutbound(binding.ConnectorKind),
            ThreadType: binding.ThreadType,
            AccountState: accountState,
            DeliveryMode: ResolveDeliveryMode(binding.ThreadType),
            HasPendingApproval: pendingApproval is not null,
            PendingApprovalId: pendingApproval?.Id);
    }

    private static DeliveryMode ResolveDeliveryMode(ChannelThreadType threadType)
    {
        return threadType switch
        {
            ChannelThreadType.DirectMessage => DeliveryMode.DraftApproval,
            ChannelThreadType.Group => DeliveryMode.RequireApproval,
            _ => throw new ArgumentOutOfRangeException(nameof(threadType), threadType, null),
        };
    }

    private static string ResolveDisplayTitle(ThreadBinding binding)
    {
        return binding.ChannelIdentity.DisplayName
            ?? binding.ChannelIdentity.Username
            ?? binding.ExternalThreadId;
    }

    private static bool SupportsOutbound(ChannelConnectorKind connectorKind)
    {
        return connectorKind is ChannelConnectorKind.Telegram or ChannelConnectorKind.Feishu or ChannelConnectorKind.DingTalk;
    }

    private static IReadOnlyList<string> BuildOperatorWarnings(
        PluginRiskOverview pluginRisk,
        ChannelRiskOverview channelRisk,
        SandboxApprovalPosture approvalPosture)
    {
        var warnings = new List<string>
        {
            "Local sandbox is active today. Boundary enforcement is enabled, but it remains a best-effort host guardrail.",
            "Docker isolation is supported by the SDK, but it is not the active KodaClaw runtime profile."
        };

        if (!approvalPosture.RequireApprovalForExternalActions)
        {
            warnings.Add("The persisted external-action approval preference is disabled. Treat plugin and channel actions as a wider operator blast radius.");
        }

        if (pluginRisk.HighRiskCount > 0)
        {
            warnings.Add($"{pluginRisk.HighRiskCount} plugin(s) request high-risk scopes such as network, secrets, background execution, or broad filesystem access.");
        }

        if (channelRisk.AutoSendCount > 0)
        {
            warnings.Add($"{channelRisk.AutoSendCount} channel thread(s) can auto-send without a review stop.");
        }

        if (channelRisk.PendingApprovalCount > 0)
        {
            warnings.Add($"{channelRisk.PendingApprovalCount} outbound channel approval(s) are currently pending.");
        }

        return warnings;
    }
}
