using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Bootstrap;
using KodaClaw.Contracts.System;
using KodaClaw.Gateway;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Bootstrap;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class BootstrapDraftApiIntegrationTests
{
    [Fact]
    public async Task Bootstrap_draft_requires_conversation_or_seed_content()
    {
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(requiresBootstrap: true),
            configureServices: services =>
            {
                services.AddSingleton<IModelProvider>(new StubModelProvider("{}"));
                services.AddSingleton(new BootstrapDraftOptions
                {
                    Model = "bootstrap-draft-model",
                });
            },
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_DEFAULT_MODEL"] = "bootstrap-draft-model",
                    ["OPENAI_API_KEY"] = "stub-key",
                });
            });

        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.PostAsJsonAsync(
            "/api/system/bootstrap-draft",
            new BootstrapDraftRequest(Array.Empty<BootstrapDraftMessage>()));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        error.Should().NotBeNull();
        error!.Code.Should().Be("validation.bootstrap_draft_input_required");
    }

    [Fact]
    public async Task Bootstrap_draft_returns_generated_markdown_from_onboarding_conversation()
    {
        var modelProvider = new StubModelProvider(
            """
            {
              "identityMarkdown": "# Koda Identity\n\n- Name: Koda",
              "soulMarkdown": "# Koda Soul\n\n- Rule: protect local trust",
              "userMarkdown": "# User Profile\n\n- Communication style: direct",
              "summary": "Generated a conservative onboarding draft from the conversation."
            }
            """);

        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(requiresBootstrap: true),
            configureServices: services =>
            {
                services.AddSingleton<IModelProvider>(modelProvider);
                services.AddSingleton(new BootstrapDraftOptions
                {
                    Model = "bootstrap-draft-model",
                });
            });

        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.PostAsJsonAsync(
            "/api/system/bootstrap-draft",
            new BootstrapDraftRequest(
                Conversation:
                [
                    new BootstrapDraftMessage("user", "I want concise communication and strong privacy boundaries."),
                    new BootstrapDraftMessage("assistant", "I will keep local trust and reversibility as defaults."),
                ]));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<BootstrapDraftResult>();
        result.Should().NotBeNull();
        result!.IdentityMarkdown.Should().Contain("Name: Koda");
        result.SoulMarkdown.Should().Contain("protect local trust");
        result.UserMarkdown.Should().Contain("Communication style: direct");
        result.Summary.Should().Contain("Generated a conservative onboarding draft");

        modelProvider.LastRequest.Should().NotBeNull();
        modelProvider.LastRequest!.SystemPrompt.Should().Contain("[user] I want concise communication and strong privacy boundaries.");
    }

    private sealed class StubModelProvider : IModelProvider
    {
        private readonly string _responseText;

        public StubModelProvider(string responseText)
        {
            _responseText = responseText;
        }

        public string ProviderName => "stub";

        public ModelRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<StreamChunk> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            await Task.Yield();

            yield return new StreamChunk
            {
                Type = StreamChunkType.TextDelta,
                TextDelta = _responseText,
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
            LastRequest = request;
            return Task.FromResult(new ModelResponse
            {
                Content =
                [
                    new TextContent
                    {
                        Text = _responseText,
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
