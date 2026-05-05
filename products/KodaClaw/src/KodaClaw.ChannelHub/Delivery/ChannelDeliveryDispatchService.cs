using System.Text.Json;
using KodaClaw.ChannelHub.Common;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Media;
using KodaClaw.Contracts.Sessions;

namespace KodaClaw.ChannelHub.Delivery;

public sealed class ChannelDeliveryDispatchService
{
    private const string DeliverySource = "channel.delivery";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IThreadBindingRepository _threadBindingRepository;
    private readonly IChannelAuditRepository? _channelAuditRepository;
    private readonly ChannelConnectorKindResolver _resolver;
    private readonly IDiagnosticsService? _diagnosticsService;
    private readonly ICorrelationContextAccessor? _correlationContextAccessor;

    public ChannelDeliveryDispatchService(
        IThreadBindingRepository threadBindingRepository,
        ChannelConnectorKindResolver resolver,
        IChannelAuditRepository? channelAuditRepository = null,
        IDiagnosticsService? diagnosticsService = null,
        ICorrelationContextAccessor? correlationContextAccessor = null)
    {
        _threadBindingRepository = threadBindingRepository ?? throw new ArgumentNullException(nameof(threadBindingRepository));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _channelAuditRepository = channelAuditRepository;
        _diagnosticsService = diagnosticsService;
        _correlationContextAccessor = correlationContextAccessor;
    }

    public async Task<ChannelDeliveryDispatchResult> DispatchAsync(
        ChannelAccount account,
        ThreadBinding binding,
        ChannelOutboundDraft draft,
        ChannelTurnOutcome intendedOutcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ChannelHubValidation.ValidateBinding(binding);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(intendedOutcome);

        try
        {
            await SendAsync(account, draft, cancellationToken);

            var deliveredAt = DateTimeOffset.UtcNow;
            await _threadBindingRepository.UpsertAsync(
                binding with
                {
                    UpdatedAt = deliveredAt,
                    LastOutboundAt = deliveredAt,
                    LastMessagePreview = BuildPreview(draft.MessageText),
                },
                cancellationToken);

            var deliveredOutcome = intendedOutcome with
            {
                Kind = ChannelTurnOutcomeKind.Delivered,
                Summary = string.IsNullOrWhiteSpace(intendedOutcome.Summary)
                    ? BuildPreview(draft.MessageText)
                    : intendedOutcome.Summary,
                OccurredAt = deliveredAt,
                ReplyText = draft.MessageText,
                DeliveryMode = draft.DeliveryMode,
                DraftId = draft.DraftId,
            };

            await AppendAuditAsync(
                binding,
                eventType: "delivery.sent",
                summary: BuildPreview(draft.MessageText),
                outcome: deliveredOutcome,
                cancellationToken);

            RecordDiagnosticEvent(
                eventType: "channel.delivery.sent",
                level: "info",
                message: "Channel delivery completed successfully.",
                binding: binding,
                draft: draft,
                outcome: deliveredOutcome,
                errorMessage: null);

            return new ChannelDeliveryDispatchResult(
                Succeeded: true,
                Outcome: deliveredOutcome);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or HttpRequestException)
        {
            var failedAt = DateTimeOffset.UtcNow;
            var failedOutcome = intendedOutcome with
            {
                Kind = ChannelTurnOutcomeKind.Failed,
                Summary = $"Delivery failed: {ex.Message}",
                OccurredAt = failedAt,
                ReplyText = draft.MessageText,
                DeliveryMode = draft.DeliveryMode,
                DraftId = draft.DraftId,
            };

            await AppendAuditAsync(
                binding,
                eventType: "delivery.failed",
                summary: failedOutcome.Summary,
                outcome: failedOutcome,
                cancellationToken);

            RecordDiagnosticEvent(
                eventType: "channel.delivery.failed",
                level: "error",
                message: ex.Message,
                binding: binding,
                draft: draft,
                outcome: failedOutcome,
                errorMessage: ex.Message);

            return new ChannelDeliveryDispatchResult(
                Succeeded: false,
                Outcome: failedOutcome,
                ErrorMessage: ex.Message);
        }
    }

    /// <summary>
    /// Sends a system notification message to the channel thread without tracking it as
    /// an outbound draft. Does not update ThreadBinding timestamps or append audit entries.
    /// Best-effort: callers should catch and swallow exceptions.
    /// </summary>
    public async Task<ChannelSendReceipt> SendNotificationAsync(
        ChannelAccount account,
        ThreadBinding binding,
        string text,
        IReadOnlyList<MediaReference>? mediaAttachments = null,
        string? metadataJson = null,
        OutboundMessageFormat format = OutboundMessageFormat.Auto,
        string? replyToExternalMessageId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ChannelHubValidation.ValidateBinding(binding);

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Notification text is required.", nameof(text));
        }

        var notificationDraft = new ChannelOutboundDraft(
            DraftId: $"notif-{Guid.NewGuid():N}",
            BindingId: binding.Id,
            ConnectorKind: binding.ConnectorKind,
            AccountId: binding.AccountId,
            ExternalThreadId: binding.ExternalThreadId,
            MessageText: text,
            ThreadType: binding.ThreadType,
            DeliveryMode: DeliveryMode.AutoSend,
            CreatedAt: DateTimeOffset.UtcNow,
            SessionId: binding.SessionId,
            CorrelationId: _correlationContextAccessor?.CorrelationId,
            MediaAttachments: mediaAttachments,
            MetadataJson: metadataJson,
            Format: format,
            ReplyToExternalMessageId: replyToExternalMessageId);

        return await SendWithReceiptAsync(account, notificationDraft, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SendAsync(
        ChannelAccount account,
        ChannelOutboundDraft draft,
        CancellationToken cancellationToken)
    {
        if (!_resolver.TryGet(account.ConnectorKind, out var connector))
        {
            throw new NotSupportedException(
                $"Connector '{account.ConnectorKind}' does not support channel delivery dispatch.");
        }

        await connector!.EnsureStartedAndSendAsync(account, draft, cancellationToken);
    }

    private async Task<ChannelSendReceipt> SendWithReceiptAsync(
        ChannelAccount account,
        ChannelOutboundDraft draft,
        CancellationToken cancellationToken)
    {
        if (!_resolver.TryGet(account.ConnectorKind, out var connector))
        {
            throw new NotSupportedException(
                $"Connector '{account.ConnectorKind}' does not support channel delivery dispatch.");
        }

        return await connector!.EnsureStartedAndSendWithReceiptAsync(account, draft, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the initial "🔄 思考中…" progress message and returns a receipt including the
    /// external message id (when the underlying connector surfaces one). Best-effort — callers
    /// should interpret a null ExternalMessageId as "degrade, no edits will happen this turn".
    /// </summary>
    public async Task<ChannelSendReceipt> SendProgressInitialAsync(
        ChannelAccount account,
        ThreadBinding binding,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ChannelHubValidation.ValidateBinding(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if (!_resolver.TryGet(account.ConnectorKind, out var connector))
        {
            return new ChannelSendReceipt(null, DateTimeOffset.UtcNow);
        }

        var draft = new ChannelOutboundDraft(
            DraftId: $"progress-init-{Guid.NewGuid():N}",
            BindingId: binding.Id,
            ConnectorKind: binding.ConnectorKind,
            AccountId: binding.AccountId,
            ExternalThreadId: binding.ExternalThreadId,
            MessageText: text,
            ThreadType: binding.ThreadType,
            DeliveryMode: DeliveryMode.AutoSend,
            CreatedAt: DateTimeOffset.UtcNow,
            SessionId: binding.SessionId,
            CorrelationId: _correlationContextAccessor?.CorrelationId,
            Format: OutboundMessageFormat.PlainText);

        try
        {
            return await connector!.SendWithReceiptAsync(draft, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordDiagnosticEvent(
                eventType: "channel.progress_indicator.initial_failed",
                level: "warn",
                message: ex.Message,
                binding: binding,
                draft: draft,
                outcome: null,
                errorMessage: ex.Message);
            return new ChannelSendReceipt(null, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Edits the progress message with new text. Best-effort — exceptions are logged via
    /// diagnostics and re-thrown so the indicator can track consecutive failures.
    /// </summary>
    public Task EditProgressAsync(
        ChannelAccount account,
        ThreadBinding binding,
        string externalMessageId,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ChannelHubValidation.ValidateBinding(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if (!_resolver.TryGet(account.ConnectorKind, out var connector))
        {
            throw new NotSupportedException(
                $"Connector '{account.ConnectorKind}' is not registered.");
        }

        return connector!.EditAsync(
            binding.ExternalThreadId,
            externalMessageId,
            text,
            OutboundMessageFormat.PlainText,
            cancellationToken);
    }

    private async Task AppendAuditAsync(
        ThreadBinding binding,
        string eventType,
        string summary,
        ChannelTurnOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (_channelAuditRepository is null)
        {
            return;
        }

        await _channelAuditRepository.AppendAsync(
            new ChannelAuditEntry(
                Id: $"audit-{Guid.NewGuid():N}",
                BindingId: binding.Id,
                ConnectorKind: binding.ConnectorKind,
                AccountId: binding.AccountId,
                ExternalThreadId: binding.ExternalThreadId,
                ThreadType: binding.ThreadType,
                EventType: eventType,
                CreatedAt: outcome.OccurredAt,
                SessionId: binding.SessionId,
                ApprovalId: outcome.ApprovalId,
                DeliveryMode: outcome.DeliveryMode,
                Summary: summary,
                MetadataJson: JsonSerializer.Serialize(outcome, JsonOptions)),
            cancellationToken);
    }

    private void RecordDiagnosticEvent(
        string eventType,
        string level,
        string message,
        ThreadBinding binding,
        ChannelOutboundDraft draft,
        ChannelTurnOutcome? outcome,
        string? errorMessage)
    {
        if (_diagnosticsService is null)
        {
            return;
        }

        _diagnosticsService.Record(new DiagnosticEvent(
            Id: $"diag-channel-delivery-{Guid.NewGuid():N}",
            Source: DeliverySource,
            EventType: eventType,
            Level: level,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow,
            SessionId: binding.SessionId,
            CorrelationId: draft.CorrelationId ?? _correlationContextAccessor?.CorrelationId,
            Attributes: new Dictionary<string, string?>
            {
                ["bindingId"] = binding.Id,
                ["draftId"] = draft.DraftId,
                ["connectorKind"] = draft.ConnectorKind.ToString(),
                ["accountId"] = draft.AccountId,
                ["externalThreadId"] = draft.ExternalThreadId,
                ["deliveryMode"] = draft.DeliveryMode.ToString(),
                ["approvalId"] = outcome?.ApprovalId,
                ["inboxItemId"] = outcome?.InboxItemId,
                ["outcomeKind"] = outcome?.Kind.ToString(),
                ["error"] = errorMessage,
            }));
    }

    private static string BuildPreview(string text) => ChannelTextExtensions.Preview(text);
}
