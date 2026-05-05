using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Tools;

namespace Kode.Agent.Tests.Integration;

public sealed class ToolAllowlistIntegrationTests
{
    // [Fact]
    // public async Task RunAsync_DeniedByAllowlist_DoesNotPauseAndDoesNotExecuteTool()
    // {
    //     var baseDir = Path.Combine(Path.GetTempPath(), "Kode.Agent.Tests", Guid.NewGuid().ToString("N"));
    //     Directory.CreateDirectory(baseDir);
    //
    //     var store = new JsonAgentStore(Path.Combine(baseDir, "store"));
    //     var toolRegistry = new ToolRegistry();
    //     toolRegistry.Register(new CounterTool());
    //
    //     var deps = new AgentDependencies
    //     {
    //         Store = store,
    //         ToolRegistry = toolRegistry,
    //         SandboxFactory = new LocalSandboxFactory(),
    //         ModelProvider = new StubProvider(),
    //         LoggerFactory = null
    //     };
    //
    //     CounterTool.Counter = 0;
    //
    //     var config = new AgentConfig
    //     {
    //         Model = "stub",
    //         Tools = ["count_tool"],
    //         SandboxOptions = new SandboxOptions
    //         {
    //             WorkingDirectory = Path.Combine(baseDir, "data"),
    //             EnforceBoundary = false
    //         },
    //         Permissions = new PermissionConfig
    //         {
    //             Mode = "auto",
    //             AllowTools = ["fs_read"] // exclude count_tool => deny immediately
    //         },
    //         MaxIterations = 5
    //     };
    //
    //     await using var agent = await Kode.Agent.Sdk.Core.Agent.Agent.CreateAsync("allowlist-test", config, deps);
    //     var result = await agent.RunAsync("hi");
    //
    //     Assert.True(result.Success);
    //     Assert.Equal(0, CounterTool.Counter);
    //     Assert.Equal(AgentRuntimeState.Ready, agent.RuntimeState);
    // }

    private sealed class CounterTool : ToolBase
    {
        public static int Counter;

        public override string Name => "count_tool";
        public override string Description => "Increments a counter.";
        public override object InputSchema => new { type = "object", properties = new { }, additionalProperties = false };

        public override Task<ToolResult> ExecuteAsync(object arguments, ToolContext context, CancellationToken cancellationToken = default)
        {
            Counter++;
            return Task.FromResult(ToolResult.Ok(new { ok = true }));
        }
    }

    private sealed class StubProvider : IModelProvider
    {
        private int _callCount;
        public string ProviderName => "stub";

#pragma warning disable CS1998 // Async method lacks await operators
        public async IAsyncEnumerable<StreamChunk> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _callCount++;

            if (_callCount == 1)
            {
                yield return new StreamChunk
                {
                    Type = StreamChunkType.ToolUseStart,
                    ToolUse = new ToolUseChunk { Id = "call-1", Name = "count_tool" }
                };
                yield return new StreamChunk
                {
                    Type = StreamChunkType.ToolUseInputDelta,
                    ToolUse = new ToolUseChunk { Id = "call-1", InputDelta = "{}" }
                };
                yield return new StreamChunk
                {
                    Type = StreamChunkType.ToolUseComplete,
                    ToolUse = new ToolUseChunk { Id = "call-1" }
                };
                yield return new StreamChunk
                {
                    Type = StreamChunkType.MessageStop,
                    StopReason = ModelStopReason.ToolUse,
                    Usage = new TokenUsage { InputTokens = 0, OutputTokens = 0 }
                };
                yield break;
            }

            yield return new StreamChunk { Type = StreamChunkType.TextDelta, TextDelta = "ok" };
            yield return new StreamChunk
            {
                Type = StreamChunkType.MessageStop,
                StopReason = ModelStopReason.EndTurn,
                Usage = new TokenUsage { InputTokens = 0, OutputTokens = 0 }
            };
        }

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ModelResponse
            {
                Content = [new TextContent { Text = "ok" }],
                StopReason = ModelStopReason.EndTurn,
                Usage = new TokenUsage { InputTokens = 0, OutputTokens = 0 },
                Model = request.Model
            });
        }

        public Task<bool> ValidateAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public ModelCapabilities? GetModelCapabilities(string modelId) => null;
    }
}
