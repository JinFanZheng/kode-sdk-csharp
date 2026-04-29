using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Bootstrap;
using KodaClaw.Contracts.Chat;
using KodaClaw.Contracts.Secrets;
using KodaClaw.Contracts.Settings;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class GatewayAuthIntegrationTests
{
    [Fact]
    public async Task Health_endpoint_should_be_public_and_return_typed_contract()
    {
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: CreateSnapshot(requiresBootstrap: true));

        var response = await hosted.Client.GetAsync("/api/system/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<SystemHealthResponse>();
        payload.Should().NotBeNull();
        payload!.Name.Should().Be("KodaClaw Gateway");
        payload.Status.Should().Be("healthy");
    }

    [Fact]
    public async Task Bootstrap_state_should_return_401_without_token()
    {
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: CreateSnapshot(requiresBootstrap: true));

        var response = await hosted.Client.GetAsync("/api/system/bootstrap-state");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Bootstrap_state_should_return_snapshot_when_token_matches()
    {
        var snapshot = CreateSnapshot(requiresBootstrap: false);
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: snapshot);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/system/bootstrap-state");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<BootstrapStateResponse>();
        payload.Should().NotBeNull();
        payload!.WorkspaceRootPath.Should().Be(snapshot.RootPath);
        payload.WorkspaceVersion.Should().Be(snapshot.WorkspaceVersion);
        payload.WorkspaceInitialized.Should().Be(snapshot.WorkspaceInitialized);
        payload.RequiresBootstrap.Should().Be(snapshot.RequiresBootstrap);
        payload.ActiveMainSessionId.Should().Be(snapshot.ActiveMainSessionId);
        payload.Mode.Should().Be(AppMode.Normal);
    }

    [Fact]
    public async Task Bootstrap_state_should_accept_secret_store_gateway_token_when_secret_ref_configured()
    {
        var secretRef = new SecretRef("memory", "gateway", "auth-token");
        var snapshot = CreateSnapshot(requiresBootstrap: false);
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: string.Empty,
            workspaceSnapshot: snapshot,
            configureServices: services =>
            {
                services.AddSingleton<ISecretStore>(new FakeSecretStore(new Dictionary<string, string?>
                {
                    [secretRef.ToReferenceString()] = "secret-store-token"
                }));
            },
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_GATEWAY_TOKEN_SECRET_REF"] = secretRef.ToReferenceString()
                });
            });
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "secret-store-token");

        var response = await hosted.Client.GetAsync("/api/system/bootstrap-state");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Bootstrap_state_should_fallback_to_legacy_gateway_token_when_secret_ref_does_not_resolve()
    {
        var secretRef = new SecretRef("memory", "gateway", "missing-token");
        var snapshot = CreateSnapshot(requiresBootstrap: false);
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "legacy-token",
            workspaceSnapshot: snapshot,
            configureServices: services =>
            {
                services.AddSingleton<ISecretStore>(new FakeSecretStore(new Dictionary<string, string?>()));
            },
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_GATEWAY_TOKEN_SECRET_REF"] = secretRef.ToReferenceString()
                });
            });
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "legacy-token");

        var response = await hosted.Client.GetAsync("/api/system/bootstrap-state");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_endpoint_should_allow_loopback_cors_origin_and_expose_correlation_header()
    {
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: CreateSnapshot(requiresBootstrap: false));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/system/health");
        request.Headers.Add("Origin", "http://127.0.0.1:4173");

        var response = await hosted.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle().Which.Should().Be("http://127.0.0.1:4173");
        response.Headers.GetValues("Access-Control-Expose-Headers").Should().ContainSingle().Which.Should().Contain("X-KodaClaw-Correlation-Id");
    }

    [Fact]
    public async Task Bootstrap_state_preflight_should_allow_loopback_origin_and_authorization_header()
    {
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: CreateSnapshot(requiresBootstrap: false));

        using var request = CreateCorsPreflightRequest("/api/system/bootstrap-state", "http://127.0.0.1:4173");

        var response = await hosted.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle().Which.Should().Be("http://127.0.0.1:4173");
        string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")).Should().ContainEquivalentOf("authorization");
        string.Join(",", response.Headers.GetValues("Access-Control-Allow-Methods")).Should().Contain("GET");
    }

    [Fact]
    public async Task Bootstrap_state_preflight_should_allow_null_origin_for_packaged_desktop_renderer()
    {
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: CreateSnapshot(requiresBootstrap: false));

        using var request = CreateCorsPreflightRequest("/api/system/bootstrap-state", "null");

        var response = await hosted.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle().Which.Should().Be("null");
    }

    [Fact]
    public async Task Health_endpoint_should_allow_configured_non_loopback_cors_origin()
    {
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: CreateSnapshot(requiresBootstrap: false),
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_CORS_ALLOWED_ORIGINS"] = "https://console.koda.test"
                });
            });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/system/health");
        request.Headers.Add("Origin", "https://console.koda.test");

        var response = await hosted.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle().Which.Should().Be("https://console.koda.test");
    }

    [Fact]
    public async Task Health_endpoint_should_not_allow_untrusted_cors_origin()
    {
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: CreateSnapshot(requiresBootstrap: false));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/system/health");
        request.Headers.Add("Origin", "https://example.com");

        var response = await hosted.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    private static HttpRequestMessage CreateCorsPreflightRequest(string path, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        return request;
    }

    internal static WorkspaceSnapshot CreateSnapshot(bool requiresBootstrap, string? rootPath = null)
    {
        return new WorkspaceSnapshot(
            RootPath: rootPath ?? "/tmp/kodaclaw-workspace",
            WorkspaceVersion: 1,
            WorkspaceInitialized: true,
            RequiresBootstrap: requiresBootstrap,
            ActiveMainSessionId: "main-001",
            DeviceId: "device-001");
    }
}

internal sealed class HostedGateway : IAsyncDisposable
{
    private readonly WebApplication _app;

    private HostedGateway(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<HostedGateway> StartAsync(
        string gatewayToken,
        WorkspaceSnapshot workspaceSnapshot,
        Action<IServiceCollection>? configureServices = null,
        Action<IConfigurationBuilder>? configureConfiguration = null,
        bool useTestWorkspaceService = true)
    {
        var port = FindFreePort();
        var app = GatewayApp.Build(
            configureServices: services =>
            {
                if (useTestWorkspaceService)
                {
                    services.AddSingleton<IWorkspaceService>(new TestWorkspaceService(workspaceSnapshot));
                }

                configureServices?.Invoke(services);
            },
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_GATEWAY_TOKEN"] = gatewayToken,
                    ["KODACLAW_STARTUP_REPAIR_ENABLED"] = "false",
                });
                configureConfiguration?.Invoke(configuration);
            });

        var baseAddress = new Uri($"http://127.0.0.1:{port}");
        app.Urls.Add(baseAddress.ToString());
        await app.StartAsync();

        var client = new HttpClient
        {
            BaseAddress = baseAddress
        };

        return new HostedGateway(app, client);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

public sealed class GatewayChatStreamIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Chat_stream_should_return_401_without_token()
    {
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(requiresBootstrap: false),
            configureServices: services =>
            {
                services.AddSingleton<IChatSessionService>(
                    new FakeChatSessionService(Enumerable.Empty<ChatStreamEvent>()));
            });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream")
        {
            Content = JsonContent.Create(new { message = "hello" })
        };

        var response = await hosted.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Chat_stream_should_stream_sse_events()
    {
        var chatEvents = new[]
        {
            new ChatStreamEvent("text_chunk", "session-001", Sequence: 1, Delta: "hello"),
            new ChatStreamEvent("text_chunk", "session-001", Sequence: 2, Delta: "world"),
            new ChatStreamEvent("done", "session-001", Reason: "completed")
        };

        var chatService = new FakeChatSessionService(chatEvents);

        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(requiresBootstrap: false),
            configureServices: services =>
            {
                services.AddSingleton<IChatSessionService>(chatService);
            });

        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.PostAsJsonAsync("/api/chat/stream", new { message = "hello" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");

        var body = await response.Content.ReadAsStringAsync();
        var frames = ParseServerSentEvents(body);
        frames.Select(frame => frame.EventName).Should().ContainInOrder("text_chunk", "text_chunk", "done");

        var payloads = frames
            .Select(frame => JsonSerializer.Deserialize<ChatStreamEvent>(frame.Data, JsonOptions))
            .ToArray();

        payloads.Should().NotContainNulls();
        payloads[0]!.Type.Should().Be("text_chunk");
        payloads[0]!.Delta.Should().Be("hello");
        payloads[1]!.Delta.Should().Be("world");
        payloads[2]!.Type.Should().Be("done");
        payloads[2]!.Reason.Should().Be("completed");
    }

    [Fact]
    public async Task Chat_stream_should_return_json_error_when_message_missing()
    {
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(requiresBootstrap: false),
            configureServices: services =>
            {
                services.AddSingleton<IChatSessionService>(
                    new FakeChatSessionService(Enumerable.Empty<ChatStreamEvent>()));
            });

        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        using var response = await hosted.Client.PostAsJsonAsync("/api/chat/stream", new { message = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        payload.Should().NotBeNull();
        payload!.Code.Should().Be("validation.message_required");
    }

    private static IReadOnlyList<SseFrame> ParseServerSentEvents(string body)
    {
        return body
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(chunk =>
            {
                var lines = chunk
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var eventName = lines.Single(line => line.StartsWith("event: ", StringComparison.Ordinal))["event: ".Length..];
                var data = lines.Single(line => line.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
                return new SseFrame(eventName, data);
            })
            .ToArray();
    }

    private sealed record SseFrame(string EventName, string Data);
}

internal sealed class FakeChatSessionService : IChatSessionService
{
    private readonly ChatStreamEvent[] _events;

    public FakeChatSessionService(IEnumerable<ChatStreamEvent> events)
    {
        _events = events.ToArray();
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamMainSessionAsync(
        ChatStreamRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var chatEvent in _events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return chatEvent;
        }
    }
}

internal sealed class TestWorkspaceService : IWorkspaceService
{
    private readonly WorkspaceSnapshot _snapshot;

    public TestWorkspaceService(WorkspaceSnapshot snapshot)
    {
        _snapshot = snapshot;
    }

    public string RootPath => _snapshot.RootPath;

    public Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_snapshot);
    }

    public Task<WorkspaceSnapshot> EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_snapshot);
    }

    public Task<WorkspaceAppConfig> LoadAppConfigAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new WorkspaceAppConfig
        {
            WorkspaceVersion = _snapshot.WorkspaceVersion,
            BootstrapCompleted = !_snapshot.RequiresBootstrap,
            ActiveMainSessionId = _snapshot.ActiveMainSessionId
        });
    }

    public Task SaveAppConfigAsync(WorkspaceAppConfig appConfig, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public string GetSessionDirectory(string sessionId)
    {
        return Path.Combine(_snapshot.RootPath, "sessions", sessionId);
    }

    public IReadOnlyList<string> GetSkillsPaths() => [];

    public Task<WorkspaceMcpConfig> ReadMcpConfigAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new WorkspaceMcpConfig());

    public Task SaveMcpConfigAsync(WorkspaceMcpConfig config, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<GatewayConfig> ReadGatewayConfigAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new GatewayConfig());

    public Task SaveGatewayConfigAsync(GatewayConfig config, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

        public Task<bool> TryCommitWorkspaceAsync(string message, CancellationToken cancellationToken = default) => Task.FromResult(false);

}

internal sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, string?> _values;

    public FakeSecretStore(Dictionary<string, string?> values)
    {
        _values = values;
    }

    public Task<string?> GetAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
    {
        _values.TryGetValue(secretRef.ToReferenceString(), out var value);
        return Task.FromResult(value);
    }

    public Task UpsertAsync(SecretRef secretRef, string secretValue, CancellationToken cancellationToken = default)
    {
        _values[secretRef.ToReferenceString()] = secretValue;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
    {
        _values.Remove(secretRef.ToReferenceString());
        return Task.CompletedTask;
    }

    public Task<SecretDescriptor> DescribeAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new SecretDescriptor(
            secretRef,
            Exists: _values.ContainsKey(secretRef.ToReferenceString()),
            IsReadOnly: false,
            StorageDisplayName: "Fake Secret Store"));
    }
}
