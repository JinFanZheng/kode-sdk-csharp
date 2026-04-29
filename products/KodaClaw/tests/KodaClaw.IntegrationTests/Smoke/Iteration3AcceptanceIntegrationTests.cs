using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using KodaClaw.Automation;
using KodaClaw.Automation.Scheduler;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Canvas;
using KodaClaw.Contracts.Inbox;
using KodaClaw.IntegrationTests.Gateway;
using KodaClaw.Workspace;
using KodaClaw.Workspace.Heartbeat;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace KodaClaw.IntegrationTests.Smoke;

public sealed class Iteration3AcceptanceIntegrationTests
{
    private const string GatewayToken = "test-token";

    [Fact]
    public async Task Iteration_3_acceptance_should_cover_automation_run_inbox_delivery_and_canvas_publication()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);

        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);

        var definitions = hosted.Services.GetRequiredService<IAutomationDefinitionRepository>();
        var runs = hosted.Services.GetRequiredService<IAutomationRunRepository>();
        var scheduler = hosted.Services.GetRequiredService<IAutomationScheduler>();

        var definition = CreateDefinition();
        await definitions.UpsertAsync(definition);

        var executed = await scheduler.RunOnceAsync();
        executed.Should().Be(1);

        var reloadedDefinition = await definitions.GetByIdAsync(definition.Id);
        reloadedDefinition.Should().NotBeNull();
        reloadedDefinition!.LastRunStatus.Should().Be(AutomationRunStatus.Succeeded);
        reloadedDefinition.LastRunAt.Should().NotBeNull();
        reloadedDefinition.NextRunAt.Should().NotBeNull();

        var run = (await runs.ListAsync(new AutomationRunQuery(AutomationId: definition.Id, Limit: 10))).Single();
        run.Status.Should().Be(AutomationRunStatus.Succeeded);
        run.SessionId.Should().NotBeNullOrWhiteSpace();
        run.Summary.Should().NotBeNullOrWhiteSpace();

        var automationList = await hosted.Client.GetFromJsonAsync<AutomationDefinitionsQueryResponse>(
            "/api/automations?enabled=true&source=Heartbeat&limit=20");
        automationList.Should().NotBeNull();
        automationList!.Items.Should().Contain(item =>
            item.Id == definition.Id &&
            item.LastRunStatus == AutomationRunStatus.Succeeded);

        var automationDetail = await hosted.Client.GetFromJsonAsync<AutomationDefinition>($"/api/automations/{definition.Id}");
        automationDetail.Should().NotBeNull();
        automationDetail!.LastRunStatus.Should().Be(AutomationRunStatus.Succeeded);

        var automationRuns = await hosted.Client.GetFromJsonAsync<AutomationRunsQueryResponse>(
            $"/api/automations/{definition.Id}/runs?status=Succeeded&limit=20");
        automationRuns.Should().NotBeNull();
        automationRuns!.Items.Should().Contain(item => item.RunId == run.RunId);

        var inbox = await hosted.Client.GetFromJsonAsync<InboxQueryResponse>(
            $"/api/inbox?kind=AutomationResult&sessionId={Uri.EscapeDataString(run.SessionId!)}&limit=20");
        inbox.Should().NotBeNull();
        var inboxItem = inbox!.Items.Should().ContainSingle(item => item.Id == $"automation-result-{run.RunId}").Subject;
        inboxItem.Route.Should().Be($"/automations/{definition.Id}");
        inboxItem.RequiresAction.Should().BeFalse();
        inboxItem.Status.Should().Be(InboxItemStatus.Open);

        var entryPath = "workspace/canvas/artifacts/heartbeat-digest/index.html";
        var entryAbsolutePath = Path.Combine(workspace.Path, entryPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(entryAbsolutePath)!);
        await File.WriteAllTextAsync(
            entryAbsolutePath,
            $"<html><body><h1>heartbeat digest</h1><p>{run.RunId}</p></body></html>");

        var publishResponse = await hosted.Client.PostAsJsonAsync(
            "/api/canvas",
            new UpsertCanvasArtifactRequest(
                Id: "canvas-heartbeat-digest",
                Title: "Heartbeat Digest",
                Kind: CanvasArtifactKind.Report,
                Summary: "Automation digest canvas artifact.",
                Source: "runtime.automation",
                EntryPath: entryPath,
                AssetDirectory: "workspace/canvas/artifacts/heartbeat-digest",
                Route: "/canvas/heartbeat-digest",
                SessionId: run.SessionId,
                CorrelationId: run.RunId,
                MetadataJson: $"{{\"automationId\":\"{definition.Id}\",\"runId\":\"{run.RunId}\"}}"));

        publishResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var publishedArtifact = await publishResponse.Content.ReadFromJsonAsync<CanvasArtifact>();
        publishedArtifact.Should().NotBeNull();
        publishedArtifact!.SessionId.Should().Be(run.SessionId);
        publishedArtifact.EntryPath.Should().Be(entryPath);

        var canvasList = await hosted.Client.GetFromJsonAsync<CanvasQueryResponse>(
            $"/api/canvas?sessionId={Uri.EscapeDataString(run.SessionId!)}&limit=20");
        canvasList.Should().NotBeNull();
        canvasList!.Items.Should().Contain(item => item.Id == publishedArtifact.Id);
        canvasList.DefaultArtifactId.Should().Be(publishedArtifact.Id);

        var defaultEntry = await hosted.Client.GetFromJsonAsync<CanvasEntryResponse>("/api/canvas/default");
        defaultEntry.Should().NotBeNull();
        defaultEntry!.ArtifactId.Should().Be(publishedArtifact.Id);
        defaultEntry.EntryPath.Should().Be(entryPath);

        var canvasDetail = await hosted.Client.GetFromJsonAsync<CanvasArtifact>($"/api/canvas/{publishedArtifact.Id}");
        canvasDetail.Should().NotBeNull();
        canvasDetail!.CorrelationId.Should().Be(run.RunId);

        var fileResponse = await hosted.Client.GetAsync("/api/canvas/fs/workspace/canvas/artifacts/heartbeat-digest/index.html");
        fileResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await fileResponse.Content.ReadAsStringAsync();
        body.Should().Contain(run.RunId);
    }

    private static Task<HostedGateway> StartGatewayAsync(string workspaceRoot)
    {
        return HostedGateway.StartAsync(
            gatewayToken: GatewayToken,
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false,
                rootPath: workspaceRoot),
            configureServices: services =>
            {
                services.AddSingleton<IModelProvider>(new StubModelProvider());
                // Replace with no-op to prevent HeartbeatFileWatcherHostedService from
                // creating default automations that inflate RunOnceAsync() count.
                services.Replace(ServiceDescriptor.Singleton<IHeartbeatSyncService>(
                    _ => new NoOpHeartbeatSyncService()));
            },
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                    ["KODACLAW_DEFAULT_MODEL"] = "gpt-4o-mini",
                    ["OPENAI_API_KEY"] = "stub-key",
                });
            },
            useTestWorkspaceService: false);
    }

    private sealed class NoOpHeartbeatSyncService : IHeartbeatSyncService
    {
        public Task<HeartbeatSyncResult> SyncAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new HeartbeatSyncResult(0, 0, false));
    }

    private static AutomationDefinition CreateDefinition()
    {
        return new AutomationDefinition(
            Id: "heartbeat-digest",
            Title: "Heartbeat Digest",
            Prompt: "Summarize unresolved inbox items and publish a concise heartbeat.",
            Source: AutomationDefinitionSource.Heartbeat,
            SourcePath: "workspace/HEARTBEAT.md",
            CronExpression: "0 * * * *",
            Enabled: true,
            InputPaths: ["workspace/inbox", "workspace/tasks"],
            ModelId: null,
            NotificationChannels: null,
            NotifyMode: AutomationNotifyMode.None,
            CreatedAt: new DateTimeOffset(2026, 3, 18, 8, 0, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 3, 18, 8, 0, 0, TimeSpan.Zero),
            LastRunAt: null,
            NextRunAt: null,
            LastRunStatus: null,
            LastError: null);
    }

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-iteration3-acceptance",
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

    private sealed class StubModelProvider : IModelProvider
    {
        public string ProviderName => "stub";

        public async IAsyncEnumerable<StreamChunk> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();

            yield return new StreamChunk
            {
                Type = StreamChunkType.TextDelta,
                TextDelta = "automation-digest-ready",
            };
            yield return new StreamChunk
            {
                Type = StreamChunkType.MessageStop,
                StopReason = ModelStopReason.EndTurn,
                Usage = new TokenUsage
                {
                    InputTokens = 0,
                    OutputTokens = 0,
                },
            };
        }

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ModelResponse
            {
                Content =
                [
                    new TextContent
                    {
                        Text = "automation-digest-ready",
                    },
                ],
                StopReason = ModelStopReason.EndTurn,
                Usage = new TokenUsage
                {
                    InputTokens = 0,
                    OutputTokens = 0,
                },
                Model = request.Model,
            });
        }

        public Task<bool> ValidateAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }
    }
}
