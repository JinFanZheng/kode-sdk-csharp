using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Approvals;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Plugins;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.Settings;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class SandboxRiskApiIntegrationTests
{
    private const string GatewayToken = "test-token";

    [Fact]
    public async Task Sandbox_risk_overview_should_require_token()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);

        var response = await hosted.Client.GetAsync("/api/settings/sandbox-risk");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Sandbox_risk_overview_should_return_default_snapshot()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var payload = await hosted.Client.GetFromJsonAsync<SandboxRiskOverviewResponse>("/api/settings/sandbox-risk");

        payload.Should().NotBeNull();
        payload!.ExecutionProfiles.Should().HaveCount(2);
        payload.ExecutionProfiles.Should().Contain(item => item.Key == "local-boundary" && item.Active);
        payload.ExecutionProfiles.Should().Contain(item => item.Key == "docker-isolation" && !item.Active && item.Supported);
        payload.PluginRisk.TotalCount.Should().Be(0);
        payload.ChannelRisk.TotalThreads.Should().Be(0);
        payload.ApprovalPosture.RequireApprovalForExternalActions.Should().BeFalse();
        payload.OperatorWarnings.Should().Contain(item => item.Contains("Local sandbox", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sandbox_risk_overview_should_aggregate_settings_plugins_and_channels()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var settingsRepository = hosted.Services.GetRequiredService<ISettingsRepository>();
        await settingsRepository.SaveAsync(new KodaClawSettings(
            DefaultLandingRoute: "/models",
            Theme: ThemeMode.Dark,
            RequireApprovalForExternalActions: false,
            NotificationsEnabled: true,
            QuietHoursEnabled: true,
            QuietHoursStartLocalTime: "08:00",
            QuietHoursEndLocalTime: "22:00",
            UpdatedAt: new DateTimeOffset(2026, 3, 19, 1, 0, 0, TimeSpan.Zero)));

        var pluginRepository = hosted.Services.GetRequiredService<IPluginRegistryRepository>();
        var pluginRoot = Path.Combine(
            workspace.Path,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            "plugins",
            "plugin.highrisk");
        Directory.CreateDirectory(pluginRoot);
        await pluginRepository.UpsertAsync(new PluginRecord(
            Id: "plugin.highrisk",
            Manifest: new PluginManifest(
                Id: "plugin.highrisk",
                Name: "High Risk Plugin",
                Version: "0.1.0",
                Types: [PluginType.Tool],
                Runtime: new PluginRuntimeSpec(
                    Transport: PluginTransportKind.Stdio,
                    Command: "dotnet",
                    Args: ["plugin.highrisk.dll"]),
                Permissions: new PluginPermissionSet(
                    Filesystem: ["workspace"],
                    Network: true,
                    Background: true,
                    Channels: ["telegram.send"],
                    Secrets: ["telegram.bot"]),
                Capabilities: new PluginCapabilitySet(Tools: ["echo"])),
            InstallSource: PluginInstallSource.LocalDirectory,
            RootPath: pluginRoot,
            TrustState: PluginTrustState.Signed,
            Enabled: true,
            RuntimeState: PluginRuntimeState.Running,
            DiscoveredAt: new DateTimeOffset(2026, 3, 19, 1, 5, 0, TimeSpan.Zero),
            InstalledAt: new DateTimeOffset(2026, 3, 19, 1, 6, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 3, 19, 1, 7, 0, TimeSpan.Zero),
            TrustEvidence: new PluginTrustEvidence(
                Source: PluginTrustEvidenceSource.SignatureSidecar,
                VerificationState: PluginTrustVerificationState.Verified,
                Summary: "Signature sidecar matched current digests.",
                VerifiedAt: new DateTimeOffset(2026, 3, 19, 1, 6, 30, TimeSpan.Zero),
                ManifestDigestSha256: "manifest-digest",
                PackageDigestSha256: "package-digest",
                Signer: "Fixture Publisher",
                SignatureFilePath: Path.Combine(pluginRoot, "plugin.signature.json"))));

        var accountRepository = hosted.Services.GetRequiredService<IChannelAccountRepository>();
        await accountRepository.UpsertAsync(new ChannelAccount(
            Id: "telegram-main",
            ConnectorKind: ChannelConnectorKind.Telegram,
            DisplayName: "Telegram Main",
            State: ChannelAccountState.Connected,
            CreatedAt: new DateTimeOffset(2026, 3, 19, 1, 10, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 3, 19, 1, 15, 0, TimeSpan.Zero),
            ExternalAccountId: "bot_001",
            CredentialReference: "keychain:channels:telegram-main"));

        var bindingRepository = hosted.Services.GetRequiredService<IThreadBindingRepository>();
        await bindingRepository.UpsertAsync(new ThreadBinding(
            Id: "binding-dm-001",
            ConnectorKind: ChannelConnectorKind.Telegram,
            AccountId: "telegram-main",
            ExternalThreadId: "chat-001",
            ThreadType: ChannelThreadType.DirectMessage,
            SessionId: "session-dm-001",
            SessionKind: SessionKind.ChannelDirectMessage,
            ChannelIdentity: new ChannelIdentity(
                Id: "user-001",
                Username: "alice",
                DisplayName: "Alice"),
            PolicyId: "policy-dm-default",
            DeliveryRuleId: "delivery-dm-default",
            CreatedAt: new DateTimeOffset(2026, 3, 19, 1, 12, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 3, 19, 1, 16, 0, TimeSpan.Zero),
            LastInboundAt: new DateTimeOffset(2026, 3, 19, 1, 16, 30, TimeSpan.Zero),
            LastMessagePreview: "Need approval"));
        await bindingRepository.UpsertAsync(new ThreadBinding(
            Id: "binding-group-001",
            ConnectorKind: ChannelConnectorKind.Telegram,
            AccountId: "telegram-main",
            ExternalThreadId: "chat-002",
            ThreadType: ChannelThreadType.Group,
            SessionId: "session-group-001",
            SessionKind: SessionKind.ChannelGroup,
            ChannelIdentity: new ChannelIdentity(
                Id: "group-001",
                DisplayName: "Ops Room"),
            PolicyId: "policy-group-default",
            DeliveryRuleId: "delivery-group-default",
            CreatedAt: new DateTimeOffset(2026, 3, 19, 1, 13, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 3, 19, 1, 17, 0, TimeSpan.Zero),
            LastInboundAt: new DateTimeOffset(2026, 3, 19, 1, 17, 30, TimeSpan.Zero),
            LastMessagePreview: "Group review required"));

        var approvalRepository = hosted.Services.GetRequiredService<IApprovalRepository>();
        await approvalRepository.UpsertAsync(new Approval(
            Id: "approval-channel-001",
            Kind: ApprovalKind.ChannelDelivery,
            Status: ApprovalStatus.Pending,
            Title: "Approve outbound reply",
            Summary: "Telegram DM needs operator approval.",
            Source: "tests.sandbox-risk",
            RequestedAt: new DateTimeOffset(2026, 3, 19, 1, 18, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 3, 19, 1, 18, 0, TimeSpan.Zero),
            SessionId: "session-dm-001",
            CorrelationId: "corr-sandbox-risk-001"));

        var payload = await hosted.Client.GetFromJsonAsync<SandboxRiskOverviewResponse>("/api/settings/sandbox-risk");

        payload.Should().NotBeNull();
        payload!.ApprovalPosture.RequireApprovalForExternalActions.Should().BeFalse();
        payload.ApprovalPosture.QuietHoursWindow.Should().Be("08:00 - 22:00");

        payload.PluginRisk.TotalCount.Should().Be(1);
        payload.PluginRisk.SignedCount.Should().Be(1);
        payload.PluginRisk.HighRiskCount.Should().Be(1);
        payload.PluginRisk.BroadFilesystemCount.Should().Be(1);
        payload.PluginRisk.SecretAccessCount.Should().Be(1);
        payload.PluginRisk.RiskItems.Should().ContainSingle(item => item.PluginId == "plugin.highrisk");

        payload.ChannelRisk.TotalThreads.Should().Be(2);
        payload.ChannelRisk.OutboundCapableThreadCount.Should().Be(2);
        payload.ChannelRisk.DraftApprovalCount.Should().Be(1);
        payload.ChannelRisk.RequireApprovalCount.Should().Be(1);
        payload.ChannelRisk.PendingApprovalCount.Should().Be(1);
        payload.ChannelRisk.RiskItems.Should().Contain(item =>
            item.BindingId == "binding-dm-001" &&
            item.HasPendingApproval &&
            item.PendingApprovalId == "approval-channel-001");

        payload.OperatorWarnings.Should().Contain(item => item.Contains("high-risk scopes", StringComparison.Ordinal));
        payload.OperatorWarnings.Should().Contain(item => item.Contains("approval preference is disabled", StringComparison.Ordinal));
    }

    private static Task<HostedGateway> StartGatewayAsync(string workspaceRoot)
    {
        return HostedGateway.StartAsync(
            gatewayToken: GatewayToken,
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false,
                rootPath: workspaceRoot),
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                });
            },
            useTestWorkspaceService: false);
    }

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-sandbox-risk-api",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
