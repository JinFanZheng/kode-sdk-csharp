using KodaClaw.ChannelHub;
using KodaClaw.ChannelHub.Connectors.Webhook;
using KodaClaw.Contracts;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using KodaClaw.ChannelHub.Audit;
using KodaClaw.ChannelHub.Policy;
using KodaClaw.Contracts.Approvals;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;

public static partial class GatewayApp
{
    private static async Task<ChannelThreadDetail?> LoadChannelThreadDetailAsync(
        IWorkspaceService workspaceService,
        IThreadBindingRepository threadBindingRepository,
        IChannelAccountRepository channelAccountRepository,
        ChannelPolicyEngine channelPolicyEngine,
        ChannelAuditQueryService channelAuditQueryService,
        IApprovalRepository approvalRepository,
        string bindingId,
        CancellationToken cancellationToken)
    {
        var binding = await threadBindingRepository.GetByIdAsync(bindingId, cancellationToken);
        if (binding is null)
        {
            return null;
        }

        var account = await channelAccountRepository.GetByIdAsync(binding.AccountId, cancellationToken);
        if (account is null)
        {
            return null;
        }

        var snapshot = await workspaceService.GetSnapshotAsync(cancellationToken);
        var sessionDetail = await LoadSessionDetailAsync(
            workspaceService.RootPath,
            binding.SessionId,
            snapshot.ActiveMainSessionId,
            cancellationToken);
        var session = sessionDetail is null
            ? null
            : new SessionSummary(
                SessionId: sessionDetail.SessionId,
                SessionKind: sessionDetail.SessionKind,
                Status: sessionDetail.Status,
                CreatedAt: sessionDetail.CreatedAt,
                LastEventAt: sessionDetail.LastEventAt);
        var audit = await channelAuditQueryService.ListRecentByBindingIdAsync(binding.Id, 20, cancellationToken);
        var pendingApproval = await LoadPendingChannelApprovalAsync(
            approvalRepository,
            binding.SessionId,
            cancellationToken);
        var lastTurnOutcome = TryResolveLastTurnOutcome(audit);
        var policy = channelPolicyEngine.CreateDefaultPolicy(binding.ThreadType, binding.UpdatedAt, binding.PolicyId);
        var policyEvidence = BuildPolicyEvidence(policy, pendingApproval, lastTurnOutcome, audit);

        return new ChannelThreadDetail(
            Account: account,
            Binding: binding,
            Policy: policy,
            DeliveryRule: BuildDefaultChannelDeliveryRule(binding.ThreadType, binding.UpdatedAt, binding.DeliveryRuleId, binding.DeliveryModeOverride),
            RecentAudit: audit,
            Session: session,
            PendingApprovalId: pendingApproval?.Id,
            HasPendingDraft: pendingApproval is not null,
            PolicyEvidence: policyEvidence,
            LastTurnOutcome: lastTurnOutcome);
    }

    private static async Task<Approval?> LoadPendingChannelApprovalAsync(
        IApprovalRepository approvalRepository,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var approvals = await approvalRepository.ListAsync(
            new ApprovalQuery(
                Status: ApprovalStatus.Pending,
                Kind: ApprovalKind.ChannelDelivery,
                SessionId: sessionId,
                Limit: 1),
            cancellationToken);

        return approvals.FirstOrDefault();
    }

    private static ChannelThreadSummary BuildChannelThreadSummary(
        ThreadBinding binding,
        ChannelAccount? account,
        Approval? pendingApproval,
        ChannelTurnOutcome? lastTurnOutcome)
    {
        return new ChannelThreadSummary(
            BindingId: binding.Id,
            ConnectorKind: binding.ConnectorKind,
            AccountId: binding.AccountId,
            ExternalThreadId: binding.ExternalThreadId,
            ThreadType: binding.ThreadType,
            SessionId: binding.SessionId,
            SessionKind: binding.SessionKind,
            DisplayTitle: ResolveChannelDisplayTitle(binding),
            DeliveryMode: binding.DeliveryModeOverride ?? ResolveDefaultChannelDeliveryMode(binding.ThreadType),
            AccountState: account?.State ?? ChannelAccountState.Disconnected,
            UpdatedAt: binding.UpdatedAt,
            LastInboundAt: binding.LastInboundAt,
            LastOutboundAt: binding.LastOutboundAt,
            LastMessagePreview: binding.LastMessagePreview,
            PendingApprovalId: pendingApproval?.Id,
            HasPendingDraft: pendingApproval is not null,
            LastTurnOutcome: lastTurnOutcome);
    }

    private static ChannelTurnOutcome? TryResolveLastTurnOutcome(IReadOnlyList<ChannelAuditEntry> audit)
    {
        foreach (var entry in audit)
        {
            if (!string.IsNullOrWhiteSpace(entry.MetadataJson))
            {
                try
                {
                    var outcome = JsonSerializer.Deserialize<ChannelTurnOutcome>(
                        entry.MetadataJson,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (outcome is not null)
                    {
                        return outcome;
                    }
                }
                catch (JsonException)
                {
                    // Ignore legacy or non-turn audit metadata.
                }
            }

            if (TryMapOutcomeFromEventType(entry, out var mapped))
            {
                return mapped;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> BuildPolicyEvidence(
        ChannelPolicy policy,
        Approval? pendingApproval,
        ChannelTurnOutcome? lastTurnOutcome,
        IReadOnlyList<ChannelAuditEntry> audit)
    {
        List<string> items = [];
        var latestNoAction = audit.FirstOrDefault(entry => entry.EventType == "turn.no_action");
        var latestApprovalRejected = audit.FirstOrDefault(entry => entry.EventType == "approval.rejected");

        if (policy.ThreadType == ChannelThreadType.Group && policy.RequireExplicitMention)
        {
            items.Add("group_mention_required");
        }

        if (lastTurnOutcome?.ReasonCode == "policy_blocked_requires_mention")
        {
            items.Add("blocked_without_mention");
        }

        if (lastTurnOutcome?.ReasonCode == "approval_rejected")
        {
            items.Add($"approval_rejected|{lastTurnOutcome.OccurredAt:O}");
        }

        if (pendingApproval is not null)
        {
            items.Add("pending_approval");
        }
        else if (lastTurnOutcome?.Kind is ChannelTurnOutcomeKind.DraftCreated or ChannelTurnOutcomeKind.ApprovalRequested)
        {
            items.Add("draft_waiting");
        }

        if (latestApprovalRejected is not null && lastTurnOutcome?.ReasonCode != "approval_rejected")
        {
            items.Add($"last_approval_reject|{latestApprovalRejected.CreatedAt:O}");
        }

        if (latestNoAction is not null &&
            lastTurnOutcome?.Kind == ChannelTurnOutcomeKind.NoAction &&
            lastTurnOutcome.ReasonCode != "approval_rejected")
        {
            items.Add($"last_no_action|{latestNoAction.CreatedAt:O}");
        }

        return items.Count > 0 ? items : ["none"];
    }

    private static bool TryMapOutcomeFromEventType(ChannelAuditEntry entry, out ChannelTurnOutcome? outcome)
    {
        var kind = entry.EventType switch
        {
            "turn.no_action" => ChannelTurnOutcomeKind.NoAction,
            "turn.draft_created" => ChannelTurnOutcomeKind.DraftCreated,
            "turn.approval_requested" => ChannelTurnOutcomeKind.ApprovalRequested,
            "turn.failed" => ChannelTurnOutcomeKind.Failed,
            "delivery.sent" => ChannelTurnOutcomeKind.Delivered,
            "delivery.failed" => ChannelTurnOutcomeKind.Failed,
            _ => (ChannelTurnOutcomeKind?)null,
        };

        if (kind is null)
        {
            outcome = null;
            return false;
        }

        outcome = new ChannelTurnOutcome(
            Kind: kind.Value,
            Summary: entry.Summary ?? entry.EventType,
            OccurredAt: entry.CreatedAt,
            DeliveryMode: entry.DeliveryMode,
            ApprovalId: entry.ApprovalId);
        return true;
    }

    private static string ResolveChannelDisplayTitle(ThreadBinding binding)
    {
        return binding.ChannelIdentity.DisplayName
            ?? binding.ChannelIdentity.Username
            ?? binding.ExternalThreadId;
    }

    private static DeliveryMode ResolveDefaultChannelDeliveryMode(ChannelThreadType threadType)
    {
        return threadType switch
        {
            ChannelThreadType.DirectMessage => DeliveryMode.DraftApproval,
            ChannelThreadType.Group => DeliveryMode.RequireApproval,
            _ => throw new ArgumentOutOfRangeException(nameof(threadType), threadType, null),
        };
    }

    private static DeliveryRule BuildDefaultChannelDeliveryRule(
        ChannelThreadType threadType,
        DateTimeOffset updatedAt,
        string? deliveryRuleId = null,
        DeliveryMode? deliveryModeOverride = null)
    {
        return new DeliveryRule(
            Id: string.IsNullOrWhiteSpace(deliveryRuleId)
                ? threadType == ChannelThreadType.DirectMessage
                    ? "delivery-default-dm"
                    : "delivery-default-group"
                : deliveryRuleId.Trim(),
            Mode: deliveryModeOverride ?? ResolveDefaultChannelDeliveryMode(threadType),
            UpdatedAt: updatedAt,
            AllowProactiveSend: false,
            MuteDuringQuietHours: true);
    }

    private static (int StatusCode, ErrorResponse Error) MapWebhookRejection(
        GenericWebhookInboundDispatchResult result)
    {
        return result.RejectionCode switch
        {
            "secret_mismatch" => (
                StatusCodes.Status401Unauthorized,
                new ErrorResponse(
                    Code: "channel.webhook_secret_mismatch",
                    Message: result.RejectionMessage ?? "Webhook shared secret did not match.")),
            "invalid_payload" => (
                StatusCodes.Status400BadRequest,
                new ErrorResponse(
                    Code: "channel.webhook_payload_invalid",
                    Message: result.RejectionMessage ?? "Webhook payload is invalid.")),
            "account_not_started" => (
                StatusCodes.Status409Conflict,
                new ErrorResponse(
                    Code: "channel.account_not_started",
                    Message: result.RejectionMessage ?? "Channel account is not started.")),
            _ => (
                StatusCodes.Status400BadRequest,
                new ErrorResponse(
                    Code: "channel.webhook_rejected",
                    Message: result.RejectionMessage ?? "Webhook event was rejected.")),
        };
    }
}
