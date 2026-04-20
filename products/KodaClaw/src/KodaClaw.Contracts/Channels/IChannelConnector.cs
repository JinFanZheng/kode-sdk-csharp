namespace KodaClaw.Contracts;

public interface IChannelConnector
{
    ChannelConnectorKind Kind { get; }

    Task StartAsync(
        ChannelAccount account,
        Func<ChannelEventEnvelope, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken = default);

    Task StopAsync(string accountId, CancellationToken cancellationToken = default);

    Task SendAsync(ChannelOutboundDraft draft, CancellationToken cancellationToken = default);

    Task EnsureStartedAndSendAsync(
        ChannelAccount account,
        ChannelOutboundDraft draft,
        CancellationToken cancellationToken = default)
        => SendAsync(draft, cancellationToken);
}
