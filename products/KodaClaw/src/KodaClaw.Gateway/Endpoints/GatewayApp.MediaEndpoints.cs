using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.System;
using KodaClaw.Workspace;
using KodaClaw.Workspace.Media;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

public static partial class GatewayApp
{
    private static void MapMediaEndpoints(WebApplication app)
    {
        var media = app.MapGroup("/api/media");

        media.MapPost("/upload", async (
            HttpContext context,
            IConfiguration configuration,
            IMediaStore mediaStore,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var form = await context.Request.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("file");
            if (file is null)
                return Results.BadRequest(new ErrorResponse(Code: "media.missing_file", Message: "Form field 'file' is required."));

            if (file.Length == 0)
                return Results.BadRequest(new ErrorResponse(Code: "media.empty_file", Message: "Uploaded file must not be empty."));

            await using var stream = file.OpenReadStream();
            var meta = await mediaStore.StoreAsync(file.FileName, file.ContentType, stream, cancellationToken);

            RecordDiagnosticEvent(diagnosticsService, context,
                source: "gateway.media", eventType: "gateway.media.uploaded",
                level: "info", message: "Media file uploaded.",
                attributes: new Dictionary<string, string?> { ["mediaId"] = meta.Id, ["fileName"] = meta.FileName });

            return Results.Ok(meta);
        });

        media.MapGet("/{id}", async (
            string id,
            IMediaStore mediaStore,
            CancellationToken cancellationToken) =>
        {
            var meta = await mediaStore.GetMetaAsync(id, cancellationToken);
            if (meta is null)
                return Results.NotFound(new ErrorResponse(Code: "media.not_found", Message: "Media file was not found."));

            var stream = await mediaStore.OpenReadAsync(id, cancellationToken);
            if (stream is null)
                return Results.NotFound(new ErrorResponse(Code: "media.not_found", Message: "Media file was not found."));

            return Results.File(stream, meta.ContentType, meta.FileName);
        });

        media.MapGet("/{id}/meta", async (
            string id,
            HttpContext context,
            IConfiguration configuration,
            IMediaStore mediaStore,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var meta = await mediaStore.GetMetaAsync(id, cancellationToken);
            if (meta is null)
                return Results.NotFound(new ErrorResponse(Code: "media.not_found", Message: "Media file was not found."));

            return Results.Ok(meta);
        });
    }
}
