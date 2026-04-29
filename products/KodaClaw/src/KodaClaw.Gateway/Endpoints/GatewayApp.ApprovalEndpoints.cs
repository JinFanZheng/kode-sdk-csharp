using KodaClaw.Contracts;
using KodaClaw.Contracts.Approvals;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

public static partial class GatewayApp
{
    private static void MapApprovalEndpoints(WebApplication app)
    {
        var approvals = app.MapGroup("/api/approvals");
        approvals.MapGet(string.Empty, async (
            HttpContext context,
            IConfiguration configuration,
            IApprovalRepository approvalRepository,
            IDiagnosticsService diagnosticsService,
            [FromQuery] int? limit,
            [FromQuery] string? status,
            [FromQuery] string? kind,
            [FromQuery] string? sessionId,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            if (!TryParseEnum(status, out ApprovalStatus? parsedStatus))
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.approvals",
                    eventType: "gateway.approvals.invalid_request",
                    level: "warning",
                    message: "Approvals list received an invalid status filter.");
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.approval_status_invalid",
                    Message: "Approval status filter is invalid."));
            }

            if (!TryParseEnum(kind, out ApprovalKind? parsedKind))
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.approvals",
                    eventType: "gateway.approvals.invalid_request",
                    level: "warning",
                    message: "Approvals list received an invalid kind filter.");
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.approval_kind_invalid",
                    Message: "Approval kind filter is invalid."));
            }

            var approvals = await approvalRepository.ListAsync(
                new ApprovalQuery(
                    Status: parsedStatus,
                    Kind: parsedKind,
                    SessionId: sessionId,
                    Limit: NormalizeApprovalsLimit(limit)),
                cancellationToken);

            return Results.Ok(new ApprovalQueryResponse(approvals));
        });

        approvals.MapGet("/{id}", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IApprovalRepository approvalRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var approval = await approvalRepository.GetByIdAsync(id, cancellationToken);
            if (approval is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.approvals",
                    eventType: "gateway.approvals.not_found",
                    level: "warning",
                    message: "Requested approval was not found.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["approvalId"] = id,
                    });
                return Results.NotFound(new ErrorResponse(
                    Code: "approval.not_found",
                    Message: "Approval was not found."));
            }

            return Results.Ok(approval);
        });

        approvals.MapPost("/{id}/approve", async (
            HttpContext context,
            string id,
            ApprovalDecisionRequest? request,
            IConfiguration configuration,
            IApprovalRepository approvalRepository,
            IInboxRepository inboxRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            return await HandleApprovalDecisionAsync(
                context,
                configuration,
                approvalRepository,
                inboxRepository,
                diagnosticsService,
                id,
                approve: true,
                note: request?.Note,
                cancellationToken);
        });

        approvals.MapPost("/{id}/reject", async (
            HttpContext context,
            string id,
            ApprovalDecisionRequest? request,
            IConfiguration configuration,
            IApprovalRepository approvalRepository,
            IInboxRepository inboxRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            return await HandleApprovalDecisionAsync(
                context,
                configuration,
                approvalRepository,
                inboxRepository,
                diagnosticsService,
                id,
                approve: false,
                note: request?.Note,
                cancellationToken);
        });

    }
}
