using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Plugins;
using KodaClaw.Contracts.System;
using Xunit;

namespace KodaClaw.ContractTests.Workspace;

public sealed class SandboxRiskContractsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Sandbox_risk_overview_should_json_round_trip()
    {
        var payload = new SandboxRiskOverviewResponse(
            GeneratedAt: new DateTimeOffset(2026, 3, 19, 3, 0, 0, TimeSpan.Zero),
            ExecutionProfiles:
            [
                new SandboxExecutionProfile(
                    Key: "local-boundary",
                    DisplayName: "Local sandbox (current)",
                    Active: true,
                    Supported: true,
                    BoundaryEnforced: true,
                    BestEffort: true,
                    Summary: "Runs on the host with boundary enforcement.",
                    BlastRadius: "Host tools and writable workspace files remain in scope.",
                    Guardrails: ["Per-session working directory", "Boundary enforcement"],
                    ResidualRisks: ["Host toolchain", "Writable mounted paths"]),
                new SandboxExecutionProfile(
                    Key: "docker-isolation",
                    DisplayName: "Docker sandbox (SDK-supported)",
                    Active: false,
                    Supported: true,
                    BoundaryEnforced: true,
                    BestEffort: false,
                    Summary: "Can move command execution into a container.",
                    BlastRadius: "Mounted paths are still writable unless mounted read-only.",
                    Guardrails: ["Container isolation", "Optional network isolation"],
                    ResidualRisks: ["Not active by default"]),
            ],
            ApprovalPosture: new SandboxApprovalPosture(
                RequireApprovalForExternalActions: true,
                NotificationsEnabled: true,
                QuietHoursEnabled: true,
                QuietHoursWindow: "08:00 - 22:00",
                PersistedPreferenceOnly: true,
                Advisory: "Runtime-specific surfaces can still add their own approval gates."),
            PluginRisk: new PluginRiskOverview(
                TotalCount: 2,
                SignedCount: 1,
                TrustedCount: 1,
                UntrustedCount: 0,
                HighRiskCount: 1,
                NetworkEnabledCount: 1,
                BackgroundCount: 1,
                BroadFilesystemCount: 1,
                SecretAccessCount: 1,
                ChannelAccessCount: 1,
                RiskItems:
                [
                    new PluginRiskItem(
                        PluginId: "plugin.fixture",
                        DisplayName: "Fixture Plugin",
                        TrustState: PluginTrustState.Signed,
                        Enabled: true,
                        RuntimeState: PluginRuntimeState.Running,
                        RequestedScopes: ["network outbound", "filesystem: workspace"],
                        HighRiskReasons: ["Requests network access."],
                        MediumRiskReasons: ["Requests 1 channel scope(s)."],
                        TrustEvidenceSummary: "Signature sidecar matched current digests.")
                ],
                Advisory: "Review high-risk plugins together with trust and enablement state."),
            ChannelRisk: new ChannelRiskOverview(
                TotalThreads: 2,
                OutboundCapableThreadCount: 2,
                AutoSendCount: 0,
                DraftApprovalCount: 1,
                RequireApprovalCount: 1,
                PendingApprovalCount: 1,
                RiskItems:
                [
                    new ChannelRiskItem(
                        BindingId: "binding-001",
                        DisplayTitle: "Alice",
                        ConnectorKind: ChannelConnectorKind.Telegram,
                        SupportsOutbound: true,
                        ThreadType: ChannelThreadType.DirectMessage,
                        AccountState: ChannelAccountState.Connected,
                        DeliveryMode: DeliveryMode.DraftApproval,
                        HasPendingApproval: true,
                        PendingApprovalId: "approval-channel-001")
                ],
                Advisory: "Current defaults keep direct messages in draft approval and groups in explicit approval."),
            OperatorWarnings:
            [
                "Local sandbox is active today.",
                "Docker isolation is supported by the SDK, but it is not the active runtime profile."
            ]);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<SandboxRiskOverviewResponse>(json, JsonOptions);

        json.Should().Contain("\"executionProfiles\"");
        json.Should().Contain("\"approvalPosture\"");
        json.Should().Contain("\"pluginRisk\"");
        json.Should().Contain("\"channelRisk\"");
        roundTrip.Should().NotBeNull();
        roundTrip!.ExecutionProfiles.Should().HaveCount(2);
        roundTrip.ExecutionProfiles[0].BestEffort.Should().BeTrue();
        roundTrip.ApprovalPosture.PersistedPreferenceOnly.Should().BeTrue();
        roundTrip.PluginRisk.RiskItems.Should().ContainSingle().Which.TrustState.Should().Be(PluginTrustState.Signed);
        roundTrip.ChannelRisk.RiskItems.Should().ContainSingle().Which.PendingApprovalId.Should().Be("approval-channel-001");
        roundTrip.OperatorWarnings.Should().Contain(item => item.Contains("Docker isolation", StringComparison.Ordinal));
    }
}
