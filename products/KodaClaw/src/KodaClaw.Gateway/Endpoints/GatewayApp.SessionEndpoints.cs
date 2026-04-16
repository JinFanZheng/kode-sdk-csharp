using KodaClaw.Contracts;
using KodaClaw.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

public static partial class GatewayApp
{
    private static void MapSessionEndpoints(WebApplication app)
    {
        var sessions = app.MapGroup("/api/sessions");
        sessions.MapGet(string.Empty, async (
            HttpContext context,
            IConfiguration configuration,
            IWorkspaceService workspaceService,
            IDiagnosticsService diagnosticsService,
            [FromQuery] int? limit,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            await workspaceService.EnsureInitializedAsync(cancellationToken);
            var appConfig = await workspaceService.LoadAppConfigAsync(cancellationToken);
            var sessions = await LoadSessionsAsync(
                workspaceService.RootPath,
                appConfig.ActiveMainSessionId,
                NormalizeSessionsLimit(limit),
                cancellationToken);

            return Results.Ok(new SessionsQueryResponse(
                sessions
                    .Select(static session => new SessionSummary(
                        SessionId: session.SessionId,
                        SessionKind: session.SessionKind,
                        Status: session.Status,
                        CreatedAt: session.CreatedAt,
                        LastEventAt: session.LastEventAt,
                        Title: session.Title))
                    .ToArray()));
        });

        sessions.MapPost("/rotate", async (
            HttpContext context,
            IConfiguration configuration,
            IMainSessionService mainSessionService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var previousSessionId = await mainSessionService.RotateMainSessionAsync(cancellationToken);

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.sessions",
                eventType: "gateway.sessions.rotated",
                level: "info",
                message: "Main session rotated.",
                attributes: new Dictionary<string, string?>
                {
                    ["previousSessionId"] = previousSessionId,
                });

            return Results.Ok(new RotateSessionResponse(Ok: true, PreviousSessionId: previousSessionId));
        });

        sessions.MapPost("/{id}/resume", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IMainSessionService mainSessionService,
            IWorkspaceService workspaceService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            await workspaceService.EnsureInitializedAsync(cancellationToken);
            var appConfig = await workspaceService.LoadAppConfigAsync(cancellationToken);

            var session = await LoadSessionDetailAsync(
                workspaceService.RootPath,
                id,
                appConfig.ActiveMainSessionId,
                cancellationToken);

            if (session is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.sessions",
                    eventType: "gateway.sessions.resume.not_found",
                    level: "warning",
                    message: "Requested session for resume was not found.",
                    attributes: new Dictionary<string, string?> { ["sessionId"] = id });
                return Results.NotFound(new ErrorResponse(
                    Code: "session.not_found",
                    Message: "Session was not found."));
            }

            var response = await mainSessionService.ResumeSessionAsync(id, cancellationToken);

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.sessions",
                eventType: "gateway.sessions.resumed",
                level: "info",
                message: "Main session resumed.",
                attributes: new Dictionary<string, string?> { ["resumedSessionId"] = id });

            return Results.Ok(response);
        });

        sessions.MapGet("/{id}/messages", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IWorkspaceService workspaceService,
            IDiagnosticsService diagnosticsService,
            [FromQuery] int? limit,
            [FromQuery] int? skip,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            await workspaceService.EnsureInitializedAsync(cancellationToken);
            var appConfig = await workspaceService.LoadAppConfigAsync(cancellationToken);

            var session = await LoadSessionDetailAsync(
                workspaceService.RootPath,
                id,
                appConfig.ActiveMainSessionId,
                cancellationToken);

            if (session is null)
            {
                return Results.NotFound(new ErrorResponse(
                    Code: "session.not_found",
                    Message: "Session was not found."));
            }

            var pageLimit = limit.HasValue ? Math.Clamp(limit.Value, 1, 100) : 20;
            var pageSkip = skip.HasValue ? Math.Max(skip.Value, 0) : 0;
            var response = await LoadSessionMessagesAsync(
                workspaceService.RootPath,
                id,
                pageLimit,
                pageSkip,
                cancellationToken);

            return Results.Ok(response);
        });

        sessions.MapDelete("/main/{id}", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IWorkspaceService workspaceService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            if (!id.StartsWith("main-", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "session.delete.invalid_kind",
                    Message: "Only main- sessions can be deleted via this endpoint."));
            }

            var appConfig = await workspaceService.LoadAppConfigAsync(cancellationToken);
            if (string.Equals(id, appConfig.ActiveMainSessionId, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "session.delete.active_session",
                    Message: "Cannot delete the currently active main session."));
            }

            var sessionDir = workspaceService.GetSessionDirectory(id);
            if (!Directory.Exists(sessionDir))
            {
                return Results.NotFound(new ErrorResponse(
                    Code: "session.not_found",
                    Message: "Session directory was not found."));
            }

            Directory.Delete(sessionDir, recursive: true);

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.sessions",
                eventType: "gateway.sessions.deleted",
                level: "info",
                message: "Main session deleted.",
                attributes: new Dictionary<string, string?> { ["sessionId"] = id });

            return Results.Ok();
        });

        sessions.MapGet("/{id}", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IWorkspaceService workspaceService,
            IDiagnosticsService diagnosticsService,
            IProviderAccountRepository accountRepository,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            await workspaceService.EnsureInitializedAsync(cancellationToken);
            var appConfig = await workspaceService.LoadAppConfigAsync(cancellationToken);

            var session = await LoadSessionDetailAsync(
                workspaceService.RootPath,
                id,
                appConfig.ActiveMainSessionId,
                cancellationToken);

            if (session is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.sessions",
                    eventType: "gateway.sessions.not_found",
                    level: "warning",
                    message: "Requested session was not found.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["sessionId"] = id,
                    });
                return Results.NotFound(new ErrorResponse(
                    Code: "session.not_found",
                    Message: "Session was not found."));
            }

            ResolvedModel? resolvedModel = null;
            try
            {
                resolvedModel = await accountRepository.ResolveDefaultForAsync(
                    ModelCapabilitySet.Text, cancellationToken);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionEndpoints] Model not configured: {ex.Message}");
            }

            var enrichedSession = session with
            {
                AccountModelId = resolvedModel?.Model.Id,
                AccountModelName = resolvedModel?.Model.DisplayName,
                ModelCapabilities = (int)(resolvedModel?.Model.Capabilities ?? ModelCapabilitySet.None),
            };

            return Results.Ok(enrichedSession);
        });

    }
}
