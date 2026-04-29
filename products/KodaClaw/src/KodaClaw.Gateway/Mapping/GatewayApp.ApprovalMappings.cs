using KodaClaw.ChannelHub;
using KodaClaw.ChannelHub.Delivery;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Approvals;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.System;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static partial class GatewayApp
{
    private static async Task<IResult> HandleApprovalDecisionAsync(
        HttpContext context,
        IConfiguration configuration,
        IApprovalRepository approvalRepository,
        IInboxRepository inboxRepository,
        IDiagnosticsService diagnosticsService,
        string approvalId,
        bool approve,
        string? note,
        CancellationToken cancellationToken)
    {
        var actionName = approve ? "approve" : "reject";
        if (!TryAuthorize(context, configuration))
        {
            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.auth",
                eventType: "gateway.auth.failed",
                level: "warning",
                message: $"Unauthorized access to approval {actionName} endpoint.");
            return Results.Unauthorized();
        }

        var approval = await approvalRepository.GetByIdAsync(approvalId, cancellationToken);
        if (approval is null)
        {
            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.approvals",
                eventType: "gateway.approvals.not_found",
                level: "warning",
                message: $"Approval {actionName} targeted a missing item.",
                attributes: new Dictionary<string, string?>
                {
                    ["approvalId"] = approvalId,
                    ["decision"] = actionName,
                });
            return Results.NotFound(new ErrorResponse(
                Code: "approval.not_found",
                Message: "Approval was not found."));
        }

        if (approval.Status != ApprovalStatus.Pending)
        {
            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.approvals",
                eventType: "gateway.approvals.conflict",
                level: "warning",
                message: "Approval decision targeted a non-pending item.",
                sessionId: approval.SessionId,
                attributes: new Dictionary<string, string?>
                {
                    ["approvalId"] = approval.Id,
                    ["decision"] = actionName,
                    ["status"] = approval.Status.ToString(),
                });
            return Results.Conflict(new ErrorResponse(
                Code: "approval.not_pending",
                Message: $"Approval is already {approval.Status}."));
        }

        if (approval.Kind == ApprovalKind.ChannelDelivery)
        {
            var channelDeliveryApprovalService = context.RequestServices.GetService<ChannelDeliveryApprovalService>();
            if (channelDeliveryApprovalService is null)
            {
                return MapFailedApprovalDecision(
                    context,
                    diagnosticsService,
                    approvalId,
                    approval.SessionId,
                    actionName,
                    "gateway.approvals.runtime_unavailable",
                    "error",
                    "Channel delivery approval service is unavailable.",
                    Results.Json(
                        new ErrorResponse(
                            Code: "runtime.unavailable",
                            Message: "Channel delivery approval service is unavailable."),
                        statusCode: StatusCodes.Status503ServiceUnavailable));
            }

            var channelResult = approve
                ? await channelDeliveryApprovalService.ApproveAsync(approval, note, cancellationToken)
                : await channelDeliveryApprovalService.RejectAsync(approval, note, cancellationToken);

            return MapChannelApprovalDecisionResult(
                context,
                diagnosticsService,
                approvalId,
                approve,
                channelResult);
        }

        ApprovalDecisionDispatchResult result;
        var mainSessionService = context.RequestServices.GetService<IMainSessionService>();
        if (mainSessionService is null)
        {
            result = approve
                ? new ApprovalDecisionDispatchResult(
                    ApprovalDecisionDispatchStatus.LiveSessionRequired,
                    approval,
                    "A live runtime session is required for this approval.")
                : await RejectApprovalWithoutLiveSessionAsync(
                    approval,
                    note,
                    approvalRepository,
                    inboxRepository,
                    cancellationToken);
        }
        else
        {
            result = approve
                ? await mainSessionService.ApproveApprovalAsync(approvalId, cancellationToken)
                : await mainSessionService.RejectApprovalAsync(approvalId, note, cancellationToken);

            if (!approve && result.Status == ApprovalDecisionDispatchStatus.LiveSessionRequired)
            {
                result = await RejectApprovalWithoutLiveSessionAsync(
                    approval,
                    note,
                    approvalRepository,
                    inboxRepository,
                    cancellationToken);
            }
        }

        return MapApprovalDecisionResult(
            context,
            diagnosticsService,
            approvalId,
            approve,
            result);
    }

    private static IResult MapChannelApprovalDecisionResult(
        HttpContext context,
        IDiagnosticsService diagnosticsService,
        string approvalId,
        bool approve,
        ChannelDeliveryApprovalDispatchResult result)
    {
        var actionName = approve ? "approve" : "reject";
        return result.Status switch
        {
            ChannelDeliveryApprovalDispatchStatus.Completed when result.Approval is not null =>
                MapCompletedApprovalDecision(
                    context,
                    diagnosticsService,
                    actionName,
                    new ApprovalDecisionDispatchResult(
                        ApprovalDecisionDispatchStatus.Completed,
                        result.Approval,
                        result.Message)),
            ChannelDeliveryApprovalDispatchStatus.NotFound =>
                Results.NotFound(new ErrorResponse(
                    Code: "approval.not_found",
                    Message: "Approval was not found.")),
            ChannelDeliveryApprovalDispatchStatus.NotPending =>
                MapFailedApprovalDecision(
                    context,
                    diagnosticsService,
                    approvalId,
                    result.Approval?.SessionId,
                    actionName,
                    "gateway.approvals.conflict",
                    "warning",
                    result.Message ?? "Approval is no longer pending.",
                    Results.Conflict(new ErrorResponse(
                        Code: "approval.not_pending",
                        Message: result.Message ?? "Approval is no longer pending."))),
            ChannelDeliveryApprovalDispatchStatus.InvalidApproval =>
                MapFailedApprovalDecision(
                    context,
                    diagnosticsService,
                    approvalId,
                    result.Approval?.SessionId,
                    actionName,
                    "gateway.approvals.invalid_payload",
                    "error",
                    result.Message ?? "Channel delivery approval payload is invalid.",
                    Results.BadRequest(new ErrorResponse(
                        Code: "channel.delivery_approval_invalid",
                        Message: result.Message ?? "Channel delivery approval payload is invalid."))),
            ChannelDeliveryApprovalDispatchStatus.DeliveryFailed =>
                MapFailedApprovalDecision(
                    context,
                    diagnosticsService,
                    approvalId,
                    result.Approval?.SessionId,
                    actionName,
                    "gateway.approvals.delivery_failed",
                    "error",
                    result.Message ?? "Channel delivery failed after approval.",
                    Results.Json(
                        new ErrorResponse(
                            Code: "channel.delivery_failed",
                            Message: result.Message ?? "Channel delivery failed after approval."),
                        statusCode: StatusCodes.Status503ServiceUnavailable)),
            _ =>
                MapFailedApprovalDecision(
                    context,
                    diagnosticsService,
                    approvalId,
                    result.Approval?.SessionId,
                    actionName,
                    "gateway.approvals.failed",
                    "error",
                    result.Message ?? "Approval decision failed.",
                    Results.Json(
                        new ErrorResponse(
                            Code: "approval.decision_failed",
                            Message: result.Message ?? "Approval decision failed."),
                        statusCode: StatusCodes.Status500InternalServerError)),
        };
    }

    private static IResult MapApprovalDecisionResult(
        HttpContext context,
        IDiagnosticsService diagnosticsService,
        string approvalId,
        bool approve,
        ApprovalDecisionDispatchResult result)
    {
        var actionName = approve ? "approve" : "reject";
        return result.Status switch
        {
            ApprovalDecisionDispatchStatus.Completed when result.Approval is not null =>
                MapCompletedApprovalDecision(context, diagnosticsService, actionName, result),
            ApprovalDecisionDispatchStatus.NotFound =>
                Results.NotFound(new ErrorResponse(
                    Code: "approval.not_found",
                    Message: "Approval was not found.")),
            ApprovalDecisionDispatchStatus.NotPending =>
                MapFailedApprovalDecision(
                    context,
                    diagnosticsService,
                    approvalId,
                    result.Approval?.SessionId,
                    actionName,
                    "gateway.approvals.conflict",
                    "warning",
                    result.Message ?? "Approval is no longer pending.",
                    Results.Conflict(new ErrorResponse(
                        Code: "approval.not_pending",
                        Message: result.Message ?? "Approval is no longer pending."))),
            ApprovalDecisionDispatchStatus.LiveSessionRequired =>
                MapFailedApprovalDecision(
                    context,
                    diagnosticsService,
                    approvalId,
                    result.Approval?.SessionId,
                    actionName,
                    "gateway.approvals.live_session_required",
                    "warning",
                    result.Message ?? "A live runtime session is required for this approval.",
                    Results.Conflict(new ErrorResponse(
                        Code: "approval.live_session_required",
                        Message: result.Message ?? "A live runtime session is required for this approval."))),
            ApprovalDecisionDispatchStatus.LiveApprovalMissing =>
                MapFailedApprovalDecision(
                    context,
                    diagnosticsService,
                    approvalId,
                    result.Approval?.SessionId,
                    actionName,
                    "gateway.approvals.live_context_missing",
                    "warning",
                    result.Message ?? "Pending approval is no longer attached to an active approval waiter.",
                    Results.Conflict(new ErrorResponse(
                        Code: "approval.live_context_missing",
                        Message: result.Message ?? "Pending approval is no longer attached to an active approval waiter."))),
            ApprovalDecisionDispatchStatus.DecisionTimedOut =>
                MapFailedApprovalDecision(
                    context,
                    diagnosticsService,
                    approvalId,
                    result.Approval?.SessionId,
                    actionName,
                    "gateway.approvals.decision_timeout",
                    "error",
                    result.Message ?? "Timed out waiting for approval decision to persist.",
                    Results.Json(
                        new ErrorResponse(
                            Code: "approval.decision_timeout",
                            Message: result.Message ?? "Timed out waiting for approval decision to persist."),
                        statusCode: StatusCodes.Status504GatewayTimeout)),
            ApprovalDecisionDispatchStatus.RuntimeUnavailable =>
                MapFailedApprovalDecision(
                    context,
                    diagnosticsService,
                    approvalId,
                    result.Approval?.SessionId,
                    actionName,
                    "gateway.approvals.runtime_unavailable",
                    "error",
                    result.Message ?? "Approval runtime is unavailable.",
                    Results.Json(
                        new ErrorResponse(
                            Code: "runtime.unavailable",
                            Message: result.Message ?? "Approval runtime is unavailable."),
                        statusCode: StatusCodes.Status503ServiceUnavailable)),
            _ =>
                MapFailedApprovalDecision(
                    context,
                    diagnosticsService,
                    approvalId,
                    result.Approval?.SessionId,
                    actionName,
                    "gateway.approvals.failed",
                    "error",
                    result.Message ?? "Approval decision failed.",
                    Results.Json(
                        new ErrorResponse(
                            Code: "approval.decision_failed",
                            Message: result.Message ?? "Approval decision failed."),
                        statusCode: StatusCodes.Status500InternalServerError)),
        };
    }

    private static IResult MapCompletedApprovalDecision(
        HttpContext context,
        IDiagnosticsService diagnosticsService,
        string actionName,
        ApprovalDecisionDispatchResult result)
    {
        var approval = result.Approval!;
        var pastTense = actionName == "approve" ? "approved" : "rejected";
        RecordDiagnosticEvent(
            diagnosticsService,
            context,
            source: "gateway.approvals",
            eventType: $"gateway.approvals.{pastTense}",
            level: actionName == "approve" ? "info" : "warning",
            message: $"Approval {pastTense} successfully.",
            sessionId: approval.SessionId,
            attributes: new Dictionary<string, string?>
            {
                ["approvalId"] = approval.Id,
                ["status"] = approval.Status.ToString(),
                ["decidedBy"] = approval.DecidedBy,
            });
        return Results.Ok(approval);
    }

    private static IResult MapFailedApprovalDecision(
        HttpContext context,
        IDiagnosticsService diagnosticsService,
        string approvalId,
        string? sessionId,
        string actionName,
        string eventType,
        string level,
        string message,
        IResult result)
    {
        RecordDiagnosticEvent(
            diagnosticsService,
            context,
            source: "gateway.approvals",
            eventType: eventType,
            level: level,
            message: message,
            sessionId: sessionId,
            attributes: new Dictionary<string, string?>
            {
                ["approvalId"] = approvalId,
                ["decision"] = actionName,
            });
        return result;
    }

    private static async Task<ApprovalDecisionDispatchResult> RejectApprovalWithoutLiveSessionAsync(
        Approval approval,
        string? note,
        IApprovalRepository approvalRepository,
        IInboxRepository inboxRepository,
        CancellationToken cancellationToken)
    {
        var updatedAt = DateTimeOffset.UtcNow;
        var transitioned = await approvalRepository.TransitionAsync(
            approval.Id,
            ApprovalStatus.Rejected,
            updatedAt,
            decidedBy: "api",
            decisionNote: note,
            cancellationToken);

        if (!transitioned)
        {
            var reloaded = await approvalRepository.GetByIdAsync(approval.Id, cancellationToken);
            return reloaded is null
                ? new ApprovalDecisionDispatchResult(
                    ApprovalDecisionDispatchStatus.NotFound,
                    Message: "Approval was not found.")
                : new ApprovalDecisionDispatchResult(
                    reloaded.Status == ApprovalStatus.Pending
                        ? ApprovalDecisionDispatchStatus.DecisionTimedOut
                        : ApprovalDecisionDispatchStatus.NotPending,
                    reloaded,
                    reloaded.Status == ApprovalStatus.Pending
                        ? "Timed out waiting for approval decision to persist."
                        : $"Approval is already {reloaded.Status}.");
        }

        if (!string.IsNullOrWhiteSpace(approval.InboxItemId))
        {
            await inboxRepository.UpdateStatusAsync(
                approval.InboxItemId,
                InboxItemStatus.Resolved,
                updatedAt,
                resolvedAt: updatedAt,
                cancellationToken: cancellationToken);
        }

        var decidedApproval = await approvalRepository.GetByIdAsync(approval.Id, cancellationToken);
        return decidedApproval is null
            ? new ApprovalDecisionDispatchResult(
                ApprovalDecisionDispatchStatus.NotFound,
                Message: "Approval was not found.")
            : new ApprovalDecisionDispatchResult(
                ApprovalDecisionDispatchStatus.Completed,
                decidedApproval,
                "rejected_without_live_session");
    }
}
