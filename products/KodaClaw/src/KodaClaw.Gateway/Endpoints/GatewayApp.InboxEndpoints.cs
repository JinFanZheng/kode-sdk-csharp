using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

public static partial class GatewayApp
{
    private static void MapInboxEndpoints(WebApplication app)
    {
        var inbox = app.MapGroup("/api/inbox");
        inbox.MapGet(string.Empty, async (
            HttpContext context,
            IConfiguration configuration,
            IInboxRepository inboxRepository,
            IDiagnosticsService diagnosticsService,
            [FromQuery] int? limit,
            [FromQuery] string? status,
            [FromQuery] string? kind,
            [FromQuery] bool? requiresAction,
            [FromQuery] string? sessionId,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            if (!TryParseEnum(status, out InboxItemStatus? parsedStatus))
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.inbox",
                    eventType: "gateway.inbox.invalid_request",
                    level: "warning",
                    message: "Inbox list received an invalid status filter.");
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.inbox_status_invalid",
                    Message: "Inbox status filter is invalid."));
            }

            if (!TryParseEnum(kind, out InboxItemKind? parsedKind))
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.inbox",
                    eventType: "gateway.inbox.invalid_request",
                    level: "warning",
                    message: "Inbox list received an invalid kind filter.");
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.inbox_kind_invalid",
                    Message: "Inbox kind filter is invalid."));
            }

            var items = await inboxRepository.ListAsync(
                new InboxQuery(
                    Status: parsedStatus,
                    Kind: parsedKind,
                    RequiresAction: requiresAction,
                    SessionId: sessionId,
                    Limit: NormalizeInboxLimit(limit)),
                cancellationToken);

            return Results.Ok(new InboxQueryResponse(items));
        });

        inbox.MapGet("/{id}", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IInboxRepository inboxRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var item = await inboxRepository.GetByIdAsync(id, cancellationToken);
            if (item is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.inbox",
                    eventType: "gateway.inbox.not_found",
                    level: "warning",
                    message: "Requested inbox item was not found.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["inboxItemId"] = id,
                    });
                return Results.NotFound(new ErrorResponse(
                    Code: "inbox.not_found",
                    Message: "Inbox item was not found."));
            }

            return Results.Ok(item);
        });

        inbox.MapPatch("/{id}/status", async (
            HttpContext context,
            string id,
            InboxStatusUpdateRequest request,
            IConfiguration configuration,
            IInboxRepository inboxRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (!TryParseEnum(request.Status, out InboxItemStatus? status))
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.inbox",
                    eventType: "gateway.inbox.invalid_request",
                    level: "warning",
                    message: "Inbox status update received an invalid status.");
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(
                    new ErrorResponse(
                        Code: "validation.inbox_status_invalid",
                        Message: "Inbox status is invalid."),
                    cancellationToken);
                return;
            }

            var updated = await inboxRepository.UpdateStatusAsync(
                id,
                status!.Value,
                updatedAt: DateTimeOffset.UtcNow,
                cancellationToken: cancellationToken);

            if (!updated)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.inbox",
                    eventType: "gateway.inbox.not_found",
                    level: "warning",
                    message: "Inbox status update targeted a missing item.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["inboxItemId"] = id,
                    });
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(
                    new ErrorResponse(
                        Code: "inbox.not_found",
                        Message: "Inbox item was not found."),
                    cancellationToken);
                return;
            }

            var item = await inboxRepository.GetByIdAsync(id, cancellationToken);
            if (item is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(
                    new ErrorResponse(
                        Code: "inbox.not_found",
                        Message: "Inbox item was not found."),
                    cancellationToken);
                return;
            }

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.inbox",
                eventType: "gateway.inbox.updated",
                level: "info",
                message: "Updated inbox item status.",
                sessionId: item.SessionId,
                attributes: new Dictionary<string, string?>
                {
                    ["inboxItemId"] = item.Id,
                    ["status"] = item.Status.ToString(),
                });

            await context.Response.WriteAsJsonAsync(item, cancellationToken);
        });

    }
}
