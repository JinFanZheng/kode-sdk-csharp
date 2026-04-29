using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Settings;
using KodaClaw.Contracts.System;
using KodaClaw.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

public static partial class GatewayApp
{
    private static void MapSettingsEndpoints(WebApplication app)
    {
        var settings = app.MapGroup("/api/settings");

        settings.MapGet(string.Empty, async (
            HttpContext context,
            IConfiguration configuration,
            ISettingsRepository settingsRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var currentSettings = await settingsRepository.GetAsync(cancellationToken);

            return Results.Ok(currentSettings);
        });

        settings.MapGet("/sandbox-risk", async (
            HttpContext context,
            IConfiguration configuration,
            SandboxRiskOverviewService sandboxRiskOverviewService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var overview = await sandboxRiskOverviewService.GetAsync(cancellationToken);
            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.settings",
                eventType: "gateway.settings.sandbox_risk_fetched",
                level: overview.PluginRisk.HighRiskCount > 0 ||
                    overview.ChannelRisk.AutoSendCount > 0 ||
                    !overview.ApprovalPosture.RequireApprovalForExternalActions
                    ? "warning"
                    : "info",
                message: "Fetched sandbox and operator risk overview.",
                attributes: new Dictionary<string, string?>
                {
                    ["pluginHighRiskCount"] = overview.PluginRisk.HighRiskCount.ToString(),
                    ["channelAutoSendCount"] = overview.ChannelRisk.AutoSendCount.ToString(),
                    ["channelPendingApprovalCount"] = overview.ChannelRisk.PendingApprovalCount.ToString(),
                    ["requireApprovalForExternalActions"] = overview.ApprovalPosture.RequireApprovalForExternalActions.ToString(),
                });

            return Results.Ok(overview);
        });

        settings.MapPut(string.Empty, async (
            HttpContext context,
            KodaClawSettings request,
            IConfiguration configuration,
            ISettingsRepository settingsRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var nextSettings = request with
            {
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            try
            {
                await settingsRepository.SaveAsync(nextSettings, cancellationToken);
            }
            catch (ArgumentException ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.settings",
                    eventType: "gateway.settings.invalid_request",
                    level: "warning",
                    message: ex.Message);
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.settings_invalid",
                    Message: ex.Message));
            }

            var saved = await settingsRepository.GetAsync(cancellationToken);
            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.settings",
                eventType: "gateway.settings.updated",
                level: "info",
                message: "Updated application settings.",
                attributes: new Dictionary<string, string?>
                {
                    ["theme"] = saved.Theme.ToString(),
                    ["defaultLandingRoute"] = saved.DefaultLandingRoute,
                    ["quietHoursEnabled"] = saved.QuietHoursEnabled.ToString(),
                });

            return Results.Ok(saved);
        });
    }
}
