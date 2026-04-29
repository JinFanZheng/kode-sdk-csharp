using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Bootstrap;
using KodaClaw.Contracts.Chat;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.ControlPlane;
using KodaClaw.Runtime.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class GatewayDiagnosticsIntegrationTests
{
    [Fact]
    public async Task Diagnostics_recent_should_require_token()
    {
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(requiresBootstrap: false));

        var response = await hosted.Client.GetAsync("/api/diagnostics/recent");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Diagnostics_timeline_should_require_token()
    {
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(requiresBootstrap: false));

        var response = await hosted.Client.GetAsync("/api/diagnostics/timeline");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Diagnostics_recent_should_support_full_filters()
    {
        var diagnostics = BuildSeededDiagnosticsService(
        [
            new DiagnosticEvent(
                Id: "diag-001",
                Source: "gateway.chat",
                EventType: "gateway.chat.requested",
                Level: "info",
                Message: "requested",
                Timestamp: new DateTimeOffset(2026, 3, 18, 10, 0, 0, TimeSpan.Zero),
                CorrelationId: "corr-a",
                SessionId: "session-a"),
            new DiagnosticEvent(
                Id: "diag-002",
                Source: "gateway.chat",
                EventType: "gateway.chat.failed",
                Level: "error",
                Message: "failed",
                Timestamp: new DateTimeOffset(2026, 3, 18, 10, 1, 0, TimeSpan.Zero),
                CorrelationId: "corr-a",
                SessionId: "session-b"),
            new DiagnosticEvent(
                Id: "diag-003",
                Source: "workspace.bootstrap",
                EventType: "workspace.bootstrap.completed",
                Level: "info",
                Message: "completed",
                Timestamp: new DateTimeOffset(2026, 3, 18, 10, 2, 0, TimeSpan.Zero),
                CorrelationId: "corr-b",
                SessionId: "session-b")
        ]);

        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(requiresBootstrap: false),
            configureServices: services =>
            {
                services.AddSingleton<IDiagnosticsService>(diagnostics);
                RemoveMetricsBridge(services);
            });

        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync(
            "/api/diagnostics/recent?correlationId=corr-a&sessionId=session-b&source=GATEWAY.CHAT&eventType=gateway.chat.failed&level=ERROR&limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<DiagnosticsQueryResponse>();
        payload.Should().NotBeNull();
        payload!.Events.Select(item => item.Id).Should().Equal("diag-002");
    }

    [Fact]
    public async Task Diagnostics_timeline_should_use_default_limit_of_50()
    {
        var diagnostics = BuildSeededDiagnosticsService(CreateDiagnosticEvents(70));
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(requiresBootstrap: false),
            configureServices: services =>
            {
                services.AddSingleton<IDiagnosticsService>(diagnostics);
                RemoveMetricsBridge(services);
            });

        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/diagnostics/timeline");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<DiagnosticsQueryResponse>();
        payload.Should().NotBeNull();
        payload!.Events.Should().HaveCount(50);
        payload.Events[0].Id.Should().Be("diag-070");
    }

    [Fact]
    public async Task Diagnostics_timeline_should_clamp_limit_to_100()
    {
        var diagnostics = BuildSeededDiagnosticsService(CreateDiagnosticEvents(150));
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(requiresBootstrap: false),
            configureServices: services =>
            {
                services.AddSingleton<IDiagnosticsService>(diagnostics);
                RemoveMetricsBridge(services);
            });

        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/diagnostics/timeline?limit=999");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<DiagnosticsQueryResponse>();
        payload.Should().NotBeNull();
        payload!.Events.Should().HaveCount(100);
        payload.Events[0].Id.Should().Be("diag-150");
        payload.Events[^1].Id.Should().Be("diag-051");
    }

    [Fact]
    public async Task Chat_stream_should_echo_correlation_id_and_record_diagnostics()
    {
        var chatEvents = new[]
        {
            new ChatStreamEvent("text_chunk", "session-001", Sequence: 1, Delta: "hello"),
            new ChatStreamEvent("done", "session-001", Reason: "completed")
        };

        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(requiresBootstrap: false),
            configureServices: services =>
            {
                services.AddSingleton<IChatSessionService>(new FakeChatSessionService(chatEvents));
            });

        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream")
        {
            Content = JsonContent.Create(new ChatStreamRequest("hello"))
        };
        request.Headers.Add("X-KodaClaw-Correlation-Id", "corr-chat-001");

        using var response = await hosted.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("event: text_chunk");
        response.Headers.GetValues("X-KodaClaw-Correlation-Id").Single().Should().Be("corr-chat-001");

        var diagnosticsResponse = await hosted.Client.GetAsync("/api/diagnostics/recent?correlationId=corr-chat-001");
        diagnosticsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await diagnosticsResponse.Content.ReadFromJsonAsync<DiagnosticsQueryResponse>();

        payload.Should().NotBeNull();
        var events = payload!.Events;
        events.Select(item => item.EventType).Should().Contain("gateway.chat.requested");
        events.Select(item => item.EventType).Should().Contain("gateway.chat.completed");
        events.Should().OnlyContain(item => item.CorrelationId == "corr-chat-001");
    }

    [Fact]
    public async Task Bootstrap_complete_should_record_diagnostics_with_same_correlation_id()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: true,
                rootPath: workspace.Path),
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspace.Path
                });
            },
            useTestWorkspaceService: false);

        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/system/bootstrap-complete")
        {
            Content = JsonContent.Create(new BootstrapCompletionRequest("# identity", "# soul", "# user"))
        };
        request.Headers.Add("X-KodaClaw-Correlation-Id", "corr-bootstrap-001");

        using var response = await hosted.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var diagnosticsResponse = await hosted.Client.GetAsync("/api/diagnostics/recent?correlationId=corr-bootstrap-001");
        diagnosticsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await diagnosticsResponse.Content.ReadFromJsonAsync<DiagnosticsQueryResponse>();

        payload.Should().NotBeNull();
        payload!.Events.Should().Contain(item =>
            item.EventType == "workspace.bootstrap.completed" &&
            item.CorrelationId == "corr-bootstrap-001");
    }

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-diagnostics-flow",
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

    private static void RemoveMetricsBridge(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(d => d.ImplementationType == typeof(MetricsBridgeService));
        if (descriptor is not null)
            services.Remove(descriptor);
    }

    private static InMemoryDiagnosticsService BuildSeededDiagnosticsService(IEnumerable<DiagnosticEvent> events)
    {
        var service = new InMemoryDiagnosticsService();
        foreach (var item in events)
        {
            service.Record(item);
        }

        return service;
    }

    private static IReadOnlyList<DiagnosticEvent> CreateDiagnosticEvents(int count)
    {
        return Enumerable.Range(1, count)
            .Select(index => new DiagnosticEvent(
                Id: $"diag-{index:000}",
                Source: "gateway.chat",
                EventType: "gateway.chat.requested",
                Level: "info",
                Message: $"event-{index}",
                Timestamp: DateTimeOffset.UtcNow.AddDays(1).AddMinutes(index),
                CorrelationId: $"corr-{index:000}",
                SessionId: "session-main"))
            .ToArray();
    }
}
