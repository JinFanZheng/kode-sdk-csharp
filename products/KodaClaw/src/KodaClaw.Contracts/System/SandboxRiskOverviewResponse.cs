using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Plugins;

namespace KodaClaw.Contracts.System;

public sealed record SandboxRiskOverviewResponse(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<SandboxExecutionProfile> ExecutionProfiles,
    SandboxApprovalPosture ApprovalPosture,
    PluginRiskOverview PluginRisk,
    ChannelRiskOverview ChannelRisk,
    IReadOnlyList<string> OperatorWarnings);

public sealed record SandboxExecutionProfile(
    string Key,
    string DisplayName,
    bool Active,
    bool Supported,
    bool BoundaryEnforced,
    bool BestEffort,
    string Summary,
    string BlastRadius,
    IReadOnlyList<string> Guardrails,
    IReadOnlyList<string> ResidualRisks);

public sealed record SandboxApprovalPosture(
    bool RequireApprovalForExternalActions,
    bool NotificationsEnabled,
    bool QuietHoursEnabled,
    string? QuietHoursWindow,
    bool PersistedPreferenceOnly,
    string Advisory);

public sealed record PluginRiskOverview(
    int TotalCount,
    int SignedCount,
    int TrustedCount,
    int UntrustedCount,
    int HighRiskCount,
    int NetworkEnabledCount,
    int BackgroundCount,
    int BroadFilesystemCount,
    int SecretAccessCount,
    int ChannelAccessCount,
    IReadOnlyList<PluginRiskItem> RiskItems,
    string Advisory);

public sealed record PluginRiskItem(
    string PluginId,
    string DisplayName,
    PluginTrustState TrustState,
    bool Enabled,
    PluginRuntimeState RuntimeState,
    IReadOnlyList<string> RequestedScopes,
    IReadOnlyList<string> HighRiskReasons,
    IReadOnlyList<string> MediumRiskReasons,
    string? TrustEvidenceSummary = null);

public sealed record ChannelRiskOverview(
    int TotalThreads,
    int OutboundCapableThreadCount,
    int AutoSendCount,
    int DraftApprovalCount,
    int RequireApprovalCount,
    int PendingApprovalCount,
    IReadOnlyList<ChannelRiskItem> RiskItems,
    string Advisory);

public sealed record ChannelRiskItem(
    string BindingId,
    string DisplayTitle,
    ChannelConnectorKind ConnectorKind,
    bool SupportsOutbound,
    ChannelThreadType ThreadType,
    ChannelAccountState AccountState,
    DeliveryMode DeliveryMode,
    bool HasPendingApproval,
    string? PendingApprovalId = null);
