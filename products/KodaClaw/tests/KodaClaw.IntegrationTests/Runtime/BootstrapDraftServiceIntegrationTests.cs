using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Bootstrap;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Bootstrap;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Xunit;

namespace KodaClaw.IntegrationTests.Runtime;

public sealed class BootstrapDraftServiceIntegrationTests
{
    [Fact]
    public async Task Generate_draft_should_send_bootstrap_transcript_and_parse_json_response()
    {
        var modelProvider = new CapturingModelProvider(
            """
            {
              "identityMarkdown": "# Koda Identity\n\n- Name: Koda",
              "soulMarkdown": "# Koda Soul\n\n- Rule: protect trust",
              "userMarkdown": "# User Profile\n\n- Working style: direct",
              "summary": "Captured core identity, soul, and user preferences from onboarding."
            }
            """);
        var service = new BootstrapDraftService(
            modelProvider,
            new BootstrapDraftOptions
            {
                Model = "bootstrap-capture-model",
            });

        var result = await service.GenerateDraftAsync(
            new BootstrapDraftRequest(
                Conversation:
                [
                    new BootstrapDraftMessage("user", "I am your long-term user and I prefer direct communication."),
                    new BootstrapDraftMessage("assistant", "I will keep local data boundaries explicit and conservative."),
                ],
                IdentityMarkdown: "# Koda Identity\n\n- Name: draft",
                SoulMarkdown: null,
                UserMarkdown: null));

        result.IdentityMarkdown.Should().Contain("Name: Koda");
        result.SoulMarkdown.Should().Contain("protect trust");
        result.UserMarkdown.Should().Contain("Working style: direct");
        result.Summary.Should().Contain("Captured core identity");

        modelProvider.LastRequest.Should().NotBeNull();
        modelProvider.LastRequest!.SystemPrompt.Should().Contain("Id: Bootstrap");
        modelProvider.LastRequest.SystemPrompt.Should().Contain("[user] I am your long-term user and I prefer direct communication.");
        modelProvider.LastRequest.SystemPrompt.Should().Contain("[assistant] I will keep local data boundaries explicit and conservative.");
        modelProvider.LastRequest.SystemPrompt.Should().Contain("# Koda Identity");
    }

    [Fact]
    public async Task Generate_draft_should_throw_when_request_has_no_conversation_or_seed_content()
    {
        var service = new BootstrapDraftService(
            new CapturingModelProvider("{}"),
            new BootstrapDraftOptions
            {
                Model = "bootstrap-capture-model",
            });

        var action = () => service.GenerateDraftAsync(new BootstrapDraftRequest(Array.Empty<BootstrapDraftMessage>()));

        await action.Should().ThrowAsync<ArgumentException>();
    }

    private sealed class CapturingModelProvider : IModelProvider
    {
        private readonly string _responseText;

        public CapturingModelProvider(string responseText)
        {
            _responseText = responseText;
        }

        public string ProviderName => "capturing";

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
