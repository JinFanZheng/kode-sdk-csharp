using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Gateway;
using KodaClaw.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

public static partial class GatewayApp
{
    private static void MapWorkspaceEndpoints(WebApplication app)
    {
        var workspace = app.MapGroup("/api/workspace");

        workspace.MapGet("/readiness", async (
            HttpContext context,
            IConfiguration configuration,
            IWorkspaceReadinessService readinessService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var readiness = await readinessService.GetReadinessAsync(cancellationToken);
            return Results.Ok(readiness);
        });

        workspace.MapGet("/file", async (
            HttpContext context,
            string target,
            IConfiguration configuration,
            IWorkspaceService workspaceService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var (fileName, error) = ResolveWorkspaceTarget(target);
            if (error is not null) return Results.BadRequest(new ErrorResponse(Code: "workspace.invalid_target", Message: error));

            var filePath = Path.Combine(workspaceService.RootPath, KodaClawWorkspaceLayout.WorkspaceDirectory, fileName!);
            var content = File.Exists(filePath) ? await File.ReadAllTextAsync(filePath, cancellationToken) : string.Empty;
            return Results.Ok(new WorkspaceFileResponse(Target: target, Content: content));
        });

        workspace.MapPut("/file", async (
            HttpContext context,
            string target,
            WorkspaceFileUpdateRequest body,
            IConfiguration configuration,
            IWorkspaceService workspaceService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var (fileName, error) = ResolveWorkspaceTarget(target);
            if (error is not null) return Results.BadRequest(new ErrorResponse(Code: "workspace.invalid_target", Message: error));

            var dirPath = Path.Combine(workspaceService.RootPath, KodaClawWorkspaceLayout.WorkspaceDirectory);
            Directory.CreateDirectory(dirPath);
            var filePath = Path.Combine(dirPath, fileName!);
            await File.WriteAllTextAsync(filePath, body.Content ?? string.Empty, cancellationToken);
            RecordDiagnosticEvent(diagnosticsService, context, source: "gateway.workspace",
                eventType: "gateway.workspace.file_updated", level: "info",
                message: $"Workspace file updated: {target}.",
                attributes: new Dictionary<string, string?> { ["target"] = target });
            await workspaceService.TryCommitWorkspaceAsync(
                $"workspace({target})[user/settings-desk]: edit via Settings Desk",
                cancellationToken);
            return Results.Ok(new WorkspaceFileResponse(Target: target, Content: body.Content ?? string.Empty));
        });

        workspace.MapGet("/persona-presets", (
            HttpContext context,
            IConfiguration configuration,
            PersonaPresetService personaPresetService,
            IDiagnosticsService diagnosticsService) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var allPresets = personaPresetService.GetAll();
            return Results.Ok(allPresets);
        });

        workspace.MapGet("/persona-presets/{presetId}", (
            HttpContext context,
            string presetId,
            IConfiguration configuration,
            PersonaPresetService personaPresetService,
            IDiagnosticsService diagnosticsService) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var preset = personaPresetService.GetById(presetId);
            if (preset is null)
                return Results.NotFound(new ErrorResponse(
                    Code: "persona_preset.not_found",
                    Message: "Persona preset was not found."));

            return Results.Ok(preset);
        });

        var onboarding = app.MapGroup("/api/onboarding");

        onboarding.MapGet("/state", async (
            HttpContext context,
            IConfiguration configuration,
            OnboardingStateService onboardingStateService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var state = await onboardingStateService.GetStateAsync(cancellationToken);
            return Results.Ok(state);
        });

        onboarding.MapPut("/state", async (
            HttpContext context,
            OnboardingState body,
            IConfiguration configuration,
            OnboardingStateService onboardingStateService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            await onboardingStateService.SaveStateAsync(body, cancellationToken);
            return Results.Ok(body);
        });

        onboarding.MapPost("/complete", async (
            HttpContext context,
            IConfiguration configuration,
            OnboardingStateService onboardingStateService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var state = await onboardingStateService.CompleteAsync(cancellationToken);
            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.onboarding",
                eventType: "gateway.onboarding.completed",
                level: "info",
                message: "Onboarding marked as completed.");
            return Results.Ok(state);
        });

        onboarding.MapPost("/reset", async (
            HttpContext context,
            IConfiguration configuration,
            OnboardingStateService onboardingStateService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var state = await onboardingStateService.ResetAsync(cancellationToken);
            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.onboarding",
                eventType: "gateway.onboarding.reset",
                level: "info",
                message: "Onboarding state reset.");
            return Results.Ok(state);
        });

        static (string? FileName, string? Error) ResolveWorkspaceTarget(string target) => target switch
        {
            "identity"  => (KodaClawWorkspaceLayout.IdentityFile, null),
            "soul"      => (KodaClawWorkspaceLayout.SoulFile, null),
            "ontology"  => (KodaClawWorkspaceLayout.OntologyFile, null),
            "user"      => (KodaClawWorkspaceLayout.UserFile, null),
            "memory"    => (KodaClawWorkspaceLayout.MemoryFile, null),
            "heartbeat" => (KodaClawWorkspaceLayout.HeartbeatFile, null),
            _           => (null, $"Unknown workspace target '{target}'. Allowed: identity, soul, ontology, user, memory, heartbeat."),
        };

        onboarding.MapPost("/apply-persona", async (
            HttpContext context,
            ApplyPersonaRequest body,
            IConfiguration configuration,
            PersonaPresetService personaPresetService,
            IWorkspaceService workspaceService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var preset = personaPresetService.GetById(body.PresetId);
            if (preset is null)
                return Results.NotFound(new ErrorResponse(
                    Code: "persona_preset.not_found",
                    Message: "Persona preset was not found."));

            // Write SOUL.md and IDENTITY.md into workspace
            var soulPath = Path.Combine(workspaceService.RootPath,
                KodaClawWorkspaceLayout.WorkspaceDirectory,
                KodaClawWorkspaceLayout.SoulFile);
            var identityPath = Path.Combine(workspaceService.RootPath,
                KodaClawWorkspaceLayout.WorkspaceDirectory,
                KodaClawWorkspaceLayout.IdentityFile);

            await File.WriteAllTextAsync(soulPath, preset.SoulMarkdown, cancellationToken);
            await File.WriteAllTextAsync(identityPath, preset.IdentityMarkdown, cancellationToken);

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.onboarding",
                eventType: "gateway.onboarding.persona_applied",
                level: "info",
                message: "Persona preset applied to workspace.",
                attributes: new Dictionary<string, string?>
                {
                    ["presetId"] = body.PresetId,
                    ["displayName"] = preset.DisplayName,
                });

            return Results.Ok(new { ok = true });
        });
    }
}
