using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using KodaClaw.Automation;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.System;
using KodaClaw.Storage.Json;
using KodaClaw.Workspace;
using KodaClaw.Workspace.Heartbeat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class AutomationApiIntegrationTests
{
    [Fact]
    public async Task Automations_list_should_require_token()
    {
        using var workspace = new TempAutomationWorkspaceRoot();
        await SeedAutomationAsync(workspace.Path, CreateAutomationDefinition("auto-heartbeat", enabled: true));
        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);

        var response = await hosted.Client.GetAsync("/api/automations");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Automations_list_should_return_filtered_items()
    {
        using var workspace = new TempAutomationWorkspaceRoot();
        await SeedAutomationAsync(workspace.Path, CreateAutomationDefinition("auto-heartbeat", enabled: true));
        await SeedAutomationAsync(workspace.Path, CreateAutomationDefinition("auto-manual", enabled: false, source: AutomationDefinitionSource.Manual));
        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);

        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/automations?enabled=true&source=Heartbeat&limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<AutomationDefinitionsQueryResponse>();
        payload.Should().NotBeNull();
        payload!.Items.Select(item => item.Id).Should().Equal("auto-heartbeat");
    }

    [Fact]
    public async Task Automation_detail_should_return_not_found_when_missing()
    {
        using var workspace = new TempAutomationWorkspaceRoot();
        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);

        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/automations/missing-id");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        payload.Should().NotBeNull();
        payload!.Code.Should().Be("automation.not_found");
    }

    [Fact]
    public async Task Automation_runs_should_return_filtered_items()
    {
        using var workspace = new TempAutomationWorkspaceRoot();
        await SeedAutomationAsync(
            workspace.Path,
            CreateAutomationDefinition("auto-heartbeat", enabled: true),
            CreateRunRecord("run-succeeded", "auto-heartbeat", AutomationRunStatus.Succeeded),
            CreateRunRecord("run-failed", "auto-heartbeat", AutomationRunStatus.Failed));
        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);

        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/automations/auto-heartbeat/runs?status=Succeeded&limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<AutomationRunsQueryResponse>();
        payload.Should().NotBeNull();
        payload!.Items.Select(item => item.RunId).Should().Equal("run-succeeded");
    }

    [Fact]
    public async Task Automation_patch_should_update_enabled_state_and_return_reloaded_payload()
    {
        using var workspace = new TempAutomationWorkspaceRoot();
        await SeedAutomationAsync(workspace.Path, CreateAutomationDefinition("auto-heartbeat", enabled: true));
        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);

        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.PatchAsJsonAsync(
            "/api/automations/auto-heartbeat",
            new UpdateAutomationDefinitionRequest(Enabled: false));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<AutomationDefinition>();
        payload.Should().NotBeNull();
        payload!.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Automation_list_should_reject_invalid_source_filter()
    {
        using var workspace = new TempAutomationWorkspaceRoot();
        await SeedAutomationAsync(workspace.Path, CreateAutomationDefinition("auto-heartbeat", enabled: true));
        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);

        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/automations?source=unknown-source");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        payload.Should().NotBeNull();
        payload!.Code.Should().Be("validation.automation_source_invalid");
    }

    [Fact]
    public async Task Automation_runs_should_reject_invalid_status_filter()
    {
        using var workspace = new TempAutomationWorkspaceRoot();
        await SeedAutomationAsync(workspace.Path, CreateAutomationDefinition("auto-heartbeat", enabled: true));
        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);

        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/automations/auto-heartbeat/runs?status=NotAStatus");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        payload.Should().NotBeNull();
        payload!.Code.Should().Be("validation.automation_run_status_invalid");
    }

    private static async Task SeedAutomationAsync(
        string workspaceRoot,
        AutomationDefinition definition,
        params AutomationRunRecord[] runs)
    {
        var services = new ServiceCollection();
        services.AddKodaClawWorkspace(options => options.RootPath = workspaceRoot);
        services.AddKodaClawJsonStore(workspaceRoot);
        services.AddKodaClawAutomation();
        using var provider = services.BuildServiceProvider();

        var definitionRepository = provider.GetRequiredService<IAutomationDefinitionRepository>();
        await definitionRepository.UpsertAsync(definition);

        if (runs.Length == 0)
        {
            return;
        }

        var runRepository = provider.GetRequiredService<IAutomationRunRepository>();
        foreach (var run in runs)
        {
            await runRepository.AddAsync(run);
        }
    }

    private static Task<HostedGateway> StartRealWorkspaceGatewayAsync(string workspaceRoot)
    {
        return HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false,
                rootPath: workspaceRoot),
            configureServices: services =>
            {
                // Replace with no-op to prevent HeartbeatFileWatcherHostedService from
                // deleting the seeded test automations on startup.
                services.Replace(ServiceDescriptor.Singleton<IHeartbeatSyncService>(
                    _ => new NoOpHeartbeatSyncService()));
            },
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                });
            },
            useTestWorkspaceService: false);
    }

    private sealed class NoOpHeartbeatSyncService : IHeartbeatSyncService
    {
        public Task<HeartbeatSyncResult> SyncAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new HeartbeatSyncResult(0, 0, false));
    }

    private static AutomationDefinition CreateAutomationDefinition(
        string id,
        bool enabled,
        AutomationDefinitionSource source = AutomationDefinitionSource.Heartbeat)
    {
        return new AutomationDefinition(
            Id: id,
            Title: source == AutomationDefinitionSource.Heartbeat ? "Heartbeat Digest" : "Manual Sweep",
            Prompt: "Review unresolved inbox items and summarize the queue.",
            Source: source,
            SourcePath: source == AutomationDefinitionSource.Heartbeat ? "workspace/HEARTBEAT.md" : null,
            CronExpression: "0 * * * *",
            Enabled: enabled,
            InputPaths: ["workspace/inbox", "workspace/tasks"],
            ModelId: null,
            NotificationChannels: null,
            NotifyMode: AutomationNotifyMode.None,
            CreatedAt: new DateTimeOffset(2026, 3, 18, 8, 0, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 3, 18, 8, 0, 0, TimeSpan.Zero),
            LastRunAt: new DateTimeOffset(2026, 3, 18, 9, 0, 0, TimeSpan.Zero),
            NextRunAt: new DateTimeOffset(2026, 3, 19, 9, 0, 0, TimeSpan.Zero),
            LastRunStatus: AutomationRunStatus.Succeeded,
            LastError: null);
    }

    private static AutomationRunRecord CreateRunRecord(string runId, string automationId, AutomationRunStatus status)
    {
        return new AutomationRunRecord(
            RunId: runId,
            AutomationId: automationId,
            Status: status,
            Trigger: "heartbeat",
            Attempt: 1,
            SessionId: $"{automationId}-session",
            StartedAt: new DateTimeOffset(2026, 3, 18, 9, 0, 0, TimeSpan.Zero),
            CompletedAt: new DateTimeOffset(2026, 3, 18, 9, 1, 0, TimeSpan.Zero),
            Summary: status == AutomationRunStatus.Succeeded ? "Queue digest posted." : "Queue digest failed.",
            ErrorMessage: status == AutomationRunStatus.Failed ? "Network timeout" : null);
    }

    private sealed class TempAutomationWorkspaceRoot : IDisposable
    {
        public TempAutomationWorkspaceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-automation-api",
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
