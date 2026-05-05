namespace KodaClaw.Contracts.Channels;

public interface IChannelSendService
{
    Task<ChannelSendResult> SendAsync(string bindingId, string text, string? mediaId = null, string? metadataJson = null, OutboundMessageFormat format = OutboundMessageFormat.Auto, CancellationToken cancellationToken = default);

    Task<ChannelSendResult> SendReplyAsync(
        string bindingId,
        string text,
        string replyToExternalMessageId,
        string? mediaId = null,
        string? metadataJson = null,
        OutboundMessageFormat format = OutboundMessageFormat.Auto,
        CancellationToken cancellationToken = default)
        => SendAsync(bindingId, text, mediaId, metadataJson, format, cancellationToken);
}

public sealed record ChannelSendResult(
    bool Ok,
    string BindingId,
    DateTimeOffset SentAt,
    string? ExternalMessageId = null);
