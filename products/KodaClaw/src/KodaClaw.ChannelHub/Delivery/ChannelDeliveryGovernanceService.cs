using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KodaClaw.ChannelHub.Common;
using KodaClaw.Contracts.Approvals;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.Sessions;

namespace KodaClaw.ChannelHub.Delivery;

public sealed class ChannelDeliveryGovernanceService
{
    private const string DeliverySource = "channel.delivery";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IApprovalRepository _approvalRepository;
    private readonly IInboxRepository _inboxRepository;
    private readonly IDiagnosticsService? _diagnosticsService;
    private readonly ICorrelationContextAccessor? _correlationContextAccessor;

    public ChannelDeliveryGovernanceService(
        IApprovalRepository approvalRepository,
        IInboxRepository inboxRepository,
        IDiagnosticsService? diagnosticsService = null,
        ICorrelationContextAccessor? correlationContextAccessor = null)
    {
        _approvalRepository = approvalRepository ?? throw new ArgumentNullException(nameof(approvalRepository));
        _inboxRepository = inboxRepository ?? throw new ArgumentNullException(nameof(inboxRepository));
        _diagnosticsService = diagnosticsService;
        _correlationContextAccessor = correlationContextAccessor;
    }

    public async Task<ChannelDeliveryEvaluationResult> EvaluateAsync(
        ThreadBinding binding,
        DeliveryRule deliveryRule,
        ChannelOutboundDraft draft,
        CancellationToken cancellationToken = default)
    {
        ChannelHubValidation.ValidateBinding(binding);
        ArgumentNullException.ThrowIfNull(deliveryRule);
        ArgumentNullException.ThrowIfNull(draft);

        ValidateDeliveryRule(deliveryRule);
        ValidateDraft(binding, draft);

        if (deliveryRule.Mode == DeliveryMode.AutoSend)
        {
            RecordDiagnosticEvent(
                eventType: "channel_delivery.auto_send_ready",
                level: "info",
                message: $"Channel draft '{draft.DraftId}' is ready for direct delivery.",
                binding: binding,
                draft: draft,
                deliveryRule: deliveryRule,
                approvalId: null,
                inboxItemId: null);

            return new ChannelDeliveryEvaluationResult(
                Disposition: ChannelDeliveryDisposition.SendImmediately,
                DeliveryMode: deliveryRule.Mode);
        }

        var approvalId = BuildApprovalId(draft.DraftId);
        var inboxItemId = BuildInboxItemId(draft.DraftId);
        var approvalToken = BuildApprovalToken(draft.DraftId);
        var now = DateTimeOffset.UtcNow;
        var correlationId = draft.CorrelationId ?? _correlationContextAccessor?.CorrelationId;
        var payloadJson = JsonSerializer.Serialize(new
        {
            draftId = draft.DraftId,
            bindingId = binding.Id,
            connectorKind = draft.ConnectorKind,
            accountId = draft.AccountId,
            externalThreadId = draft.ExternalThreadId,
            deliveryMode = deliveryRule.Mode,
            messageText = draft.MessageText,
            mediaAttachments = draft.MediaAttachments,
            token = approvalToken,
        }, JsonOptions);

        var approvalTitle = deliveryRule.Mode == DeliveryMode.DraftApproval
            ? $"Review draft reply for {binding.ExternalThreadId}"
            : $"Approve channel delivery for {binding.ExternalThreadId}";
        var approvalSummary = $"Connector {binding.ConnectorKind} is waiting to send: {BuildPreview(draft.MessageText)}";

        await _approvalRepository.UpsertAsync(new Approval(
            Id: approvalId,
            Kind: ApprovalKind.ChannelDelivery,
            Status: ApprovalStatus.Pending,
            Title: approvalTitle,
            Summary: approvalSummary,
            Source: DeliverySource,
            RequestedAt: now,
            UpdatedAt: now,
            SessionId: binding.SessionId,
            CorrelationId: correlationId,
            InboxItemId: inboxItemId,
            PayloadJson: payloadJson), cancellationToken);

        await _inboxRepository.UpsertAsync(new InboxItem(
            Id: inboxItemId,
            Kind: InboxItemKind.ChannelUpdate,
            Status: InboxItemStatus.Open,
            Title: approvalTitle,
            Summary: approvalSummary,
            Source: DeliverySource,
            CreatedAt: now,
            UpdatedAt: now,
            RequiresAction: true,
            Route: $"/approvals/{approvalId}",
            SessionId: binding.SessionId,
            CorrelationId: correlationId,
            ApprovalId: approvalId,
            PayloadJson: payloadJson), cancellationToken);

        RecordDiagnosticEvent(
            eventType: "channel_delivery.approval_requested",
            level: "info",
            message: $"Channel draft '{draft.DraftId}' requires approval before delivery.",
            binding: binding,
            draft: draft,
            deliveryRule: deliveryRule,
            approvalId: approvalId,
            inboxItemId: inboxItemId);

        return new ChannelDeliveryEvaluationResult(
            Disposition: ChannelDeliveryDisposition.ApprovalRequired,
            DeliveryMode: deliveryRule.Mode,
            ApprovalId: approvalId,
            InboxItemId: inboxItemId,
            ApprovalToken: approvalToken);
    }

    private void RecordDiagnosticEvent(
        string eventType,
        string level,
        string message,
        ThreadBinding binding,
        ChannelOutboundDraft draft,
        DeliveryRule deliveryRule,
        string? approvalId,
        string? inboxItemId)
    {
        if (_diagnosticsService == null)
        {
            return;
        }

        _diagnosticsService.Record(new DiagnosticEvent(
            Id: $"diag-channel-delivery-{draft.DraftId}-{Guid.NewGuid():N}",
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
                ["deliveryMode"] = deliveryRule.Mode.ToString(),
                ["approvalId"] = approvalId,
                ["inboxItemId"] = inboxItemId,
            }));
    }

    private static void ValidateDeliveryRule(DeliveryRule deliveryRule)
    {
        ChannelHubValidation.RequireNonEmpty(deliveryRule.Id, nameof(deliveryRule.Id));

        if (deliveryRule.UpdatedAt == default)
        {
            throw new ArgumentException("Delivery rule updated timestamp is required.", nameof(deliveryRule));
        }
    }

    private static void ValidateDraft(ThreadBinding binding, ChannelOutboundDraft draft)
    {
        ChannelHubValidation.RequireNonEmpty(draft.DraftId, nameof(draft.DraftId));
        ChannelHubValidation.RequireNonEmpty(draft.BindingId, nameof(draft.BindingId));
        ChannelHubValidation.RequireNonEmpty(draft.AccountId, nameof(draft.AccountId));
        ChannelHubValidation.RequireNonEmpty(draft.ExternalThreadId, nameof(draft.ExternalThreadId));
        ChannelHubValidation.RequireNonEmpty(draft.MessageText, nameof(draft.MessageText));

        if (draft.CreatedAt == default)
        {
            throw new ArgumentException("Draft created timestamp is required.", nameof(draft));
        }

        if (!string.Equals(binding.Id, draft.BindingId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Draft binding id must match the target thread binding.", nameof(draft));
        }

        if (!string.Equals(binding.AccountId, draft.AccountId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Draft account id must match the target thread binding.", nameof(draft));
        }

        if (binding.ConnectorKind != draft.ConnectorKind)
        {
            throw new ArgumentException("Draft connector kind must match the target thread binding.", nameof(draft));
        }

        if (!string.Equals(binding.ExternalThreadId, draft.ExternalThreadId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Draft external thread id must match the target thread binding.", nameof(draft));
        }
    }

    public static string BuildApprovalToken(string draftId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(draftId));
        return Convert.ToHexString(bytes[..3]).ToUpperInvariant();
    }

    private static string BuildApprovalId(string draftId)
    {
        return $"channel-delivery-approval-{draftId}";
    }

    private static string BuildInboxItemId(string draftId)
    {
        return $"channel-delivery-inbox-{draftId}";
    }

    private static string BuildPreview(string text) => ChannelTextExtensions.Preview(text);
}
