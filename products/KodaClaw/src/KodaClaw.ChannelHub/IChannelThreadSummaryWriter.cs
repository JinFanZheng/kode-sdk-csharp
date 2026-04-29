using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;

namespace KodaClaw.ChannelHub;

public interface IChannelThreadSummaryWriter
{
    Task WriteAsync(
        ThreadBinding binding,
        ChannelTurnOutcome outcome,
        CancellationToken cancellationToken = default);
}
