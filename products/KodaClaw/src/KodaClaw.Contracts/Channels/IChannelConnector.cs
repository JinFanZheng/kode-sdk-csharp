namespace KodaClaw.Contracts.Channels;

public interface IChannelConnector
{
    ChannelConnectorKind Kind { get; }

    bool SupportsEdit => false;

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

    async Task<ChannelSendReceipt> SendWithReceiptAsync(
        ChannelOutboundDraft draft,
        CancellationToken cancellationToken = default)
    {
        await SendAsync(draft, cancellationToken).ConfigureAwait(false);
        return new ChannelSendReceipt(null, DateTimeOffset.UtcNow);
    }

    Task EditAsync(
        string externalThreadId,
        string externalMessageId,
        string text,
        OutboundMessageFormat format,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"Connector '{Kind}' does not support editing messages.");
}
