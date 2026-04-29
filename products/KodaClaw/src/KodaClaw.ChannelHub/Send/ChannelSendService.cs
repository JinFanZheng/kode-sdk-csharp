using KodaClaw.ChannelHub.Delivery;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Media;
using KodaClaw.Workspace;
using KodaClaw.Workspace.Media;

namespace KodaClaw.ChannelHub.Send;

public sealed class ChannelSendService : IChannelSendService
{
    private readonly IThreadBindingRepository _threadBindingRepository;
    private readonly IChannelAccountRepository _channelAccountRepository;
    private readonly ChannelDeliveryDispatchService _dispatchService;
    private readonly IMediaStore? _mediaStore;
    private readonly IChannelSendCapture? _capture;

    public ChannelSendService(
        IThreadBindingRepository threadBindingRepository,
        IChannelAccountRepository channelAccountRepository,
        ChannelDeliveryDispatchService dispatchService,
        IMediaStore? mediaStore = null,
        IChannelSendCapture? capture = null)
    {
        _threadBindingRepository = threadBindingRepository ?? throw new ArgumentNullException(nameof(threadBindingRepository));
        _channelAccountRepository = channelAccountRepository ?? throw new ArgumentNullException(nameof(channelAccountRepository));
        _dispatchService = dispatchService ?? throw new ArgumentNullException(nameof(dispatchService));
        _mediaStore = mediaStore;
        _capture = capture;
    }

    public async Task<ChannelSendResult> SendAsync(
        string bindingId,
        string text,
        string? mediaId = null,
        string? metadataJson = null,
        OutboundMessageFormat format = OutboundMessageFormat.Auto,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bindingId))
        {
            throw new ArgumentException(
                "bindingId is required.", nameof(bindingId));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException(
                "text is required.", nameof(text));
        }

        var binding = await _threadBindingRepository.GetByIdAsync(bindingId, cancellationToken)
            ?? throw new InvalidOperationException($"Channel binding '{bindingId}' was not found.");

        var account = await _channelAccountRepository.GetByIdAsync(binding.AccountId, cancellationToken)
            ?? throw new InvalidOperationException($"Channel account '{binding.AccountId}' was not found.");

        IReadOnlyList<MediaReference>? mediaAttachments = null;
        if (!string.IsNullOrWhiteSpace(mediaId) && _mediaStore is not null)
        {
            var meta = await _mediaStore.GetMetaAsync(mediaId, cancellationToken);
            if (meta is not null)
            {
                mediaAttachments = [new MediaReference(meta.Id, meta.ContentType, DurationMs: meta.DurationMs)];
            }
        }

        await _dispatchService.SendNotificationAsync(account, binding, text, mediaAttachments, metadataJson, format, cancellationToken);
        _capture?.Record(bindingId, text);

        return new ChannelSendResult(Ok: true, BindingId: bindingId, SentAt: DateTimeOffset.UtcNow);
    }
}
