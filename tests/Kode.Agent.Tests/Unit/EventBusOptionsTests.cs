using FluentAssertions;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Events;
using Xunit;

namespace Kode.Agent.Tests.Unit;

public sealed class EventBusOptionsTests
{
    private static StateChangedEvent MakeEvent() => new()
    {
        Type = "state_changed",
        State = AgentRuntimeState.Working
    };

    [Fact]
    public void Ctor_RejectsNonPositiveCapacity()
    {
        var act = () => new EventBus(null, null, null,
            new EventBusOptions { SubscriberChannelCapacity = 0 });
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task DroppedEventCount_IncrementsWhenChannelFullDropsOldest()
    {
        // Tiny capacity so any unread burst triggers drops.
        var bus = new EventBus(null, null, null,
            new EventBusOptions { SubscriberChannelCapacity = 2 });
        await using var _ = bus;

        using var cts = new CancellationTokenSource();
        var iter = bus.SubscribeAsync(EventChannel.Monitor, cancellationToken: cts.Token).GetAsyncEnumerator();

        // Kick off MoveNextAsync first so the async iterator body runs and CreateSubscription registers.
        // Then give it a beat to settle, then emit the first event which will be received.
        var firstMove = iter.MoveNextAsync();
        await Task.Delay(50);
        bus.EmitMonitor(MakeEvent());
        (await firstMove).Should().BeTrue();

        bus.GetDroppedEventCount().Should().Be(0);

        // Don't read anymore — channel has capacity 2, so 10 emits should drop ≥ 8.
        for (var i = 0; i < 10; i++)
        {
            bus.EmitMonitor(MakeEvent());
        }

        await Task.Delay(30);

        bus.GetDroppedEventCount().Should().BeGreaterThan(0,
            because: "with capacity 2 and 10 emits to an unread subscriber, DropOldest must drop ≥ 8");

        await cts.CancelAsync();
        try { await iter.DisposeAsync(); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task DroppedEventCount_StartsAtZero()
    {
        await using var bus = new EventBus();
        bus.GetDroppedEventCount().Should().Be(0);
    }
}
