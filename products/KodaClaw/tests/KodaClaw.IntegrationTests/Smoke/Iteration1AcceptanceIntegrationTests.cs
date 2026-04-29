using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Bootstrap;
using KodaClaw.Contracts.Chat;
using KodaClaw.Contracts.Settings;
using KodaClaw.IntegrationTests.Gateway;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Smoke;

public sealed class Iteration1AcceptanceIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Iteration_1_acceptance_should_cover_bootstrap_chat_close_and_resume()
    {
        using var workspace = new TempWorkspaceRoot();

        string createdSessionId;
        await using (var hosted = await StartGatewayAsync(workspace.Path))
        {
            hosted.Client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", "test-token");

            var initialSnapshot = await hosted.Client.GetFromJsonAsync<BootstrapStateResponse>("/api/system/bootstrap-state");
            initialSnapshot.Should().NotBeNull();
            initialSnapshot!.RequiresBootstrap.Should().BeTrue();
            initialSnapshot.Mode.Should().Be(AppMode.Bootstrap);

            var bootstrapResponse = await hosted.Client.PostAsJsonAsync(
                "/api/system/bootstrap-complete",
                new BootstrapCompletionRequest("# identity", "# soul", "# user", ArchiveBootstrapFile: true));
            bootstrapResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var postBootstrapSnapshot = await hosted.Client.GetFromJsonAsync<BootstrapStateResponse>("/api/system/bootstrap-state");
            postBootstrapSnapshot.Should().NotBeNull();
            postBootstrapSnapshot!.RequiresBootstrap.Should().BeFalse();
            postBootstrapSnapshot.Mode.Should().Be(AppMode.Normal);

            using var chatResponse = await hosted.Client.PostAsJsonAsync(
                "/api/chat/stream",
                new ChatStreamRequest("hello from acceptance"));
            chatResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var frames = ParseServerSentEvents(await chatResponse.Content.ReadAsStringAsync());
            frames.Select(frame => frame.EventName).Should().ContainInOrder("text_chunk", "done");

            var payloads = frames
                .Select(frame => JsonSerializer.Deserialize<ChatStreamEvent>(frame.Data, JsonOptions))
                .ToArray();
            payloads.Should().NotContainNulls();
            payloads[0]!.Delta.Should().Be("stub");
            createdSessionId = payloads.Select(item => item!.SessionId).Distinct().Single();
        }

        await using (var resumedHosted = await StartGatewayAsync(workspace.Path))
        {
            resumedHosted.Client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", "test-token");

            var resumedSnapshot = await resumedHosted.Client.GetFromJsonAsync<BootstrapStateResponse>("/api/system/bootstrap-state");
            resumedSnapshot.Should().NotBeNull();
            resumedSnapshot!.RequiresBootstrap.Should().BeFalse();
            resumedSnapshot.Mode.Should().Be(AppMode.Normal);
            resumedSnapshot.ActiveMainSessionId.Should().Be(createdSessionId);

            using var resumedChatResponse = await resumedHosted.Client.PostAsJsonAsync(
                "/api/chat/stream",
                new ChatStreamRequest("hello after restart"));
            resumedChatResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            var resumedFrames = ParseServerSentEvents(await resumedChatResponse.Content.ReadAsStringAsync());
            resumedFrames.Select(frame => frame.EventName).Should().ContainInOrder("text_chunk", "done");

            var resumedPayloads = resumedFrames
                .Select(frame => JsonSerializer.Deserialize<ChatStreamEvent>(frame.Data, JsonOptions))
                .ToArray();
            resumedPayloads.Should().NotContainNulls();
            resumedPayloads.Select(item => item!.SessionId).Distinct().Should().Equal(createdSessionId);
        }
    }

    private static Task<HostedGateway> StartGatewayAsync(string workspaceRoot)
    {
        return HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: true,
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
                    ["KODACLAW_DEFAULT_MODEL"] = "gpt-4o-mini",
                    ["OPENAI_API_KEY"] = "test-key"
                });
            },
            useTestWorkspaceService: false);
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

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-iteration1-acceptance",
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
