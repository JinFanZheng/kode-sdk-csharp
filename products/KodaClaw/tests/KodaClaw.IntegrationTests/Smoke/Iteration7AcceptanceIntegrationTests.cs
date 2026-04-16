using System.Collections.Generic;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KodaClaw.Automation;
using KodaClaw.ChannelHub;
using KodaClaw.Contracts;
using KodaClaw.ControlPlane;
using KodaClaw.IntegrationTests.Gateway;
using KodaClaw.ModelHub;
using KodaClaw.PluginHost;
using KodaClaw.Storage.Json;
using KodaClaw.Workspace;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Store.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace KodaClaw.IntegrationTests.Smoke;

public sealed class Iteration7AcceptanceIntegrationTests
{
    private const string SourceGatewayToken = "hardening-secret-token";
    private const string TargetGatewayToken = "hardening-target-token";
    private const string Iteration7FallbackEnvironmentVariable = "KODACLAW_ITERATION7_MODEL_FALLBACK_KEY";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Iteration_7_acceptance_should_cover_migration_repair_risk_update_bundle_and_backup_restore()
    {
        using var sourceWorkspace = new TempWorkspaceRoot("kodaclaw-iteration7-source");

        var gatewayTokenSecretRef = new SecretRef("memory", "gateway", "acceptance-token");
        var modelSecretRef = new SecretRef("memory", "models", "primary");
        var channelSecretRef = new SecretRef("memory", "channels", "telegram-main");
        var pluginEnvironmentSecretRef = new SecretRef("memory", "plugins", "fixture-env");
        var missingChannelSecretRef = new SecretRef("memory", "channels", "telegram-missing");
        var missingPluginHeaderSecretRef = new SecretRef("memory", "plugins", "fixture-header-missing");

        var sourceSecretStore = new FakeSecretStore(new Dictionary<string, string?>
        {
            [gatewayTokenSecretRef.ToReferenceString()] = SourceGatewayToken,
            [modelSecretRef.ToReferenceString()] = "model-secret",
            [channelSecretRef.ToReferenceString()] = "telegram-secret",
            [pluginEnvironmentSecretRef.ToReferenceString()] = "plugin-secret",
        });

        Environment.SetEnvironmentVariable(Iteration7FallbackEnvironmentVariable, "model-fallback-secret");

        try
        {
            await SeedSourceWorkspaceAsync(
                sourceWorkspace.Path,
                modelSecretRef,
                channelSecretRef,
                pluginEnvironmentSecretRef,
                missingChannelSecretRef,
                missingPluginHeaderSecretRef);

            var manifestPath = await WriteFixtureManifestAsync(sourceWorkspace.Path);

            await using var sourceHosted = await StartSourceGatewayAsync(
                sourceWorkspace.Path,
                manifestPath,
                sourceSecretStore,
                gatewayTokenSecretRef);
            sourceHosted.Client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", SourceGatewayToken);

            var startupRepair = await sourceHosted.Client.GetFromJsonAsync<StartupRepairReportResponse>(
                "/api/system/startup-repair-report");
            startupRepair.Should().NotBeNull();
            startupRepair!.Checklist.Items.Should().Contain(item => item.Id == "approval-canceled:approval-runtime-001");
            startupRepair.Checklist.Items.Should().Contain(item => item.Id == "automation-run-failed:run-stale-001");
            startupRepair.Checklist.Items.Should().Contain(item => item.Id == "plugin-runtime-recovered:plugin.fixture.hardening");

            var workspaceService = sourceHosted.Services.GetRequiredService<IWorkspaceService>();
            var repairedAppConfig = await workspaceService.LoadAppConfigAsync();
            repairedAppConfig.ActiveMainSessionId.Should().BeNull();

            var migrationReport = await sourceHosted.Client.GetFromJsonAsync<SecretMigrationReport>(
                "/api/system/secret-migration-report");
            migrationReport.Should().NotBeNull();
            migrationReport!.Summary.TotalCount.Should().Be(10);
            migrationReport.Summary.MigratedCount.Should().Be(4);
            migrationReport.Summary.LegacyFallbackCount.Should().Be(4);
            migrationReport.Summary.MissingCount.Should().Be(2);
            File.Exists(Path.Combine(
                sourceWorkspace.Path,
                KodaClawWorkspaceLayout.ConfigDirectory,
                KodaClawWorkspaceLayout.SecretMigrationReportFile)).Should().BeTrue();

            var riskOverview = await sourceHosted.Client.GetFromJsonAsync<SandboxRiskOverviewResponse>(
                "/api/settings/sandbox-risk");
            riskOverview.Should().NotBeNull();
            riskOverview!.PluginRisk.TotalCount.Should().Be(1);
            riskOverview.PluginRisk.SignedCount.Should().Be(1);
            riskOverview.PluginRisk.RiskItems.Should().Contain(item =>
                item.PluginId == "plugin.fixture.hardening" &&
                item.TrustState == PluginTrustState.Signed);
            riskOverview.ChannelRisk.TotalThreads.Should().Be(1);
            riskOverview.ChannelRisk.DraftApprovalCount.Should().Be(1);
            riskOverview.ChannelRisk.RiskItems.Should().Contain(item => item.BindingId == "binding-dm-001");

            var updateCheckResponse = await sourceHosted.Client.PostAsJsonAsync(
                "/api/system/update-check",
                new UpdateCheckRequest(
                    DesktopCurrentVersion: "0.1.0",
                    DesktopReleaseChannel: UpdateReleaseChannel.Stable));
            updateCheckResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var updateState = await updateCheckResponse.Content.ReadFromJsonAsync<UpdateStateResponse>();
            updateState.Should().NotBeNull();
            updateState!.Components.Should().HaveCount(2);
            File.Exists(updateState.ArtifactPath).Should().BeTrue();

            var diagnosticsService = sourceHosted.Services.GetRequiredService<IDiagnosticsService>();
            diagnosticsService.Record(new DiagnosticEvent(
                Id: "evt-hardening-001",
                Source: "gateway.hardening",
                EventType: "gateway.hardening.acceptance",
                Level: "warning",
                Message: "authorization=Bearer hardening-inline-secret",
                Timestamp: new DateTimeOffset(2026, 3, 19, 16, 5, 0, TimeSpan.Zero),
                SessionId: "main-awaiting-001",
                Attributes: new Dictionary<string, string?>
                {
                    ["apiKey"] = "sk-hardening-secret",
                    ["phase"] = "acceptance",
                }));

            var bundleResponse = await sourceHosted.Client.PostAsJsonAsync(
                "/api/diagnostics/bundle-export",
                new DiagnosticBundleExportRequest(
                    SessionId: "main-awaiting-001",
                    TimelineLimit: 120,
                    DesktopContext: new DiagnosticBundleDesktopContext(
                        DesktopMode: true,
                        Platform: "darwin",
                        AppVersion: "0.1.0",
                        ReleaseChannel: UpdateReleaseChannel.Stable,
                        GatewayLifecycleMode: "ManagedChild")));
            bundleResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var bundlePayload = await bundleResponse.Content.ReadFromJsonAsync<DiagnosticBundleExportResponse>();
            bundlePayload.Should().NotBeNull();
            File.Exists(bundlePayload!.BundlePath).Should().BeTrue();

            using var extractedBundle = new TempWorkspaceRoot("kodaclaw-iteration7-bundle");
            ZipFile.ExtractToDirectory(bundlePayload.BundlePath, extractedBundle.Path);

            File.Exists(Path.Combine(extractedBundle.Path, "manifest.json")).Should().BeTrue();
            File.Exists(Path.Combine(extractedBundle.Path, "redaction-summary.json")).Should().BeTrue();
            File.Exists(Path.Combine(
                extractedBundle.Path,
                "snapshot",
                "evidence",
                KodaClawWorkspaceLayout.SecretMigrationReportFile)).Should().BeTrue();
            File.Exists(Path.Combine(
                extractedBundle.Path,
                "snapshot",
                "evidence",
                KodaClawWorkspaceLayout.StartupRepairReportFile)).Should().BeTrue();
            File.Exists(Path.Combine(
                extractedBundle.Path,
                "snapshot",
                "evidence",
                KodaClawWorkspaceLayout.UpdateStateFile)).Should().BeTrue();
            File.Exists(Path.Combine(
                extractedBundle.Path,
                "snapshot",
                "sessions",
                "main-awaiting-001",
                "meta.json")).Should().BeTrue();

            var timeline = await ReadJsonAsync<DiagnosticsQueryResponse>(Path.Combine(
                extractedBundle.Path,
                "snapshot",
                "diagnostics",
                "timeline.json"));
            timeline.Should().NotBeNull();
            timeline!.Events.Should().Contain(item =>
                item.EventType == "gateway.hardening.acceptance" &&
                item.Message.Contains("[REDACTED]", StringComparison.Ordinal));
            timeline.Events.Should().Contain(item =>
                item.Attributes != null &&
                item.Attributes.ContainsKey("apiKey") &&
                item.Attributes["apiKey"] == "[REDACTED]");

            var backupExport = await sourceHosted.Client.PostAsJsonAsync(
                "/api/system/backup-export",
                new BackupExportRequest());
            backupExport.StatusCode.Should().Be(HttpStatusCode.OK);
            var backupPayload = await backupExport.Content.ReadFromJsonAsync<BackupExportResponse>();
            backupPayload.Should().NotBeNull();
            File.Exists(backupPayload!.ArchivePath).Should().BeTrue();

            using var targetWorkspace = new TempWorkspaceRoot("kodaclaw-iteration7-target");
            var targetSecretStore = new FakeSecretStore(new Dictionary<string, string?>
            {
                [modelSecretRef.ToReferenceString()] = "model-secret",
            });

            await using var targetHosted = await HostedGateway.StartAsync(
                gatewayToken: TargetGatewayToken,
                workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                    requiresBootstrap: false,
                    rootPath: targetWorkspace.Path),
                configureServices: services =>
                {
                    services.AddSingleton<ISecretStore>(targetSecretStore);
                    // Replace with no-op to prevent HeartbeatFileWatcherHostedService from
                    // creating automations that make the workspace non-pristine for import.
                    services.Replace(ServiceDescriptor.Singleton<IHeartbeatSyncService>(
                        _ => new NoOpHeartbeatSyncService()));
                },
                configureConfiguration: configuration =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["KODACLAW_WORKSPACE_ROOT"] = targetWorkspace.Path,
                    });
                },
                useTestWorkspaceService: false);
            targetHosted.Client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", TargetGatewayToken);

            var preflight = await targetHosted.Client.PostAsJsonAsync(
                "/api/system/backup-import/preflight",
                new BackupImportPreflightRequest(backupPayload.ArchivePath));
            preflight.StatusCode.Should().Be(HttpStatusCode.OK);
            var preflightPayload = await preflight.Content.ReadFromJsonAsync<BackupImportPreflightResponse>();
            preflightPayload.Should().NotBeNull();
            preflightPayload!.CanImport.Should().BeTrue();
            preflightPayload.Checklist.Items.Should().Contain(item =>
                item.Id.StartsWith("secret-ref-missing:", StringComparison.Ordinal));

            var importResponse = await targetHosted.Client.PostAsJsonAsync(
                "/api/system/backup-import",
                new BackupImportRequest(backupPayload.ArchivePath));
            importResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var importPayload = await importResponse.Content.ReadFromJsonAsync<BackupImportResponse>();
            importPayload.Should().NotBeNull();
            File.Exists(importPayload!.RepairReportPath).Should().BeTrue();
            importPayload.Checklist.Items.Should().Contain(item =>
                item.Id.StartsWith("secret-ref-missing:", StringComparison.Ordinal));
            importPayload.RestoredPaths.Should().Contain("config/app.json");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Iteration7FallbackEnvironmentVariable, null);
        }
    }

    private static async Task<HostedGateway> StartSourceGatewayAsync(
        string workspaceRoot,
        string manifestPath,
        ISecretStore secretStore,
        SecretRef gatewayTokenSecretRef)
    {
        return await HostedGateway.StartAsync(
            gatewayToken: string.Empty,
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false,
                rootPath: workspaceRoot),
            configureServices: services =>
            {
                services.AddSingleton<ISecretStore>(secretStore);
                // Replace with no-op to prevent HeartbeatFileWatcherHostedService from
                // creating automations that inflate the seeded workspace state.
                services.Replace(ServiceDescriptor.Singleton<IHeartbeatSyncService>(
                    _ => new NoOpHeartbeatSyncService()));
            },
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                    ["KODACLAW_GATEWAY_TOKEN_SECRET_REF"] = gatewayTokenSecretRef.ToReferenceString(),
                    ["KODACLAW_STARTUP_REPAIR_ENABLED"] = "true",
                    ["KODACLAW_UPDATE_MANIFEST_PATH"] = manifestPath,
                    ["KODACLAW_UPDATE_RELEASE_CHANNEL"] = "Stable",
                    ["Runtime:OpenAIApiKey"] = "runtime-openai-legacy",
                });
            },
            useTestWorkspaceService: false);
    }

    private static async Task SeedSourceWorkspaceAsync(
        string workspaceRoot,
        SecretRef modelSecretRef,
        SecretRef channelSecretRef,
        SecretRef pluginEnvironmentSecretRef,
        SecretRef missingChannelSecretRef,
        SecretRef missingPluginHeaderSecretRef)
    {
        var services = new ServiceCollection();
        services.AddKodaClawControlPlane();
        services.AddKodaClawWorkspace(options => options.RootPath = workspaceRoot);
        services.AddKodaClawJsonStore(workspaceRoot);
        services.AddKodaClawAutomation(options =>
        {
            options.Enabled = false;
            options.FailureRetryDelay = TimeSpan.FromMinutes(15);
        });
        services.AddKodaClawChannelHub();
        services.AddModelRegistry();
        services.AddKodaClawPluginHost();

        await using var provider = services.BuildServiceProvider();

        var workspace = provider.GetRequiredService<IWorkspaceService>();
        await workspace.EnsureInitializedAsync();
        await workspace.SaveAppConfigAsync(new WorkspaceAppConfig
        {
            WorkspaceVersion = KodaClawWorkspaceLayout.CurrentWorkspaceVersion,
            BootstrapCompleted = true,
            ActiveMainSessionId = "main-awaiting-001",
        });

        var settingsRepository = provider.GetRequiredService<ISettingsRepository>();
        var now = new DateTimeOffset(2026, 3, 19, 15, 0, 0, TimeSpan.Zero);
        await settingsRepository.SaveAsync(new KodaClawSettings(
            DefaultLandingRoute: "/sessions",
            Theme: ThemeMode.System,
            RequireApprovalForExternalActions: true,
            NotificationsEnabled: true,
            QuietHoursEnabled: false,
            QuietHoursStartLocalTime: null,
            QuietHoursEndLocalTime: null,
            UpdatedAt: now));

        var modelRepository = provider.GetRequiredService<IProviderAccountRepository>();
        await modelRepository.AddAccountAsync(new ProviderAccount(
            Id:                        "account-primary",
            DisplayName:               "Primary account",
            ProviderKind:              ModelProviderKind.OpenAICompatible,
            BaseUrl:                   "https://proxy.example.com",
            ApiKeySecretRef:           modelSecretRef.ToReferenceString(),
            ApiKeyEnvironmentVariable: null,
            AccessMode:                "api",
            Enabled:                   true,
            CreatedAt:                 now,
            UpdatedAt:                 now));
        await modelRepository.AddModelAsync(new AccountModel(
            Id:                  "model-primary",
            AccountId:           "account-primary",
            DisplayName:         "Primary model",
            ModelId:             "o3",
            Capabilities:        ModelCapabilitySet.Text,
            IsDefaultForAccount: true,
            IsGlobalDefault:     true,
            Enabled:             true,
            CreatedAt:           now,
            UpdatedAt:           now));
        await modelRepository.AddAccountAsync(new ProviderAccount(
            Id:                        "account-fallback",
            DisplayName:               "Fallback account",
            ProviderKind:              ModelProviderKind.OpenAICompatible,
            BaseUrl:                   "https://proxy.example.com",
            ApiKeySecretRef:           null,
            ApiKeyEnvironmentVariable: Iteration7FallbackEnvironmentVariable,
            AccessMode:                "api",
            Enabled:                   true,
            CreatedAt:                 now.AddMinutes(1),
            UpdatedAt:                 now.AddMinutes(1)));
        await modelRepository.AddModelAsync(new AccountModel(
            Id:                  "model-fallback",
            AccountId:           "account-fallback",
            DisplayName:         "Fallback model",
            ModelId:             "o3-mini",
            Capabilities:        ModelCapabilitySet.Text,
            IsDefaultForAccount: true,
            IsGlobalDefault:     false,
            Enabled:             true,
            CreatedAt:           now.AddMinutes(1),
            UpdatedAt:           now.AddMinutes(1)));

        var accountRepository = provider.GetRequiredService<IChannelAccountRepository>();
        await accountRepository.UpsertAsync(new ChannelAccount(
            Id: "telegram-main",
            ConnectorKind: ChannelConnectorKind.Telegram,
            DisplayName: "Telegram Main",
            State: ChannelAccountState.Connected,
            CreatedAt: now,
            UpdatedAt: now,
            CredentialReference: channelSecretRef.ToReferenceString()));
        await accountRepository.UpsertAsync(new ChannelAccount(
            Id: "webhook-main",
            ConnectorKind: ChannelConnectorKind.GenericWebhook,
            DisplayName: "Webhook Main",
            State: ChannelAccountState.Connected,
            CreatedAt: now.AddMinutes(1),
            UpdatedAt: now.AddMinutes(1),
            ConfigurationJson: """{"sharedSecret":"webhook-inline-secret","defaultThreadType":"Group"}"""));
        await accountRepository.UpsertAsync(new ChannelAccount(
            Id: "telegram-missing",
            ConnectorKind: ChannelConnectorKind.Telegram,
            DisplayName: "Telegram Missing",
            State: ChannelAccountState.Disconnected,
            CreatedAt: now.AddMinutes(2),
            UpdatedAt: now.AddMinutes(2),
            CredentialReference: missingChannelSecretRef.ToReferenceString()));

        var bindingRepository = provider.GetRequiredService<IThreadBindingRepository>();
        await bindingRepository.UpsertAsync(new ThreadBinding(
            Id: "binding-dm-001",
            ConnectorKind: ChannelConnectorKind.Telegram,
            AccountId: "telegram-main",
            ExternalThreadId: "thread-001",
            ThreadType: ChannelThreadType.DirectMessage,
            SessionId: "channel-dm-binding-001",
            SessionKind: SessionKind.ChannelDirectMessage,
            ChannelIdentity: new ChannelIdentity(
                Id: "user-001",
                Username: "alice",
                DisplayName: "Alice"),
            PolicyId: "policy-dm",
            DeliveryRuleId: "delivery-draft",
            CreatedAt: now,
            UpdatedAt: now,
            LastInboundAt: now,
            LastOutboundAt: null,
            LastMessagePreview: "Need approval"));

        var pluginRepository = provider.GetRequiredService<IPluginRegistryRepository>();
        var pluginRootPath = Path.Combine(workspaceRoot, "workspace", "plugins", "plugin.fixture.hardening");
        Directory.CreateDirectory(pluginRootPath);
        await pluginRepository.UpsertAsync(new PluginRecord(
            Id: "plugin.fixture.hardening",
            Manifest: new PluginManifest(
                Id: "plugin.fixture.hardening",
                Name: "Fixture Hardening Plugin",
                Version: "0.1.0",
                Types: [PluginType.Tool],
                Runtime: new PluginRuntimeSpec(
                    Transport: PluginTransportKind.Stdio,
                    Command: "dotnet",
                    Args: ["fixture.dll"],
                    Headers: new Dictionary<string, string>
                    {
                        ["Authorization"] = "Bearer legacy-plugin-header",
                    },
                    EnvironmentReferences: new Dictionary<string, string>
                    {
                        ["PLUGIN_API_TOKEN"] = pluginEnvironmentSecretRef.ToReferenceString(),
                    },
                    HeaderReferences: new Dictionary<string, string>
                    {
                        ["X-API-Key"] = missingPluginHeaderSecretRef.ToReferenceString(),
                    }),
                Permissions: new PluginPermissionSet(
                    Filesystem: ["workspace"],
                    Network: true,
                    Background: true,
                    Channels: ["telegram"],
                    Secrets: ["gateway-token"]),
                Capabilities: new PluginCapabilitySet(Tools: ["echo"])),
            InstallSource: PluginInstallSource.LocalDirectory,
            RootPath: pluginRootPath,
            TrustState: PluginTrustState.Signed,
            Enabled: true,
            RuntimeState: PluginRuntimeState.Running,
            DiscoveredAt: now,
            InstalledAt: now,
            UpdatedAt: now,
            TrustEvidence: new PluginTrustEvidence(
                Source: PluginTrustEvidenceSource.SignatureSidecar,
                VerificationState: PluginTrustVerificationState.Verified,
                Summary: "Fixture signature sidecar verified.",
                VerifiedAt: now,
                ManifestDigestSha256: "abc123",
                PackageDigestSha256: "def456",
                Signer: "Fixture Publisher",
                SignatureFilePath: Path.Combine(pluginRootPath, "plugin.signature.json"))));

        var approvals = provider.GetRequiredService<IApprovalRepository>();
        var inbox = provider.GetRequiredService<IInboxRepository>();
        await approvals.UpsertAsync(new Approval(
            Id: "approval-runtime-001",
            Kind: ApprovalKind.ExternalAction,
            Status: ApprovalStatus.Pending,
            Title: "Dangerous tool",
            Summary: "Runtime approval still pending.",
            Source: "runtime.main_session.approval",
            RequestedAt: now,
            UpdatedAt: now,
            SessionId: "main-awaiting-001",
            CorrelationId: "corr-runtime-001",
            InboxItemId: "inbox-runtime-001",
            PayloadJson: "{\"callId\":\"call-runtime-001\"}"));
        await inbox.UpsertAsync(new InboxItem(
            Id: "inbox-runtime-001",
            Kind: InboxItemKind.Approval,
            Status: InboxItemStatus.Open,
            Title: "Dangerous tool",
            Summary: "Awaiting runtime approval.",
            Source: "runtime.main_session.approval",
            CreatedAt: now,
            UpdatedAt: now,
            RequiresAction: true,
            Route: "/approvals/approval-runtime-001",
            SessionId: "main-awaiting-001",
            CorrelationId: "corr-runtime-001",
            ApprovalId: "approval-runtime-001"));
        await approvals.UpsertAsync(new Approval(
            Id: "channel-delivery-approval-draft-001",
            Kind: ApprovalKind.ChannelDelivery,
            Status: ApprovalStatus.Pending,
            Title: "Review draft reply",
            Summary: "Channel draft still pending.",
            Source: "channel.delivery",
            RequestedAt: now,
            UpdatedAt: now,
            SessionId: "channel-dm-binding-001",
            CorrelationId: "corr-channel-001",
            InboxItemId: "channel-delivery-inbox-draft-001",
            PayloadJson: "{\"draftId\":\"draft-001\"}"));
        await inbox.UpsertAsync(new InboxItem(
            Id: "channel-delivery-inbox-draft-001",
            Kind: InboxItemKind.ChannelUpdate,
            Status: InboxItemStatus.Open,
            Title: "Review draft reply",
            Summary: "Awaiting delivery approval.",
            Source: "channel.delivery",
            CreatedAt: now,
            UpdatedAt: now,
            RequiresAction: true,
            Route: "/approvals/channel-delivery-approval-draft-001",
            SessionId: "channel-dm-binding-001",
            CorrelationId: "corr-channel-001",
            ApprovalId: "channel-delivery-approval-draft-001"));

        var definitionRepository = provider.GetRequiredService<IAutomationDefinitionRepository>();
        var runRepository = provider.GetRequiredService<IAutomationRunRepository>();
        await definitionRepository.UpsertAsync(new AutomationDefinition(
            Id: "auto-repair",
            Title: "Repair automation",
            Prompt: "Summarize inbox items.",
            Source: AutomationDefinitionSource.Manual,
            SourcePath: null,
            CronExpression: "0 * * * *",
            Enabled: true,
            InputPaths: ["workspace/inbox"],
            ModelId: null,
            NotificationChannels: null,
            NotifyMode: AutomationNotifyMode.None,
            CreatedAt: now,
            UpdatedAt: now,
            LastRunAt: now.AddHours(-1),
            NextRunAt: now.AddHours(1),
            LastRunStatus: AutomationRunStatus.Succeeded,
            LastError: null));
        await runRepository.AddAsync(new AutomationRunRecord(
            RunId: "run-stale-001",
            AutomationId: "auto-repair",
            Status: AutomationRunStatus.Running,
            Trigger: "automation.scheduler",
            Attempt: 2,
            SessionId: "auto-repair-session",
            StartedAt: now.AddMinutes(-30),
            CompletedAt: null,
            Summary: null,
            ErrorMessage: null));

        Directory.CreateDirectory(Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.LogsDirectory));
        await File.WriteAllTextAsync(
            Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.LogsDirectory, "gateway.log"),
            "2026-03-19T15:20:00Z startup repair prepared\nhardening-inline-secret should not appear in summaries\n");

        await SeedSessionAsync(
            workspaceRoot,
            "main-awaiting-001",
            new AgentInfo
            {
                AgentId = "main-awaiting-001",
                CreatedAt = "2026-03-19T15:00:00Z",
                MessageCount = 3,
                LastSfpIndex = 12,
                Breakpoint = BreakpointState.AwaitingApproval,
                LastBookmark = new Bookmark
                {
                    Seq = 1,
                    Timestamp = 1_763_596_800_000,
                }
            },
            toolCalls:
            [
                new ToolCallRecord
                {
                    Id = "call-runtime-001",
                    Name = "bash_run",
                    Input = new { command = "rm -rf /tmp/demo" },
                    State = ToolCallState.ApprovalRequired,
                    Approval = new ToolCallApproval
                    {
                        Required = true,
                        Decision = null,
                        DecidedBy = null,
                    },
                    CreatedAt = 1_763_596_800_000,
                    UpdatedAt = 1_763_596_800_000,
                }
            ]);
    }

    private static async Task<string> WriteFixtureManifestAsync(string workspaceRoot)
    {
        var manifestPath = Path.Combine(workspaceRoot, "update-manifest.acceptance.json");
        var escapedManifestPath = manifestPath.Replace("\\", "\\\\", StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            manifestPath,
            $$"""
            {
              "generatedAt": "2026-03-19T10:00:00Z",
              "source": "{{escapedManifestPath}}",
              "channels": [
                {
                  "channel": "Stable",
                  "latestVersion": "0.1.2",
                  "gatewayVersion": "0.1.2",
                  "desktopVersion": "0.1.3",
                  "downloadUrl": "https://example.com/download",
                  "releaseNotesUrl": "https://example.com/release-notes",
                  "releaseNotes": [
                    "Adds the manual-first update desk.",
                    "Improves operator release visibility."
                  ],
                  "guidance": "Review the release notes, then complete the guided handoff manually."
                }
              ]
            }
            """);
        return manifestPath;
    }

    private static async Task SeedSessionAsync(
        string workspaceRoot,
        string sessionId,
        AgentInfo info,
        IReadOnlyList<ToolCallRecord>? toolCalls = null)
    {
        var sessionsRoot = Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.SessionsDirectory);
        var store = new JsonAgentStore(sessionsRoot);
        await store.SaveInfoAsync(sessionId, info);
        await store.SaveMessagesAsync(sessionId, []);
        await store.SaveToolCallRecordsAsync(sessionId, toolCalls ?? []);
    }

    private static async Task<T?> ReadJsonAsync<T>(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
    }

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot(string prefix)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                prefix,
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

    private sealed class NoOpHeartbeatSyncService : IHeartbeatSyncService
    {
        public Task<HeartbeatSyncResult> SyncAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new HeartbeatSyncResult(0, 0, false));
    }
}
