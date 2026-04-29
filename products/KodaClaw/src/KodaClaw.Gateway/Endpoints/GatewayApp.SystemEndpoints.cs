using System.IO;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Backup;
using KodaClaw.Contracts.Bootstrap;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Settings;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

public static partial class GatewayApp
{
    private static void MapSystemEndpoints(WebApplication app)
    {
        // Unauthenticated health probe — used by Docker HEALTHCHECK and load balancers.
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        var system = app.MapGroup("/api/system");
        system.MapGet("/health", () =>
        {
            return Results.Ok(new SystemHealthResponse(
                Name: "KodaClaw Gateway",
                Status: "healthy",
                Mode: AppMode.Bootstrap));
        });

        system.MapGet("/bootstrap-state", async (
            HttpContext context,
            IWorkspaceService workspaceService,
            IConfiguration configuration,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var snapshot = await workspaceService.EnsureInitializedAsync(cancellationToken);
            var mode = snapshot.RequiresBootstrap ? AppMode.Bootstrap : AppMode.Normal;

            return Results.Ok(new BootstrapStateResponse(
                WorkspaceRootPath: snapshot.RootPath,
                WorkspaceVersion: snapshot.WorkspaceVersion,
                WorkspaceInitialized: snapshot.WorkspaceInitialized,
                RequiresBootstrap: snapshot.RequiresBootstrap,
                ActiveMainSessionId: snapshot.ActiveMainSessionId,
                Mode: mode));
        });

        system.MapGet("/secret-migration-report", async (
            HttpContext context,
            SecretMigrationReportService reportService,
            IConfiguration configuration,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            try
            {
                var report = await reportService.GenerateAsync(cancellationToken);
                return Results.Ok(report);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.secrets",
                    eventType: "gateway.secrets.report_failed",
                    level: "error",
                    message: ex.GetBaseException().Message);
                throw;
            }
        });

        system.MapGet("/startup-repair-report", async (
            HttpContext context,
            WorkspaceRepairService repairService,
            IConfiguration configuration,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            try
            {
                var report = await repairService.GetLatestReportAsync(cancellationToken);
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.repair",
                    eventType: "gateway.repair.report_fetched",
                    level: report.Checklist.Summary.ActionRequiredCount > 0 || report.Checklist.Summary.BlockingCount > 0
                        ? "warning"
                        : "info",
                    message: "Fetched startup repair report.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["reportPath"] = report.ReportPath,
                        ["totalCount"] = report.Checklist.Summary.TotalCount.ToString(),
                        ["blockingCount"] = report.Checklist.Summary.BlockingCount.ToString(),
                        ["actionRequiredCount"] = report.Checklist.Summary.ActionRequiredCount.ToString(),
                    });
                return Results.Ok(report);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.repair",
                    eventType: "gateway.repair.report_failed",
                    level: "error",
                    message: ex.GetBaseException().Message);
                throw;
            }
        });

        system.MapGet("/update-state", async (
            HttpContext context,
            UpdateStateService updateStateService,
            IConfiguration configuration,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            try
            {
                var response = await updateStateService.GetAsync(cancellationToken);
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.update",
                    eventType: "gateway.update.state_fetched",
                    level: response.Components.Any(item => item.UpdateAvailability == UpdateAvailability.UpdateAvailable)
                        ? "warning"
                        : "info",
                    message: "Fetched persisted update state snapshot.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["artifactPath"] = response.ArtifactPath,
                        ["manifestSource"] = response.ManifestSource,
                        ["componentCount"] = response.Components.Count.ToString(),
                    });
                return Results.Ok(response);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.update",
                    eventType: "gateway.update.state_fetch_failed",
                    level: "error",
                    message: ex.GetBaseException().Message);
                throw;
            }
        });

        system.MapPost("/update-check", async (
            HttpContext context,
            UpdateCheckRequest? request,
            UpdateStateService updateStateService,
            IConfiguration configuration,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            try
            {
                var response = await updateStateService.CheckAsync(request, cancellationToken);
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.update",
                    eventType: "gateway.update.checked",
                    level: response.Components.Any(item => item.UpdateAvailability == UpdateAvailability.UpdateAvailable)
                        ? "warning"
                        : response.Components.Any(item => item.UpdateAvailability == UpdateAvailability.CheckFailed)
                            ? "error"
                            : "info",
                    message: "Completed manual-first update check.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["artifactPath"] = response.ArtifactPath,
                        ["manifestSource"] = response.ManifestSource,
                        ["componentCount"] = response.Components.Count.ToString(),
                        ["desktopIncluded"] = response.Components.Any(item => item.Component == "desktop").ToString(),
                    });
                return Results.Ok(response);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.update",
                    eventType: "gateway.update.check_failed",
                    level: "error",
                    message: ex.GetBaseException().Message);
                throw;
            }
        });

        system.MapPost("/backup-export", async (
            HttpContext context,
            BackupExportRequest? request,
            WorkspaceBackupService backupService,
            IConfiguration configuration,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            try
            {
                var response = await backupService.ExportAsync(request, cancellationToken);
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.backup",
                    eventType: "gateway.backup.exported",
                    level: "info",
                    message: $"Exported KodaClaw backup archive '{Path.GetFileName(response.ArchivePath)}'.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["archivePath"] = response.ArchivePath,
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
                    source: "gateway.backup",
                    eventType: "gateway.backup.invalid_request",
                    level: "warning",
                    message: ex.Message);
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.archive_path_invalid",
                    Message: ex.Message));
            }
            catch (Exception ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.backup",
                    eventType: "gateway.backup.export_failed",
                    level: "error",
                    message: ex.GetBaseException().Message);
                throw;
            }
        });

        system.MapPost("/backup-import/preflight", async (
            HttpContext context,
            BackupImportPreflightRequest request,
            WorkspaceBackupService backupService,
            IConfiguration configuration,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.ArchivePath))
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.archive_path_required",
                    Message: "ArchivePath is required."));
            }

            try
            {
                var response = await backupService.PreflightImportAsync(request, cancellationToken);
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.backup",
                    eventType: "gateway.backup.preflight_completed",
                    level: response.CanImport ? "info" : "warning",
                    message: $"Backup import preflight inspected '{Path.GetFileName(response.ArchivePath)}'.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["archivePath"] = response.ArchivePath,
                        ["canImport"] = response.CanImport.ToString(),
                        ["blockingCount"] = response.Checklist.Summary.BlockingCount.ToString(),
                        ["actionRequiredCount"] = response.Checklist.Summary.ActionRequiredCount.ToString(),
                    });
                return Results.Ok(response);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (FileNotFoundException ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.backup",
                    eventType: "gateway.backup.invalid_archive",
                    level: "warning",
                    message: ex.GetBaseException().Message);
                return Results.BadRequest(new ErrorResponse(
                    Code: "backup.import.archive_not_found",
                    Message: ex.Message));
            }
            catch (InvalidDataException ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.backup",
                    eventType: "gateway.backup.invalid_archive",
                    level: "warning",
                    message: ex.GetBaseException().Message);
                return Results.BadRequest(new ErrorResponse(
                    Code: "backup.import.invalid_archive",
                    Message: ex.Message));
            }
            catch (ArgumentException ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.backup",
                    eventType: "gateway.backup.invalid_request",
                    level: "warning",
                    message: ex.Message);
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.archive_path_invalid",
                    Message: ex.Message));
            }
            catch (Exception ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.backup",
                    eventType: "gateway.backup.preflight_failed",
                    level: "error",
                    message: ex.GetBaseException().Message);
                throw;
            }
        });

        system.MapPost("/backup-import", async (
            HttpContext context,
            BackupImportRequest request,
            WorkspaceBackupService backupService,
            IConfiguration configuration,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.ArchivePath))
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.archive_path_required",
                    Message: "ArchivePath is required."));
            }

            try
            {
                var response = await backupService.ImportAsync(request, cancellationToken);
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.backup",
                    eventType: "gateway.backup.imported",
                    level: "info",
                    message: $"Imported KodaClaw backup archive '{Path.GetFileName(response.ArchivePath)}'.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["archivePath"] = response.ArchivePath,
                        ["repairReportPath"] = response.RepairReportPath,
                        ["restoredPathCount"] = response.RestoredPaths.Count.ToString(),
                        ["skippedPathCount"] = response.SkippedPaths.Count.ToString(),
                    });
                return Results.Ok(response);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("preflight failed", StringComparison.OrdinalIgnoreCase))
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.backup",
                    eventType: "gateway.backup.import_blocked",
                    level: "warning",
                    message: ex.Message);
                return Results.Conflict(new ErrorResponse(
                    Code: "backup.import.preflight_failed",
                    Message: ex.Message));
            }
            catch (FileNotFoundException ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.backup",
                    eventType: "gateway.backup.invalid_archive",
                    level: "warning",
                    message: ex.GetBaseException().Message);
                return Results.BadRequest(new ErrorResponse(
                    Code: "backup.import.archive_not_found",
                    Message: ex.Message));
            }
            catch (InvalidDataException ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.backup",
                    eventType: "gateway.backup.invalid_archive",
                    level: "warning",
                    message: ex.GetBaseException().Message);
                return Results.BadRequest(new ErrorResponse(
                    Code: "backup.import.invalid_archive",
                    Message: ex.Message));
            }
            catch (ArgumentException ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.backup",
                    eventType: "gateway.backup.invalid_request",
                    level: "warning",
                    message: ex.Message);
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.archive_path_invalid",
                    Message: ex.Message));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.backup",
                    eventType: "gateway.backup.import_failed",
                    level: "error",
                    message: ex.GetBaseException().Message);
                throw;
            }
        });

        system.MapPost("/bootstrap-draft", async (
            HttpContext context,
            BootstrapDraftRequest request,
            IBootstrapDraftService bootstrapDraftService,
            IConfiguration configuration,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var hasConversation = request.Conversation?.Any(item => !string.IsNullOrWhiteSpace(item.Text)) == true;
            var hasCurrentDraft =
                !string.IsNullOrWhiteSpace(request.IdentityMarkdown) ||
                !string.IsNullOrWhiteSpace(request.SoulMarkdown) ||
                !string.IsNullOrWhiteSpace(request.UserMarkdown);
            if (!hasConversation && !hasCurrentDraft)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "workspace.bootstrap",
                    eventType: "workspace.bootstrap.draft_invalid_request",
                    level: "warning",
                    message: "Bootstrap draft generation requires conversation evidence or existing draft content.");
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(
                    new ErrorResponse(
                        Code: "validation.bootstrap_draft_input_required",
                        Message: "Bootstrap draft generation requires conversation evidence or existing draft content."),
                    cancellationToken);
                return;
            }

            try
            {
                var result = await bootstrapDraftService.GenerateDraftAsync(request, cancellationToken);
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "workspace.bootstrap",
                    eventType: "workspace.bootstrap.draft_generated",
                    level: "info",
                    message: "Bootstrap draft generated from onboarding conversation.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["conversationCount"] = request.Conversation?.Count.ToString(),
                    });
                await context.Response.WriteAsJsonAsync(result, cancellationToken);
            }
            catch (ArgumentException ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "workspace.bootstrap",
                    eventType: "workspace.bootstrap.draft_invalid_request",
                    level: "warning",
                    message: ex.Message);
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(
                    new ErrorResponse(
                        Code: "validation.bootstrap_draft_input_required",
                        Message: ex.Message),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "workspace.bootstrap",
                    eventType: "workspace.bootstrap.draft_failed",
                    level: "error",
                    message: ex.GetBaseException().Message);
                throw;
            }
        });

        system.MapGet("/storage-usage", (
            HttpContext context,
            IWorkspaceService workspaceService,
            IConfiguration configuration,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Task.FromResult(Results.Unauthorized());

            var sessionsRoot = Path.Combine(workspaceService.RootPath, KodaClawWorkspaceLayout.SessionsDirectory);
            var main = ComputeSessionTypeUsage(sessionsRoot, "main-");
            var auto = ComputeSessionTypeUsage(sessionsRoot, "auto-");
            var channel = ComputeSessionTypeUsage(sessionsRoot, "channel-");

            var response = new StorageUsageResponse
            {
                Main = main,
                Auto = auto,
                Channel = channel,
                TotalSizeBytes = main.SizeBytes + auto.SizeBytes + channel.SizeBytes,
            };

            return Task.FromResult(Results.Ok(response));
        });

        system.MapPost("/bootstrap-complete", async (
            HttpContext context,
            BootstrapCompletionRequest request,
            IBootstrapService bootstrapService,
            IConfiguration configuration,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (string.IsNullOrWhiteSpace(request.IdentityMarkdown) ||
                string.IsNullOrWhiteSpace(request.SoulMarkdown) ||
                string.IsNullOrWhiteSpace(request.UserMarkdown))
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "workspace.bootstrap",
                    eventType: "workspace.bootstrap.invalid_request",
                    level: "warning",
                    message: "Bootstrap completion requires identity, soul, and user markdown.");
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(
                    new ErrorResponse(
                        Code: "validation.markdown_required",
                        Message: "Identity, soul, and user markdown are required."),
                    cancellationToken);
                return;
            }

            try
            {
                var result = await bootstrapService.CompleteAsync(request, cancellationToken);
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "workspace.bootstrap",
                    eventType: "workspace.bootstrap.completed",
                    level: "info",
                    message: "Bootstrap completion wrote workspace identity, soul, and user files.");
                await context.Response.WriteAsJsonAsync(result, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "workspace.bootstrap",
                    eventType: "workspace.bootstrap.failed",
                    level: "error",
                    message: ex.GetBaseException().Message);
                throw;
            }
        });
    }
}
