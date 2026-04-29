using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using KodaClaw.Automation;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Approvals;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.Plugins;
using KodaClaw.Contracts.Repair;
using KodaClaw.Contracts.Workspace;
using KodaClaw.ControlPlane;
using KodaClaw.PluginHost;
using KodaClaw.Storage.Json;
using KodaClaw.Workspace;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Store.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class StartupRepairApiIntegrationTests
{
    [Fact]
    public async Task Startup_repair_should_stabilize_workspace_and_return_latest_report()
    {
        using var workspace = new TempWorkspaceRoot("kodaclaw-startup-repair");
        await SeedWorkspaceAsync(workspace.Path);

        await using var hosted = await StartHostedGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "repair-token");

        var reportPath = Path.Combine(
            workspace.Path,
            KodaClawWorkspaceLayout.ConfigDirectory,
            KodaClawWorkspaceLayout.StartupRepairReportFile);
        File.Exists(reportPath).Should().BeTrue();

        var response = await hosted.Client.GetAsync("/api/system/startup-repair-report");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<StartupRepairReportResponse>();
        payload.Should().NotBeNull();
        payload!.ReportPath.Should().Be(reportPath);
        payload.Checklist.Scope.Should().Be("startupRepair");
        payload.Checklist.Items.Should().Contain(item => item.Id == "approval-canceled:approval-runtime-001");
        payload.Checklist.Items.Should().Contain(item => item.Id == "approval-canceled:channel-delivery-approval-draft-001");
        payload.Checklist.Items.Should().Contain(item => item.Id == "automation-run-failed:run-stale-001");
        payload.Checklist.Items.Should().Contain(item => item.Id == "plugin-runtime-recovered:plugin.fixture.repair");
        payload.Checklist.Items.Should().Contain(item => item.Id == "active-main-session-reset:main-awaiting-001");
        payload.Checklist.Items.Should().Contain(item => item.Id == "session-interrupted:main-awaiting-001");
        payload.Checklist.Summary.ActionRequiredCount.Should().BeGreaterThan(0);

        var approvalRepository = hosted.Services.GetRequiredService<IApprovalRepository>();
        var repairedRuntimeApproval = await approvalRepository.GetByIdAsync("approval-runtime-001");
        repairedRuntimeApproval.Should().NotBeNull();
        repairedRuntimeApproval!.Status.Should().Be(ApprovalStatus.Canceled);

        var repairedChannelApproval = await approvalRepository.GetByIdAsync("channel-delivery-approval-draft-001");
        repairedChannelApproval.Should().NotBeNull();
        repairedChannelApproval!.Status.Should().Be(ApprovalStatus.Canceled);

        var inboxRepository = hosted.Services.GetRequiredService<IInboxRepository>();
        (await inboxRepository.GetByIdAsync("inbox-runtime-001"))!.Status.Should().Be(InboxItemStatus.Resolved);
        (await inboxRepository.GetByIdAsync("channel-delivery-inbox-draft-001"))!.Status.Should().Be(InboxItemStatus.Resolved);
        var repairInbox = await inboxRepository.GetByIdAsync("startup-repair-latest");
        repairInbox.Should().NotBeNull();
        repairInbox!.Kind.Should().Be(InboxItemKind.Alert);
        repairInbox.Status.Should().Be(InboxItemStatus.Open);
        repairInbox.RequiresAction.Should().BeTrue();

        var workspaceService = hosted.Services.GetRequiredService<IWorkspaceService>();
        var appConfig = await workspaceService.LoadAppConfigAsync();
        appConfig.ActiveMainSessionId.Should().BeNull();

        var runRepository = hosted.Services.GetRequiredService<IAutomationRunRepository>();
        var repairedRun = await runRepository.GetByIdAsync("run-stale-001");
        repairedRun.Should().NotBeNull();
        repairedRun!.Status.Should().Be(AutomationRunStatus.Failed);
        repairedRun.CompletedAt.Should().NotBeNull();

        var definitionRepository = hosted.Services.GetRequiredService<IAutomationDefinitionRepository>();
        var repairedDefinition = await definitionRepository.GetByIdAsync("auto-repair");
        repairedDefinition.Should().NotBeNull();
        repairedDefinition!.LastRunStatus.Should().Be(AutomationRunStatus.Failed);
        repairedDefinition.NextRunAt.Should().NotBeNull();

        var pluginRepository = hosted.Services.GetRequiredService<IPluginRegistryRepository>();
        var repairedPlugin = await pluginRepository.GetByIdAsync("plugin.fixture.repair");
        repairedPlugin.Should().NotBeNull();
        repairedPlugin!.RuntimeState.Should().Be(PluginRuntimeState.Stopped);
        repairedPlugin.LastError.Should().Contain("Startup repair marked");
    }

    [Fact]
    public async Task Startup_repair_report_should_require_authentication()
    {
        using var workspace = new TempWorkspaceRoot("kodaclaw-startup-repair-auth");
        await SeedWorkspaceAsync(workspace.Path);

        await using var hosted = await StartHostedGatewayAsync(workspace.Path);

        var response = await hosted.Client.GetAsync("/api/system/startup-repair-report");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<HostedGateway> StartHostedGatewayAsync(string workspaceRoot)
    {
        return await HostedGateway.StartAsync(
            gatewayToken: "repair-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false,
                rootPath: workspaceRoot),
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                    ["KODACLAW_STARTUP_REPAIR_ENABLED"] = "true",
                });
            },
            useTestWorkspaceService: false);
    }

    private static async Task SeedWorkspaceAsync(string workspaceRoot)
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

        var approvals = provider.GetRequiredService<IApprovalRepository>();
        var inbox = provider.GetRequiredService<IInboxRepository>();
        var definitions = provider.GetRequiredService<IAutomationDefinitionRepository>();
        var runs = provider.GetRequiredService<IAutomationRunRepository>();
        var plugins = provider.GetRequiredService<IPluginRegistryRepository>();

        var now = new DateTimeOffset(2026, 3, 19, 14, 0, 0, TimeSpan.Zero);
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

        await definitions.UpsertAsync(new AutomationDefinition(
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
        await runs.AddAsync(new AutomationRunRecord(
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

        var pluginRootPath = Path.Combine(workspaceRoot, "workspace", "plugins", "plugin.fixture.repair");
        Directory.CreateDirectory(pluginRootPath);
        await plugins.UpsertAsync(new PluginRecord(
            Id: "plugin.fixture.repair",
            Manifest: new PluginManifest(
                Id: "plugin.fixture.repair",
                Name: "Fixture Repair Plugin",
                Version: "0.1.0",
                Types: [PluginType.Tool],
                Runtime: new PluginRuntimeSpec(
                    Transport: PluginTransportKind.Stdio,
                    Command: "dotnet",
                    Args: ["repair.dll"]),
                Permissions: new PluginPermissionSet(Network: false),
                Capabilities: new PluginCapabilitySet(Tools: ["echo"])),
            InstallSource: PluginInstallSource.LocalDirectory,
            RootPath: pluginRootPath,
            TrustState: PluginTrustState.Trusted,
            Enabled: true,
            RuntimeState: PluginRuntimeState.Running,
            DiscoveredAt: now,
            InstalledAt: now,
            UpdatedAt: now));

        await SeedSessionAsync(
            workspaceRoot,
            "main-awaiting-001",
            new AgentInfo
            {
                AgentId = "main-awaiting-001",
                CreatedAt = "2026-03-19T13:55:00Z",
                MessageCount = 3,
                LastSfpIndex = 5,
                Breakpoint = BreakpointState.AwaitingApproval,
                LastBookmark = new Bookmark
                {
                    Seq = 1,
                    Timestamp = 1_763_593_200_000,
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
                    CreatedAt = 1_763_593_200_000,
                    UpdatedAt = 1_763_593_200_000,
                }
            ]);
    }

    private static async Task SeedSessionAsync(
        string workspaceRoot,
        string sessionId,
        AgentInfo info,
        IReadOnlyList<ToolCallRecord>? toolCalls = null)
    {
        var store = new JsonAgentStore(Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.SessionsDirectory));
        await store.SaveInfoAsync(sessionId, info);
        await store.SaveMessagesAsync(sessionId, []);
        await store.SaveToolCallRecordsAsync(sessionId, toolCalls ?? []);
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
}
