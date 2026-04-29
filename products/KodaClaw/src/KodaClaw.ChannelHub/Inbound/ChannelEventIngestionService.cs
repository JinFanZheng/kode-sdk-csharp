using System.Security.Cryptography;
using System.Text;
using KodaClaw.ChannelHub.Common;
using KodaClaw.ChannelHub.Policy;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Sessions;

namespace KodaClaw.ChannelHub.Inbound;

public sealed class ChannelEventIngestionService
{
    private readonly IThreadBindingRepository _threadBindingRepository;
    private readonly ChannelPolicyEngine _policyEngine;
    private readonly IChannelAuditRepository? _channelAuditRepository;
    private readonly IDiagnosticsService? _diagnosticsService;
    private readonly ICorrelationContextAccessor? _correlationContextAccessor;

    public ChannelEventIngestionService(
        IThreadBindingRepository threadBindingRepository,
        ChannelPolicyEngine policyEngine,
        IChannelAuditRepository? channelAuditRepository = null,
        IDiagnosticsService? diagnosticsService = null,
        ICorrelationContextAccessor? correlationContextAccessor = null)
    {
        _threadBindingRepository = threadBindingRepository ?? throw new ArgumentNullException(nameof(threadBindingRepository));
        _policyEngine = policyEngine ?? throw new ArgumentNullException(nameof(policyEngine));
        _channelAuditRepository = channelAuditRepository;
        _diagnosticsService = diagnosticsService;
        _correlationContextAccessor = correlationContextAccessor;
    }

    public async Task<ChannelInboundProcessingResult> IngestAsync(
        ChannelEventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ValidateEnvelope(envelope);

        var occurredAt = envelope.OccurredAt == default
            ? DateTimeOffset.UtcNow
            : envelope.OccurredAt;
        var preview = BuildPreview(envelope);

        var existingBinding = await _threadBindingRepository.GetByExternalThreadAsync(
            envelope.ConnectorKind,
            envelope.AccountId,
            envelope.ExternalThreadId,
            cancellationToken);

        var createdBinding = existingBinding is null;
        var binding = existingBinding is null
            ? CreateBinding(envelope, occurredAt, preview)
            : existingBinding with
            {
                UpdatedAt = occurredAt,
                LastInboundAt = occurredAt,
                LastMessagePreview = preview,
            };

        await _threadBindingRepository.UpsertAsync(binding, cancellationToken);

        var policy = _policyEngine.CreateDefaultPolicy(
            binding.ThreadType,
            occurredAt,
            binding.PolicyId);
        var deliveryRule = CreateDefaultDeliveryRule(
            binding.ThreadType,
            occurredAt,
            binding.DeliveryRuleId,
            binding.DeliveryModeOverride);

        ChannelAuditEntry? auditEntry = null;
        if (_channelAuditRepository is not null)
        {
            auditEntry = BuildAuditEntry(binding, envelope, deliveryRule.Mode, preview, occurredAt);
            await _channelAuditRepository.AppendAsync(auditEntry, cancellationToken);
        }

        RecordDiagnosticEvent(binding, envelope, createdBinding);

        return new ChannelInboundProcessingResult(
            Binding: binding,
            Policy: policy,
            DeliveryRule: deliveryRule,
            CreatedBinding: createdBinding,
            AuditEntry: auditEntry);
    }

    private void RecordDiagnosticEvent(
        ThreadBinding binding,
        ChannelEventEnvelope envelope,
        bool createdBinding)
    {
        if (_diagnosticsService is null)
        {
            return;
        }

        _diagnosticsService.Record(new DiagnosticEvent(
            Id: $"diag-channel-ingest-{Guid.NewGuid():N}",
            Source: "channel.ingest",
            EventType: createdBinding
                ? "channel.ingest.binding_created"
                : "channel.ingest.binding_reused",
            Level: createdBinding ? "info" : "debug",
            Message: createdBinding
                ? "Created channel thread binding for inbound event."
                : "Reused channel thread binding for inbound event.",
            Timestamp: DateTimeOffset.UtcNow,
            SessionId: binding.SessionId,
            CorrelationId: envelope.CorrelationId ?? _correlationContextAccessor?.CorrelationId,
            Attributes: new Dictionary<string, string?>
            {
                ["bindingId"] = binding.Id,
                ["connectorKind"] = binding.ConnectorKind.ToString(),
                ["accountId"] = binding.AccountId,
                ["externalThreadId"] = binding.ExternalThreadId,
                ["eventType"] = envelope.EventType.ToString(),
                ["externalMessageId"] = envelope.ExternalMessageId,
            }));
    }

    private static ThreadBinding CreateBinding(
        ChannelEventEnvelope envelope,
        DateTimeOffset occurredAt,
        string preview)
    {
        var threadType = envelope.ThreadType;
        var sessionKind = ChannelPolicyEngine.ResolveSessionKind(threadType);
        var channelIdentity = ResolveThreadIdentity(envelope);

        return new ThreadBinding(
            Id: BuildBindingId(envelope),
            ConnectorKind: envelope.ConnectorKind,
            AccountId: envelope.AccountId,
            ExternalThreadId: envelope.ExternalThreadId,
            ThreadType: threadType,
            SessionId: BuildSessionId(envelope),
            SessionKind: sessionKind,
            ChannelIdentity: channelIdentity,
            PolicyId: BuildDefaultPolicyId(threadType),
            DeliveryRuleId: BuildDefaultDeliveryRuleId(threadType),
            CreatedAt: occurredAt,
            UpdatedAt: occurredAt,
            LastInboundAt: occurredAt,
            LastMessagePreview: preview,
            DeliveryModeOverride: envelope.DefaultDeliveryMode);
    }

    private static ChannelIdentity ResolveThreadIdentity(ChannelEventEnvelope envelope)
    {
        if (envelope.ThreadType == ChannelThreadType.Group && envelope.Recipient is not null)
        {
            return envelope.Recipient;
        }

        if (envelope.Sender is not null)
        {
            return envelope.Sender;
        }

        if (envelope.Recipient is not null)
        {
            return envelope.Recipient;
        }

        return new ChannelIdentity(envelope.ExternalThreadId);
    }

    private static ChannelAuditEntry BuildAuditEntry(
        ThreadBinding binding,
        ChannelEventEnvelope envelope,
        DeliveryMode deliveryMode,
        string summary,
        DateTimeOffset createdAt)
    {
        return new ChannelAuditEntry(
            Id: BuildAuditId(binding, envelope.EventId),
            BindingId: binding.Id,
            ConnectorKind: envelope.ConnectorKind,
            AccountId: envelope.AccountId,
            ExternalThreadId: envelope.ExternalThreadId,
            ThreadType: envelope.ThreadType,
            EventType: MapAuditEventType(envelope.EventType),
            CreatedAt: createdAt,
            SessionId: binding.SessionId,
            DeliveryMode: deliveryMode,
            ExternalMessageId: envelope.ExternalMessageId,
            Summary: summary,
            MetadataJson: envelope.MetadataJson);
    }

    private static void ValidateEnvelope(ChannelEventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        ChannelHubValidation.RequireNonEmpty(envelope.EventId, nameof(envelope.EventId));
        ChannelHubValidation.RequireNonEmpty(envelope.AccountId, nameof(envelope.AccountId));
        ChannelHubValidation.RequireNonEmpty(envelope.ExternalThreadId, nameof(envelope.ExternalThreadId));

        if (envelope.EventType is ChannelEventType.AccountConnected or ChannelEventType.AccountDisconnected)
        {
            throw new ArgumentException(
                "Channel ingestion only accepts thread-scoped inbound events.",
                nameof(envelope));
        }
    }

    private static string BuildBindingId(ChannelEventEnvelope envelope)
    {
        return $"binding-{BuildStableSuffix(envelope.ConnectorKind, envelope.AccountId, envelope.ExternalThreadId)}";
    }

    private static string BuildSessionId(ChannelEventEnvelope envelope)
    {
        var prefix = envelope.ThreadType == ChannelThreadType.DirectMessage
            ? "channel-dm"
            : "channel-group";
        return $"{prefix}-{BuildStableSuffix(envelope.ConnectorKind, envelope.AccountId, envelope.ExternalThreadId)}";
    }

    private static string BuildAuditId(ThreadBinding binding, string eventId)
    {
        return $"audit-{BuildStableSuffix(binding.Id, eventId)}";
    }

    private static string BuildStableSuffix(params object[] parts)
    {
        var raw = string.Join("::", parts.Select(static part => part?.ToString()?.Trim() ?? string.Empty));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes[..8]).ToLowerInvariant();
    }

    private static string BuildPreview(ChannelEventEnvelope envelope)
    {
        var text = envelope.Text;
        return string.IsNullOrWhiteSpace(text)
            ? MapAuditEventType(envelope.EventType)
            : ChannelTextExtensions.Preview(text, collapseNewlines: true);
    }

    private static string BuildDefaultPolicyId(ChannelThreadType threadType)
    {
        return threadType switch
        {
            ChannelThreadType.DirectMessage => "policy-default-dm",
            ChannelThreadType.Group => "policy-default-group",
            _ => throw new ArgumentOutOfRangeException(nameof(threadType), threadType, null),
        };
    }

    private static string BuildDefaultDeliveryRuleId(ChannelThreadType threadType)
    {
        return threadType switch
        {
            ChannelThreadType.DirectMessage => "delivery-default-dm",
            ChannelThreadType.Group => "delivery-default-group",
            _ => throw new ArgumentOutOfRangeException(nameof(threadType), threadType, null),
        };
    }

    private static DeliveryRule CreateDefaultDeliveryRule(
        ChannelThreadType threadType,
        DateTimeOffset updatedAt,
        string? deliveryRuleId = null,
        DeliveryMode? modeOverride = null)
    {
        var mode = modeOverride ?? threadType switch
        {
            ChannelThreadType.DirectMessage => DeliveryMode.AutoSend,
            ChannelThreadType.Group => DeliveryMode.DraftApproval,
            _ => throw new ArgumentOutOfRangeException(nameof(threadType), threadType, null),
        };

        return new DeliveryRule(
            Id: string.IsNullOrWhiteSpace(deliveryRuleId)
                ? BuildDefaultDeliveryRuleId(threadType)
                : deliveryRuleId.Trim(),
            Mode: mode,
            UpdatedAt: updatedAt,
            AllowProactiveSend: false,
            MuteDuringQuietHours: true);
    }

    private static string MapAuditEventType(ChannelEventType eventType)
    {
        return eventType switch
        {
            ChannelEventType.MessageReceived => "message.received",
            ChannelEventType.MessageEdited => "message.edited",
            ChannelEventType.MessageDeleted => "message.deleted",
            ChannelEventType.ReactionReceived => "reaction.received",
            ChannelEventType.AccountConnected => "account.connected",
            ChannelEventType.AccountDisconnected => "account.disconnected",
            ChannelEventType.DeliveryFailed => "delivery.failed",
            _ => eventType.ToString(),
        };
    }
}
