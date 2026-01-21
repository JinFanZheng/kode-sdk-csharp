using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Events;
using Kode.Agent.Sdk.Core.Hooks;
using System.Text.Json;
using Xunit;

namespace Kode.Agent.Tests.Integration;

/// <summary>
/// Integration tests for the event bus and hook system working together.
/// </summary>
public class EventBusHooksIntegrationTests : IAsyncDisposable
{
    private readonly EventBus _eventBus;
    private readonly HookManager _hookManager;

    public EventBusHooksIntegrationTests()
    {
        _eventBus = new EventBus();
        _hookManager = new HookManager();
    }

    public async ValueTask DisposeAsync()
    {
        await _eventBus.DisposeAsync();
    }

    [Fact]
    public async Task EventBus_EmitsEventsToSubscribers()
    {
        // Arrange
        var receivedEvents = new List<EventEnvelope>();
        var tcs = new TaskCompletionSource<bool>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Start background subscriber
        var subscribeTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var envelope in _eventBus.SubscribeAsync(EventChannel.Progress, cancellationToken: cts.Token))
                {
                    receivedEvents.Add(envelope);
                    if (receivedEvents.Count >= 3)
                    {
                        tcs.TrySetResult(true);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
        });

        // Give time for subscription to start
        await Task.Delay(200);

        // Act - Emit multiple events
        _eventBus.EmitProgress(new TextChunkEvent { Type = "text_chunk", Step = 0, Delta = "First" });
        _eventBus.EmitProgress(new TextChunkEvent { Type = "text_chunk", Step = 0, Delta = "Second" });
        _eventBus.EmitProgress(new TextChunkEvent { Type = "text_chunk", Step = 0, Delta = "Third" });

        // Wait for subscriber to receive events or timeout
        var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        cts.Cancel();

        // Ensure subscription task completes
        try { await subscribeTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }

        // Assert
        Assert.True(receivedEvents.Count >= 1, $"Expected at least 1 event, got {receivedEvents.Count}");
    }

    [Fact]
    public async Task HookManager_PreToolUse_CanDenyToolCall()
    {
        // Arrange
        var deniedToolNames = new List<string>();
        
        var hooks = new Hooks
        {
            PreToolUse = (call, ctx) =>
            {
                if (call.Name == "dangerous_tool")
                {
                    deniedToolNames.Add(call.Name);
                    return Task.FromResult<HookDecision?>(HookDecision.Deny("Dangerous tool blocked"));
                }
                return Task.FromResult<HookDecision?>(HookDecision.Allow());
            }
        };
        
        _hookManager.Register(hooks);

        // Act
        var dangerousCall = new ToolCall("id1", "dangerous_tool", JsonDocument.Parse("{}").RootElement);
        var safeCall = new ToolCall("id2", "safe_tool", JsonDocument.Parse("{}").RootElement);
        
        var dangerousDecision = await _hookManager.RunPreToolUseAsync(dangerousCall, CreateToolContext());
        var safeDecision = await _hookManager.RunPreToolUseAsync(safeCall, CreateToolContext());

        // Assert
        Assert.NotNull(dangerousDecision);
        Assert.IsType<DenyDecision>(dangerousDecision);
        Assert.Equal("Dangerous tool blocked", ((DenyDecision)dangerousDecision).Reason);
        
        Assert.NotNull(safeDecision);
        Assert.IsType<AllowDecision>(safeDecision);
        
        Assert.Single(deniedToolNames);
        Assert.Equal("dangerous_tool", deniedToolNames[0]);
    }

    [Fact]
    public void EventBus_MultiChannelEmit_WorksCorrectly()
    {
        // Act
        var progressEnvelope = _eventBus.EmitProgress(new TextChunkEvent { Type = "text_chunk", Step = 0, Delta = "Hello" });
        var controlEnvelope = _eventBus.EmitControl(new PermissionRequiredEvent 
        { 
            Type = "permission_required",
            Call = new ToolCallSnapshot
            {
                Id = "call1",
                Name = "fs_write",
                State = ToolCallState.ApprovalRequired,
                Approval = new ToolCallApproval { Required = true }
            }
        });
        var monitorEnvelope = _eventBus.EmitMonitor(new StateChangedEvent
        {
            Type = "state_changed",
            State = AgentRuntimeState.Working
        });

        // Assert - Each channel gets events with incrementing cursors
        Assert.Equal("progress", progressEnvelope.Event.Channel);
        Assert.Equal("control", controlEnvelope.Event.Channel);
        Assert.Equal("monitor", monitorEnvelope.Event.Channel);
        
        Assert.True(controlEnvelope.Cursor > progressEnvelope.Cursor);
        Assert.True(monitorEnvelope.Cursor > controlEnvelope.Cursor);
    }

    [Fact]
    public void EventBus_CursorIncrements_WithEachEmit()
    {
        // Act
        var env1 = _eventBus.EmitProgress(new TextChunkEvent { Type = "text_chunk", Step = 0, Delta = "1" });
        var env2 = _eventBus.EmitProgress(new TextChunkEvent { Type = "text_chunk", Step = 0, Delta = "2" });
        var env3 = _eventBus.EmitProgress(new TextChunkEvent { Type = "text_chunk", Step = 0, Delta = "3" });

        // Assert
        Assert.True(env2.Cursor > env1.Cursor);
        Assert.True(env3.Cursor > env2.Cursor);
    }

    [Fact]
    public async Task HookManager_MultipleHooks_RunInOrder()
    {
        // Arrange
        var executionOrder = new List<int>();
        
        var hooks1 = new Hooks
        {
            PreToolUse = (call, ctx) =>
            {
                executionOrder.Add(1);
                return Task.FromResult<HookDecision?>(null); // Continue to next hook
            }
        };
        
        var hooks2 = new Hooks
        {
            PreToolUse = (call, ctx) =>
            {
                executionOrder.Add(2);
                return Task.FromResult<HookDecision?>(null);
            }
        };
        
        var hooks3 = new Hooks
        {
            PreToolUse = (call, ctx) =>
            {
                executionOrder.Add(3);
                return Task.FromResult<HookDecision?>(HookDecision.Allow());
            }
        };

        _hookManager.Register(hooks1);
        _hookManager.Register(hooks2);
        _hookManager.Register(hooks3);

        var call = new ToolCall("id", "test", JsonDocument.Parse("{}").RootElement);

        // Act
        await _hookManager.RunPreToolUseAsync(call, CreateToolContext());

        // Assert
        Assert.Equal([1, 2, 3], executionOrder);
    }

    [Fact]
    public void EventBus_Bookmark_ContainsValidData()
    {
        // Act
        var envelope = _eventBus.EmitProgress(new TextChunkEvent { Type = "text_chunk", Step = 0, Delta = "Test" });

        // Assert
        Assert.True(envelope.Bookmark.Seq >= 0);
        Assert.True(envelope.Bookmark.Timestamp > 0);
    }

    private ToolContext CreateToolContext()
    {
        return new ToolContext
        {
            AgentId = "test-agent",
            CallId = "test-call",
            Sandbox = null!,
            CancellationToken = CancellationToken.None
        };
    }
}
