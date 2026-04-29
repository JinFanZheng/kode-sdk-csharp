using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Chat;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.System;
using KodaClaw.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

public static partial class GatewayApp
{
    private static void MapChatAndDiagnosticsEndpoints(WebApplication app)
    {
        var chat = app.MapGroup("/api/chat");
        var diagnostics = app.MapGroup("/api/diagnostics");
        chat.MapPost("/stream", async (
            HttpContext context,
            ChatStreamRequest request,
            [FromServices] IChatSessionService chatSessionService,
            IConfiguration configuration,
            IDiagnosticsService diagnosticsService,
            IHostApplicationLifetime appLifetime,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var hasMedia = request.MediaIds is { Count: > 0 };
            if (string.IsNullOrWhiteSpace(request.Message) && !hasMedia)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.chat",
                    eventType: "gateway.chat.invalid_request",
                    level: "warning",
                    message: "Chat stream request is missing both a message and media.");
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(
                    new ErrorResponse(
                        Code: "validation.message_required",
                        Message: "Message or media attachment is required."),
                    cancellationToken);
                return;
            }

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.chat",
                eventType: "gateway.chat.requested",
                level: "info",
                message: "Accepted chat stream request.",
                sessionId: request.SessionId,
                attributes: new Dictionary<string, string?>
                {
                    ["requestedSessionId"] = request.SessionId,
                });

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Response.Headers["Connection"] = "keep-alive";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, appLifetime.ApplicationStopping);
            var token = cts.Token;

            string? terminalSessionId = request.SessionId;
            string terminalEventType = "gateway.chat.completed";
            string terminalLevel = "info";
            string terminalMessage = "Chat stream completed.";
            string terminalStreamEventType = "done";

            try
            {
                await foreach (var chatEvent in chatSessionService.StreamMainSessionAsync(request, token))
                {
                    terminalSessionId = chatEvent.SessionId ?? terminalSessionId;
                    switch (chatEvent.Type)
                    {
                        case "done":
                            terminalMessage = string.IsNullOrWhiteSpace(chatEvent.Reason)
                                ? "Chat stream completed."
                                : chatEvent.Reason!;
                            terminalStreamEventType = "done";
                            break;

                        case "error":
                            terminalEventType = "gateway.chat.failed";
                            terminalLevel = "error";
                            terminalMessage = chatEvent.Error?.Message
                                ?? chatEvent.Reason
                                ?? "Chat stream failed.";
                            terminalStreamEventType = "error";
                            break;
                    }

                    var eventName = chatEvent.Type switch
                    {
                        "text_chunk" or "done" or "error" or "session_rotated" => chatEvent.Type,
                        _ => "text_chunk"
                    };

                    var serialized = JsonSerializer.Serialize(chatEvent, GatewayJson.Options);
                    var eventId = chatEvent.Sequence?.ToString() ?? chatEvent.Timestamp?.ToString();
                    var payload = eventId != null
                        ? $"id: {eventId}\nevent: {eventName}\ndata: {serialized}\n\n"
                        : $"event: {eventName}\ndata: {serialized}\n\n";

                    await context.Response.WriteAsync(payload, token);
                    await context.Response.Body.FlushAsync(token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                terminalEventType = "gateway.chat.failed";
                terminalLevel = "error";
                terminalMessage = ex.GetBaseException().Message;
                terminalStreamEventType = "exception";
                throw;
            }
            finally
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.chat",
                    eventType: terminalEventType,
                    level: terminalLevel,
                    message: terminalMessage,
                    sessionId: terminalSessionId,
                    attributes: new Dictionary<string, string?>
                    {
                        ["terminalStreamEventType"] = terminalStreamEventType,
                    });
            }
        });

        diagnostics.MapGet("/recent", (
            HttpContext context,
            IConfiguration configuration,
            IDiagnosticsService diagnosticsService,
            [FromQuery] int? limit,
            [FromQuery] string? correlationId,
            [FromQuery] string? sessionId,
            [FromQuery] string? source,
            [FromQuery] string? eventType,
            [FromQuery] string[]? levels,
            [FromQuery] DateTimeOffset? dateFrom,
            [FromQuery] DateTimeOffset? dateTo) =>
        {
            return QueryDiagnosticsEndpoint(
                context,
                configuration,
                diagnosticsService,
                endpointName: "diagnostics.recent",
                limit,
                correlationId,
                sessionId,
                source,
                eventType,
                levels,
                dateFrom,
                dateTo);
        });

        diagnostics.MapGet("/timeline", (
            HttpContext context,
            IConfiguration configuration,
            IDiagnosticsService diagnosticsService,
            [FromQuery] int? limit,
            [FromQuery] string? correlationId,
            [FromQuery] string? sessionId,
            [FromQuery] string? source,
            [FromQuery] string? eventType,
            [FromQuery] string[]? levels,
            [FromQuery] DateTimeOffset? dateFrom,
            [FromQuery] DateTimeOffset? dateTo) =>
        {
            return QueryDiagnosticsEndpoint(
                context,
                configuration,
                diagnosticsService,
                endpointName: "diagnostics.timeline",
                limit,
                correlationId,
                sessionId,
                source,
                eventType,
                levels,
                dateFrom,
                dateTo);
        });

        diagnostics.MapGet("/stats", (
            HttpContext context,
            IConfiguration configuration,
            IDiagnosticsService diagnosticsService,
            [FromQuery] DateTimeOffset? since) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            return Results.Ok(diagnosticsService.GetStats(since));
        });

        diagnostics.MapDelete("/", async (
            HttpContext context,
            IConfiguration configuration,
            IDiagnosticsService diagnosticsService,
            [FromQuery] DateTimeOffset? before,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            await diagnosticsService.ClearAsync(before, cancellationToken);
            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.diagnostics",
                eventType: "gateway.diagnostics.cleared",
                level: "info",
                message: before.HasValue
                    ? $"Cleared diagnostics events before {before.Value:O}."
                    : "Cleared all diagnostics events.",
                attributes: before.HasValue
                    ? new Dictionary<string, string?> { ["before"] = before.Value.ToString("O") }
                    : null);
            return Results.NoContent();
        });

        diagnostics.MapGet("/stream", async (
            HttpContext context,
            IConfiguration configuration,
            IDiagnosticsService diagnosticsService,
            IHostApplicationLifetime appLifetime,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Response.Headers["Connection"] = "keep-alive";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, appLifetime.ApplicationStopping);
            var token = cts.Token;

            try
            {
                await foreach (var evt in diagnosticsService.SubscribeAsync(token))
                {
                    var serialized = System.Text.Json.JsonSerializer.Serialize(evt, GatewayJson.Options);
                    var diagId = evt.Timestamp.ToUnixTimeMilliseconds().ToString();
                    await context.Response.WriteAsync($"id: {diagId}\ndata: {serialized}\n\n", token);
                    await context.Response.Body.FlushAsync(token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 客户端断开或应用关闭，正常退出
            }
        });

        diagnostics.MapPost("/bundle-export", async (
            HttpContext context,
            DiagnosticBundleExportRequest? request,
            IConfiguration configuration,
            DiagnosticBundleService diagnosticBundleService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            try
            {
                var response = await diagnosticBundleService.ExportAsync(request, cancellationToken);
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.diagnostics",
                    eventType: "gateway.diagnostics.bundle_exported",
                    level: "info",
                    message: "Exported redacted diagnostic bundle.",
                    sessionId: request?.SessionId,
                    attributes: new Dictionary<string, string?>
                    {
                        ["bundlePath"] = response.BundlePath,
                        ["requestedSessionId"] = request?.SessionId,
                        ["entryCount"] = response.Manifest.Entries.Count.ToString(),
                    });
                return Results.Ok(response);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ArgumentException ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.diagnostics",
                    eventType: "gateway.diagnostics.invalid_request",
                    level: "warning",
                    message: ex.Message,
                    sessionId: request?.SessionId);
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.archive_path_invalid",
                    Message: ex.Message));
            }
            catch (Exception ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.diagnostics",
                    eventType: "gateway.diagnostics.bundle_export_failed",
                    level: "error",
                    message: ex.GetBaseException().Message,
                    sessionId: request?.SessionId);
                throw;
            }
        });
    }
}
