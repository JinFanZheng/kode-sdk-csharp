using System.Text.Json;
using KodaClaw.ChannelHub.Common;
using KodaClaw.Contracts.Approvals;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.Media;
using KodaClaw.Contracts.Sessions;

namespace KodaClaw.ChannelHub.Delivery;

public sealed class ChannelDeliveryApprovalService
{
    private const string DeliverySource = "channel.delivery";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IApprovalRepository _approvalRepository;
    private readonly IInboxRepository _inboxRepository;
    private readonly IChannelAccountRepository _channelAccountRepository;
    private readonly IThreadBindingRepository _threadBindingRepository;
    private readonly IChannelAuditRepository? _channelAuditRepository;
    private readonly ChannelDeliveryDispatchService _deliveryDispatchService;
    private readonly IDiagnosticsService? _diagnosticsService;
    private readonly ICorrelationContextAccessor? _correlationContextAccessor;

    public ChannelDeliveryApprovalService(
        IApprovalRepository approvalRepository,
        IInboxRepository inboxRepository,
        IChannelAccountRepository channelAccountRepository,
        IThreadBindingRepository threadBindingRepository,
        ChannelDeliveryDispatchService deliveryDispatchService,
        IChannelAuditRepository? channelAuditRepository = null,
        IDiagnosticsService? diagnosticsService = null,
        ICorrelationContextAccessor? correlationContextAccessor = null)
    {
        _approvalRepository = approvalRepository ?? throw new ArgumentNullException(nameof(approvalRepository));
        _inboxRepository = inboxRepository ?? throw new ArgumentNullException(nameof(inboxRepository));
        _channelAccountRepository = channelAccountRepository ?? throw new ArgumentNullException(nameof(channelAccountRepository));
        _threadBindingRepository = threadBindingRepository ?? throw new ArgumentNullException(nameof(threadBindingRepository));
        _deliveryDispatchService = deliveryDispatchService ?? throw new ArgumentNullException(nameof(deliveryDispatchService));
        _channelAuditRepository = channelAuditRepository;
        _diagnosticsService = diagnosticsService;
        _correlationContextAccessor = correlationContextAccessor;
    }

    public Task<ChannelDeliveryApprovalDispatchResult> ApproveAsync(
        Approval approval,
        string? note,
        CancellationToken cancellationToken = default)
    {
        return HandleDecisionAsync(approval, approve: true, note, cancellationToken);
    }

    public Task<ChannelDeliveryApprovalDispatchResult> RejectAsync(
        Approval approval,
        string? note,
        CancellationToken cancellationToken = default)
    {
        return HandleDecisionAsync(approval, approve: false, note, cancellationToken);
    }

    private async Task<ChannelDeliveryApprovalDispatchResult> HandleDecisionAsync(
        Approval approval,
        bool approve,
        string? note,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);

        if (approval.Kind != ApprovalKind.ChannelDelivery)
        {
            return new ChannelDeliveryApprovalDispatchResult(
                ChannelDeliveryApprovalDispatchStatus.InvalidApproval,
                approval,
                "Approval is not a channel delivery approval.");
        }

        if (approval.Status != ApprovalStatus.Pending)
        {
            return new ChannelDeliveryApprovalDispatchResult(
                ChannelDeliveryApprovalDispatchStatus.NotPending,
                approval,
                $"Approval is already {approval.Status}.");
        }

        StoredChannelDeliveryPayload payload;
        ThreadBinding binding;
        ChannelAccount account;

        try
        {
            payload = ParsePayload(approval);
            binding = await LoadBindingAsync(payload.BindingId, cancellationToken);
            account = await LoadAccountAsync(payload.AccountId, cancellationToken);
            ValidatePayloadAgainstBindingAndAccount(payload, binding, account);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            RecordDiagnosticEvent(
                eventType: "channel.delivery.approval_invalid",
                level: "error",
                message: ex.Message,
                approval: approval,
                binding: null,
                payload: null,
                account: null);

            return new ChannelDeliveryApprovalDispatchResult(
                ChannelDeliveryApprovalDispatchStatus.InvalidApproval,
                approval,
                ex.Message);
        }

        var status = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        var now = DateTimeOffset.UtcNow;
        var transitioned = await _approvalRepository.TransitionAsync(
            approval.Id,
            status,
            now,
            decidedBy: "api",
            decisionNote: note,
            cancellationToken);

        if (!transitioned)
        {
            var reloaded = await _approvalRepository.GetByIdAsync(approval.Id, cancellationToken);
            return reloaded is null
                ? new ChannelDeliveryApprovalDispatchResult(
                    ChannelDeliveryApprovalDispatchStatus.NotFound,
                    Message: "Approval was not found.")
                : new ChannelDeliveryApprovalDispatchResult(
                    reloaded.Status == ApprovalStatus.Pending
                        ? ChannelDeliveryApprovalDispatchStatus.DeliveryFailed
                        : ChannelDeliveryApprovalDispatchStatus.NotPending,
                    reloaded,
                    reloaded.Status == ApprovalStatus.Pending
                        ? "Timed out waiting for channel delivery approval decision to persist."
                        : $"Approval is already {reloaded.Status}.");
        }

        if (!string.IsNullOrWhiteSpace(approval.InboxItemId))
        {
            await _inboxRepository.UpdateStatusAsync(
                approval.InboxItemId,
                InboxItemStatus.Resolved,
                now,
                resolvedAt: now,
                cancellationToken: cancellationToken);
        }

        var decidedApproval = await _approvalRepository.GetByIdAsync(approval.Id, cancellationToken)
            ?? approval with
            {
                Status = status,
                UpdatedAt = now,
                DecidedAt = now,
                DecidedBy = "api",
                DecisionNote = note,
            };

        await AppendAuditAsync(
            binding,
            payload,
            decidedApproval,
            eventType: approve ? "approval.approved" : "approval.rejected",
            summary: BuildDecisionSummary(approve, payload.MessageText, note),
            createdAt: now,
            outcome: approve
                ? null
                : new ChannelTurnOutcome(
                    Kind: ChannelTurnOutcomeKind.NoAction,
                    Summary: BuildDecisionSummary(approved: false, payload.MessageText, note),
                    OccurredAt: now,
                    ReplyText: payload.MessageText,
                    DeliveryMode: payload.DeliveryMode,
                    ApprovalId: approval.Id,
                    InboxItemId: approval.InboxItemId,
                    DraftId: payload.DraftId,
                    ReasonCode: "approval_rejected"),
            cancellationToken);

        RecordDiagnosticEvent(
            eventType: approve
                ? "channel.delivery.approval_approved"
                : "channel.delivery.approval_rejected",
            level: approve ? "info" : "warning",
            message: approve
                ? "Channel delivery approval approved."
                : "Channel delivery approval rejected.",
            approval: decidedApproval,
            binding: binding,
            payload: payload,
            account: account);

        if (!approve)
        {
            return new ChannelDeliveryApprovalDispatchResult(
                ChannelDeliveryApprovalDispatchStatus.Completed,
                decidedApproval);
        }

        var dispatch = await _deliveryDispatchService.DispatchAsync(
            account,
            binding,
            new ChannelOutboundDraft(
                DraftId: payload.DraftId,
                BindingId: payload.BindingId,
                ConnectorKind: payload.ConnectorKind,
                AccountId: payload.AccountId,
                ExternalThreadId: payload.ExternalThreadId,
                MessageText: payload.MessageText,
                ThreadType: binding.ThreadType,
                DeliveryMode: payload.DeliveryMode,
                CreatedAt: now,
                SessionId: approval.SessionId,
                CorrelationId: approval.CorrelationId ?? _correlationContextAccessor?.CorrelationId,
                ApprovalId: approval.Id,
                MediaAttachments: payload.MediaAttachments),
            new ChannelTurnOutcome(
                Kind: ChannelTurnOutcomeKind.Delivered,
                Summary: BuildPreview(payload.MessageText),
                OccurredAt: now,
                ReplyText: payload.MessageText,
                DeliveryMode: payload.DeliveryMode,
                ApprovalId: approval.Id,
                DraftId: payload.DraftId),
            cancellationToken);

        if (!dispatch.Succeeded)
        {
            return new ChannelDeliveryApprovalDispatchResult(
                ChannelDeliveryApprovalDispatchStatus.DeliveryFailed,
                decidedApproval,
                dispatch.ErrorMessage);
        }

        return new ChannelDeliveryApprovalDispatchResult(
            ChannelDeliveryApprovalDispatchStatus.Completed,
            decidedApproval);
    }

    private async Task AppendAuditAsync(
        ThreadBinding binding,
        StoredChannelDeliveryPayload payload,
        Approval approval,
        string eventType,
        string summary,
        DateTimeOffset createdAt,
        ChannelTurnOutcome? outcome,
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
                CreatedAt: createdAt,
                SessionId: binding.SessionId,
                ApprovalId: approval.Id,
                DeliveryMode: payload.DeliveryMode,
                Summary: summary,
                MetadataJson: BuildAuditMetadata(payload, approval, outcome)),
            cancellationToken);
    }

    private void RecordDiagnosticEvent(
        string eventType,
        string level,
        string message,
        Approval approval,
        ThreadBinding? binding,
        StoredChannelDeliveryPayload? payload,
        ChannelAccount? account)
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
            SessionId: approval.SessionId ?? binding?.SessionId,
            CorrelationId: approval.CorrelationId ?? _correlationContextAccessor?.CorrelationId,
            Attributes: new Dictionary<string, string?>
            {
                ["approvalId"] = approval.Id,
                ["bindingId"] = binding?.Id ?? payload?.BindingId,
                ["draftId"] = payload?.DraftId,
                ["connectorKind"] = account?.ConnectorKind.ToString() ?? payload?.ConnectorKind.ToString(),
                ["accountId"] = account?.Id ?? payload?.AccountId,
                ["externalThreadId"] = binding?.ExternalThreadId ?? payload?.ExternalThreadId,
                ["deliveryMode"] = payload?.DeliveryMode.ToString(),
            }));
    }

    private async Task<ThreadBinding> LoadBindingAsync(
        string bindingId,
        CancellationToken cancellationToken)
    {
        var binding = await _threadBindingRepository.GetByIdAsync(bindingId, cancellationToken);
        return binding ?? throw new InvalidOperationException(
            $"Channel thread binding '{bindingId}' was not found.");
    }

    private async Task<ChannelAccount> LoadAccountAsync(
        string accountId,
        CancellationToken cancellationToken)
    {
        var account = await _channelAccountRepository.GetByIdAsync(accountId, cancellationToken);
        return account ?? throw new InvalidOperationException(
            $"Channel account '{accountId}' was not found.");
    }

    private static StoredChannelDeliveryPayload ParsePayload(Approval approval)
    {
        if (string.IsNullOrWhiteSpace(approval.PayloadJson))
        {
            throw new ArgumentException("Channel delivery approval payload is required.", nameof(approval));
        }

        var payload = JsonSerializer.Deserialize<StoredChannelDeliveryPayload>(approval.PayloadJson, JsonOptions);
        if (payload is null)
        {
            throw new JsonException("Channel delivery approval payload is invalid.");
        }

        ValidatePayload(payload);
        return payload;
    }

    private static void ValidatePayload(StoredChannelDeliveryPayload payload)
    {
        ChannelHubValidation.RequireNonEmpty(payload.DraftId, nameof(payload.DraftId));
        ChannelHubValidation.RequireNonEmpty(payload.BindingId, nameof(payload.BindingId));
        ChannelHubValidation.RequireNonEmpty(payload.AccountId, nameof(payload.AccountId));
        ChannelHubValidation.RequireNonEmpty(payload.ExternalThreadId, nameof(payload.ExternalThreadId));
        ChannelHubValidation.RequireNonEmpty(payload.MessageText, nameof(payload.MessageText));
    }

    private static void ValidatePayloadAgainstBindingAndAccount(
        StoredChannelDeliveryPayload payload,
        ThreadBinding binding,
        ChannelAccount account)
    {
        ChannelHubValidation.ValidateBinding(binding);

        if (!string.Equals(payload.BindingId, binding.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("Channel delivery approval payload binding id does not match stored binding.");
        }

        if (!string.Equals(payload.AccountId, binding.AccountId, StringComparison.Ordinal) ||
            !string.Equals(payload.AccountId, account.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("Channel delivery approval payload account id does not match stored channel account.");
        }

        if (payload.ConnectorKind != binding.ConnectorKind || payload.ConnectorKind != account.ConnectorKind)
        {
            throw new ArgumentException("Channel delivery approval payload connector kind does not match stored channel account.");
        }

        if (!string.Equals(payload.ExternalThreadId, binding.ExternalThreadId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Channel delivery approval payload external thread id does not match stored binding.");
        }
    }

    private static string BuildDecisionSummary(bool approved, string messageText, string? note)
    {
        var action = approved ? "Approved" : "Rejected";
        var preview = BuildPreview(messageText);
        return string.IsNullOrWhiteSpace(note)
            ? $"{action}: {preview}"
            : $"{action}: {preview} ({note.Trim()})";
    }

    private static string BuildAuditMetadata(
        StoredChannelDeliveryPayload payload,
        Approval approval,
        ChannelTurnOutcome? outcome)
    {
        if (outcome is not null)
        {
            return JsonSerializer.Serialize(new
            {
                outcome.Kind,
                outcome.Summary,
                outcome.OccurredAt,
                outcome.ReplyText,
                outcome.DeliveryMode,
                outcome.ApprovalId,
                outcome.InboxItemId,
                outcome.DraftId,
                outcome.SourceEventId,
                outcome.ReasonCode,
                outcome.HasExplicitMention,
                payload.BindingId,
                payload.AccountId,
                payload.ExternalThreadId,
                payload.ConnectorKind,
            }, JsonOptions);
        }

        return JsonSerializer.Serialize(new
        {
            draftId = payload.DraftId,
            bindingId = payload.BindingId,
            connectorKind = payload.ConnectorKind,
            accountId = payload.AccountId,
            externalThreadId = payload.ExternalThreadId,
            deliveryMode = payload.DeliveryMode,
            approvalId = approval.Id,
        }, JsonOptions);
    }

    private static string BuildPreview(string text) =>
        ChannelTextExtensions.Preview(text, collapseNewlines: true);

    private sealed record StoredChannelDeliveryPayload(
        string DraftId,
        string BindingId,
        ChannelConnectorKind ConnectorKind,
        string AccountId,
        string ExternalThreadId,
        DeliveryMode DeliveryMode,
        string MessageText,
        IReadOnlyList<MediaReference>? MediaAttachments = null);
}
