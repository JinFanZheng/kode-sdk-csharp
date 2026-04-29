using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Bootstrap;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Plugins;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using KodaClaw.IntegrationTests.Gateway;
using KodaClaw.PluginHost.Hosting;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Smoke;

public sealed class Iteration4AcceptanceIntegrationTests
{
    private const string GatewayToken = "test-token";
    private const string HealthyPluginId = "plugin.bundled.fixture";
    private const string DegradedPluginId = "plugin.bundled.degraded";

    [Fact]
    public async Task Iteration_4_acceptance_should_cover_bundled_plugin_discovery_lifecycle_and_runtime_injection()
    {
        using var workspace = new TempWorkspaceRoot("kodaclaw-iteration4-acceptance");
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);
        await CompleteBootstrapAsync(hosted.Client);

        var discoverResponse = await hosted.Client.PostAsync("/api/plugins/discover", null);
        discoverResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var discovered = await discoverResponse.Content.ReadFromJsonAsync<PluginsQueryResponse>();
        discovered.Should().NotBeNull();

        var bundledSummary = discovered!.Items.Should().ContainSingle(item => item.Id == HealthyPluginId).Subject;
        bundledSummary.InstallSource.Should().Be(PluginInstallSource.Bundled);
        bundledSummary.Enabled.Should().BeFalse();
        bundledSummary.RuntimeState.Should().Be(PluginRuntimeState.Stopped);

        var bundledDetail = await hosted.Client.GetFromJsonAsync<PluginDetail>($"/api/plugins/{HealthyPluginId}");
        bundledDetail.Should().NotBeNull();
        bundledDetail!.Record.RootPath.Should().Be(Path.Combine(ResolveBundledPluginsRoot(), HealthyPluginId));
        bundledDetail.Record.InstallSource.Should().Be(PluginInstallSource.Bundled);
        bundledDetail.Record.TrustEvidence.Should().NotBeNull();
        bundledDetail.AvailableTools.Should().Contain("mcp__plugin.bundled.fixture__echo");

        var trusted = await PostPluginActionAsync(hosted.Client, HealthyPluginId, "trust");
        trusted.Record.TrustState.Should().Be(PluginTrustState.Signed);
        trusted.Record.TrustEvidence.Should().NotBeNull();
        trusted.Record.TrustEvidence!.VerificationState.Should().Be(PluginTrustVerificationState.Verified);

        var enabled = await PostPluginActionAsync(hosted.Client, HealthyPluginId, "enable");
        enabled.Record.Enabled.Should().BeTrue();

        var started = await PostPluginActionAsync(hosted.Client, HealthyPluginId, "start");
        started.Record.RuntimeState.Should().Be(PluginRuntimeState.Running);
        started.HealthSummary.IsHealthy.Should().BeTrue();
        started.AvailableTools.Should().Contain("mcp__plugin.bundled.fixture__echo");

        var filtered = await hosted.Client.GetFromJsonAsync<PluginsQueryResponse>(
            "/api/plugins?type=Tool&trustState=Signed&enabled=true&runtimeState=Running&limit=20");
        filtered.Should().NotBeNull();
        filtered!.Items.Should().Contain(item => item.Id == HealthyPluginId);

        var logs = await hosted.Client.GetFromJsonAsync<List<PluginLogEntry>>($"/api/plugins/{HealthyPluginId}/logs?limit=20");
        logs.Should().NotBeNull();
        logs!.Should().Contain(entry => entry.Message == "Plugin started successfully.");

        var runtime = hosted.Services.GetRequiredService<IMainSessionService>();
        var workspaceService = hosted.Services.GetRequiredService<IWorkspaceService>();

        var firstHandle = await runtime.EnsureMainSessionAsync();
        var firstToolIds = await ReadToolIdsAsync(firstHandle.SessionDirectory);
        firstToolIds.Should().Contain("mcp__plugin.bundled.fixture__echo");

        var sessions = await hosted.Client.GetFromJsonAsync<SessionsQueryResponse>("/api/sessions?limit=20");
        sessions.Should().NotBeNull();
        sessions!.Sessions.Should().Contain(session =>
            session.SessionId == firstHandle.SessionId &&
            session.Status.IsActiveMainSession);

        var stopped = await PostPluginActionAsync(hosted.Client, HealthyPluginId, "stop");
        stopped.Record.RuntimeState.Should().Be(PluginRuntimeState.Stopped);

        await ClearActiveMainSessionAsync(workspaceService);

        var secondHandle = await runtime.EnsureMainSessionAsync();
        secondHandle.SessionId.Should().NotBe(firstHandle.SessionId);
        var secondToolIds = await ReadToolIdsAsync(secondHandle.SessionDirectory);
        secondToolIds.Should().NotContain("mcp__plugin.bundled.fixture__echo");
    }

    [Fact]
    public async Task Iteration_4_acceptance_should_keep_gateway_healthy_when_bundled_plugin_degrades()
    {
        using var workspace = new TempWorkspaceRoot("kodaclaw-iteration4-degraded");
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);
        await CompleteBootstrapAsync(hosted.Client);

        var discoverResponse = await hosted.Client.PostAsync("/api/plugins/discover", null);
        discoverResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await PostPluginActionAsync(hosted.Client, DegradedPluginId, "trust");
        await PostPluginActionAsync(hosted.Client, DegradedPluginId, "enable");
        var started = await PostPluginActionAsync(hosted.Client, DegradedPluginId, "start");
        started.Record.RuntimeState.Should().Be(PluginRuntimeState.Running);

        var lifecycleHost = hosted.Services.GetRequiredService<IPluginLifecycleHost>();
        var degradedRecord = await lifecycleHost.CheckHealthAsync(DegradedPluginId);
        degradedRecord.RuntimeState.Should().Be(PluginRuntimeState.Degraded);
        degradedRecord.LastError.Should().Contain("healthcheck");

        var degradedDetail = await hosted.Client.GetFromJsonAsync<PluginDetail>($"/api/plugins/{DegradedPluginId}");
        degradedDetail.Should().NotBeNull();
        degradedDetail!.Record.RuntimeState.Should().Be(PluginRuntimeState.Degraded);
        degradedDetail.HealthSummary.IsHealthy.Should().BeFalse();

        var logs = await hosted.Client.GetFromJsonAsync<List<PluginLogEntry>>($"/api/plugins/{DegradedPluginId}/logs?limit=20");
        logs.Should().NotBeNull();
        logs!.Should().Contain(entry =>
            entry.Source == "plugin.health" &&
            entry.Message.Contains("Plugin healthcheck failed.", StringComparison.Ordinal));

        var diagnostics = await hosted.Client.GetFromJsonAsync<DiagnosticsQueryResponse>(
            "/api/diagnostics/recent?source=plugin.health&limit=50");
        diagnostics.Should().NotBeNull();
        diagnostics!.Events.Should().Contain(entry => entry.EventType == "plugin.degraded");

        var runtime = hosted.Services.GetRequiredService<IMainSessionService>();
        var handle = await runtime.EnsureMainSessionAsync();
        var toolIds = await ReadToolIdsAsync(handle.SessionDirectory);
        toolIds.Should().NotContain("mcp__plugin.bundled.degraded__echo");

        var systemHealth = await hosted.Client.GetFromJsonAsync<SystemHealthResponse>("/api/system/health");
        systemHealth.Should().NotBeNull();
        systemHealth!.Status.Should().Be("healthy");
    }

    private static async Task<PluginDetail> PostPluginActionAsync(HttpClient client, string pluginId, string action)
    {
        var response = await client.PostAsync($"/api/plugins/{pluginId}/{action}", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<PluginDetail>();
        payload.Should().NotBeNull();
        return payload!;
    }

    private static async Task CompleteBootstrapAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/system/bootstrap-complete",
            new BootstrapCompletionRequest("# identity", "# soul", "# user", ArchiveBootstrapFile: true));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task ClearActiveMainSessionAsync(IWorkspaceService workspaceService)
    {
        var appConfig = await workspaceService.LoadAppConfigAsync();
        await workspaceService.SaveAppConfigAsync(appConfig with { ActiveMainSessionId = null });
    }

    private static async Task<IReadOnlyList<string>> ReadToolIdsAsync(string sessionDirectory)
    {
        var metaPath = Path.Combine(sessionDirectory, "meta.json");
        File.Exists(metaPath).Should().BeTrue();

        await using var stream = File.OpenRead(metaPath);
        using var document = await JsonDocument.ParseAsync(stream);

        if (!document.RootElement.TryGetProperty("metadata", out var metadataElement) ||
            metadataElement.ValueKind != JsonValueKind.Object ||
            !metadataElement.TryGetProperty("toolIds", out var toolIdsElement) ||
            toolIdsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return toolIdsElement
            .EnumerateArray()
            .Where(node => node.ValueKind == JsonValueKind.String)
            .Select(node => node.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
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
            },
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                    ["KODACLAW_BUNDLED_PLUGINS_ROOT"] = ResolveBundledPluginsRoot(),
                    ["KODACLAW_DEFAULT_MODEL"] = "gpt-4o-mini",
                    ["OPENAI_API_KEY"] = "stub-key",
                });
            },
            useTestWorkspaceService: false);
    }

    private static string ResolveBundledPluginsRoot()
    {
        var baseDirectory = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var frameworkDirectory = new DirectoryInfo(baseDirectory);
        var configurationDirectory = frameworkDirectory.Parent
            ?? throw new DirectoryNotFoundException("Unable to resolve integration test configuration directory.");
        var binDirectory = configurationDirectory.Parent
            ?? throw new DirectoryNotFoundException("Unable to resolve integration test bin directory.");
        var integrationTestsDirectory = binDirectory.Parent
            ?? throw new DirectoryNotFoundException("Unable to resolve integration test project directory.");

        var fixturesRoot = Path.GetFullPath(Path.Combine(
            integrationTestsDirectory.FullName,
            "..",
            "Fixtures",
            "Plugins"));

        if (!Directory.Exists(fixturesRoot))
        {
            throw new DirectoryNotFoundException($"Bundled plugin fixtures root was not found: '{fixturesRoot}'.");
        }

        return fixturesRoot;
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
                TextDelta = "stub",
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
                        Text = "stub",
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
