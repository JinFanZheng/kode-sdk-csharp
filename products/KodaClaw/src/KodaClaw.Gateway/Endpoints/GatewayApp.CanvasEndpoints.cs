using KodaClaw.Contracts;
using KodaClaw.Contracts.Canvas;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

public static partial class GatewayApp
{
    private static void MapCanvasEndpoints(WebApplication app)
    {
        var canvas = app.MapGroup("/api/canvas");
        canvas.MapGet(string.Empty, async (
            HttpContext context,
            IConfiguration configuration,
            ICanvasArtifactRepository canvasRepository,
            IDiagnosticsService diagnosticsService,
            [FromQuery] string? kind,
            [FromQuery] string? source,
            [FromQuery] string? sessionId,
            [FromQuery] int? limit,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            if (!TryParseEnum(kind, out CanvasArtifactKind? parsedKind))
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.canvas",
                    eventType: "gateway.canvas.invalid_request",
                    level: "warning",
                    message: "Canvas list received an invalid kind filter.");
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.canvas_kind_invalid",
                    Message: "Canvas kind filter is invalid."));
            }

            var items = await canvasRepository.ListAsync(
                new CanvasArtifactQuery(
                    Kind: parsedKind,
                    Source: source,
                    SessionId: sessionId,
                    Limit: NormalizeCanvasLimit(limit)),
                cancellationToken);

            var defaultArtifact = items
                .OrderByDescending(static artifact => artifact.UpdatedAt)
                .ThenByDescending(static artifact => artifact.CreatedAt)
                .FirstOrDefault();

            return Results.Ok(new CanvasQueryResponse(
                Items: items,
                DefaultEntryPath: defaultArtifact?.EntryPath ?? DefaultCanvasEntryPath,
                DefaultArtifactId: defaultArtifact?.Id));
        });

        canvas.MapGet("/default", async (
            HttpContext context,
            IConfiguration configuration,
            ICanvasArtifactRepository canvasRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var items = await canvasRepository.ListAsync(
                new CanvasArtifactQuery(Limit: 1),
                cancellationToken);
            var artifact = items.FirstOrDefault();

            return Results.Ok(BuildCanvasEntryResponse(context, configuration, artifact));
        });

        canvas.MapGet("/{id}/entry", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            ICanvasArtifactRepository canvasRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var artifact = await canvasRepository.GetByIdAsync(id, cancellationToken);
            if (artifact is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.canvas",
                    eventType: "gateway.canvas.not_found",
                    level: "warning",
                    message: "Requested canvas artifact entry was not found.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["artifactId"] = id,
                    });
                return Results.NotFound(new ErrorResponse(
                    Code: "canvas.not_found",
                    Message: "Canvas artifact was not found."));
            }

            return Results.Ok(BuildCanvasEntryResponse(context, configuration, artifact));
        });

        canvas.MapGet("/{id}", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            ICanvasArtifactRepository canvasRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var artifact = await canvasRepository.GetByIdAsync(id, cancellationToken);
            if (artifact is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.canvas",
                    eventType: "gateway.canvas.not_found",
                    level: "warning",
                    message: "Requested canvas artifact was not found.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["artifactId"] = id,
                    });
                return Results.NotFound(new ErrorResponse(
                    Code: "canvas.not_found",
                    Message: "Canvas artifact was not found."));
            }

            return Results.Ok(artifact);
        });

        canvas.MapPost(string.Empty, async (
            HttpContext context,
            UpsertCanvasArtifactRequest request,
            IConfiguration configuration,
            ICanvasArtifactRepository canvasRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var existing = await canvasRepository.GetByIdAsync(request.Id, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var artifact = new CanvasArtifact(
                Id: request.Id,
                Title: request.Title,
                Kind: request.Kind,
                Summary: request.Summary,
                Source: request.Source,
                EntryPath: request.EntryPath,
                AssetDirectory: request.AssetDirectory,
                CreatedAt: existing?.CreatedAt ?? now,
                UpdatedAt: now,
                Route: request.Route,
                SessionId: request.SessionId,
                CorrelationId: request.CorrelationId,
                MetadataJson: request.MetadataJson);

            try
            {
                await canvasRepository.UpsertAsync(artifact, cancellationToken);
            }
            catch (ArgumentException ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.canvas",
                    eventType: "gateway.canvas.invalid_request",
                    level: "warning",
                    message: ex.Message,
                    attributes: new Dictionary<string, string?>
                    {
                        ["artifactId"] = request.Id,
                    });
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.canvas_invalid",
                    Message: ex.Message));
            }

            var reloaded = await canvasRepository.GetByIdAsync(request.Id, cancellationToken) ?? artifact;
            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.canvas",
                eventType: existing is null ? "gateway.canvas.created" : "gateway.canvas.updated",
                level: "info",
                message: existing is null ? "Created canvas artifact." : "Updated canvas artifact.",
                attributes: new Dictionary<string, string?>
                {
                    ["artifactId"] = reloaded.Id,
                    ["kind"] = reloaded.Kind.ToString(),
                });

            return existing is null
                ? Results.Created($"/api/canvas/{reloaded.Id}", reloaded)
                : Results.Ok(reloaded);
        });

        canvas.MapGet("/fs", async (
            HttpContext context,
            IConfiguration configuration,
            IWorkspaceService workspaceService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            return await ServeCanvasFileAsync(
                context,
                configuration,
                workspaceService,
                diagnosticsService,
                path: null,
                cancellationToken);
        });

        canvas.MapGet("/fs/{**path}", async (
            HttpContext context,
            string? path,
            IConfiguration configuration,
            IWorkspaceService workspaceService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            return await ServeCanvasFileAsync(
                context,
                configuration,
                workspaceService,
                diagnosticsService,
                path,
                cancellationToken);
        });

        canvas.MapGet("/preview/{previewToken}", async (
            HttpContext context,
            string previewToken,
            IConfiguration configuration,
            IWorkspaceService workspaceService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            return await ServeCanvasPreviewAsync(
                context,
                configuration,
                workspaceService,
                diagnosticsService,
                previewToken,
                path: null,
                cancellationToken);
        });

        canvas.MapGet("/preview/{previewToken}/{**path}", async (
            HttpContext context,
            string previewToken,
            string? path,
            IConfiguration configuration,
            IWorkspaceService workspaceService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            return await ServeCanvasPreviewAsync(
                context,
                configuration,
                workspaceService,
                diagnosticsService,
                previewToken,
                path,
                cancellationToken);
        });

    }
}
