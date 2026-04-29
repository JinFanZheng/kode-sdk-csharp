using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Plugins;
using KodaClaw.Contracts.System;
using KodaClaw.Gateway.Plugins;
using KodaClaw.PluginHost.Manifest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

public static partial class GatewayApp
{
    private static void MapPluginEndpoints(WebApplication app)
    {
        var plugins = app.MapGroup("/api/plugins");
        plugins.MapGet(string.Empty, async (
            HttpContext context,
            IConfiguration configuration,
            IPluginGatewayService pluginGatewayService,
            IDiagnosticsService diagnosticsService,
            [FromQuery] string? type,
            [FromQuery] string? trustState,
            [FromQuery] bool? enabled,
            [FromQuery] string? runtimeState,
            [FromQuery] int? limit,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            if (!TryParseEnum(type, out PluginType? parsedType))
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.plugins",
                    eventType: "gateway.plugins.invalid_request",
                    level: "warning",
                    message: "Plugins list received an invalid type filter.");
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.plugin_type_invalid",
                    Message: "Plugin type filter is invalid."));
            }

            if (!TryParseEnum(trustState, out PluginTrustState? parsedTrustState))
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.plugins",
                    eventType: "gateway.plugins.invalid_request",
                    level: "warning",
                    message: "Plugins list received an invalid trust-state filter.");
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.plugin_trust_state_invalid",
                    Message: "Plugin trust-state filter is invalid."));
            }

            if (!TryParseEnum(runtimeState, out PluginRuntimeState? parsedRuntimeState))
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.plugins",
                    eventType: "gateway.plugins.invalid_request",
                    level: "warning",
                    message: "Plugins list received an invalid runtime-state filter.");
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.plugin_runtime_state_invalid",
                    Message: "Plugin runtime-state filter is invalid."));
            }

            var payload = await pluginGatewayService.ListAsync(
                new PluginQuery(
                    Type: parsedType,
                    TrustState: parsedTrustState,
                    Enabled: enabled,
                    RuntimeState: parsedRuntimeState,
                    Limit: NormalizePluginsLimit(limit)),
                cancellationToken);

            return Results.Ok(payload);
        });

        plugins.MapGet("/{id}", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IPluginGatewayService pluginGatewayService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var detail = await pluginGatewayService.GetDetailAsync(id, cancellationToken);
            if (detail is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.plugins",
                    eventType: "gateway.plugins.not_found",
                    level: "warning",
                    message: "Requested plugin was not found.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["pluginId"] = id,
                    });
                return Results.NotFound(new ErrorResponse(
                    Code: "plugin.not_found",
                    Message: "Plugin was not found."));
            }

            return Results.Ok(detail);
        });

        plugins.MapPost("/install-local", async (
            HttpContext context,
            InstallLocalPluginRequest request,
            IConfiguration configuration,
            IPluginGatewayService pluginGatewayService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            try
            {
                var detail = await pluginGatewayService.InstallLocalAsync(request, cancellationToken);
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.plugins",
                    eventType: "gateway.plugins.installed",
                    level: "info",
                    message: "Installed local plugin into workspace registry.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["pluginId"] = detail.Record.Id,
                        ["sourcePath"] = request.Path,
                    });
                return Results.Created($"/api/plugins/{detail.Record.Id}", detail);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is DirectoryNotFoundException or
                FileNotFoundException or
                ArgumentException or
                PluginManifestValidationException)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.plugins",
                    eventType: "gateway.plugins.invalid_request",
                    level: "warning",
                    message: ex.GetBaseException().Message);
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.plugin_install_invalid",
                    Message: ex.GetBaseException().Message));
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.plugins",
                    eventType: "gateway.plugins.conflict",
                    level: "warning",
                    message: ex.GetBaseException().Message);
                return Results.Conflict(new ErrorResponse(
                    Code: "plugin.state_conflict",
                    Message: ex.GetBaseException().Message));
            }
        });

        plugins.MapPost("/discover", async (
            HttpContext context,
            IConfiguration configuration,
            IPluginGatewayService pluginGatewayService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var payload = await pluginGatewayService.DiscoverAsync(cancellationToken);
            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.plugins",
                eventType: "gateway.plugins.discovered",
                level: "info",
                message: $"Plugin discovery scanned configured roots and returned {payload.Items.Count} items.",
                attributes: new Dictionary<string, string?>
                {
                    ["count"] = payload.Items.Count.ToString(),
                });
            return Results.Ok(payload);
        });

        plugins.MapPost("/{id}/trust", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IPluginGatewayService pluginGatewayService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var detail = await pluginGatewayService.TrustAsync(id, cancellationToken);
            if (detail is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.plugins",
                    eventType: "gateway.plugins.not_found",
                    level: "warning",
                    message: "Plugin trust targeted a missing plugin.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["pluginId"] = id,
                    });
                return Results.NotFound(new ErrorResponse(
                    Code: "plugin.not_found",
                    Message: "Plugin was not found."));
            }

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.plugins",
                eventType: "gateway.plugins.trusted",
                level: "info",
                message: "Plugin trust state updated.",
                attributes: new Dictionary<string, string?>
                {
                    ["pluginId"] = detail.Record.Id,
                    ["trustState"] = detail.Record.TrustState.ToString(),
                });
            return Results.Ok(detail);
        });

        plugins.MapPost("/{id}/enable", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IPluginGatewayService pluginGatewayService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var detail = await pluginGatewayService.SetEnabledAsync(id, enabled: true, cancellationToken);
            if (detail is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.plugins",
                    eventType: "gateway.plugins.not_found",
                    level: "warning",
                    message: "Plugin enable targeted a missing plugin.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["pluginId"] = id,
                    });
                return Results.NotFound(new ErrorResponse(
                    Code: "plugin.not_found",
                    Message: "Plugin was not found."));
            }

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.plugins",
                eventType: "gateway.plugins.enabled",
                level: "info",
                message: "Plugin enabled flag updated.",
                attributes: new Dictionary<string, string?>
                {
                    ["pluginId"] = detail.Record.Id,
                    ["enabled"] = detail.Record.Enabled.ToString(),
                });
            return Results.Ok(detail);
        });

        plugins.MapPost("/{id}/disable", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IPluginGatewayService pluginGatewayService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var detail = await pluginGatewayService.SetEnabledAsync(id, enabled: false, cancellationToken);
            if (detail is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.plugins",
                    eventType: "gateway.plugins.not_found",
                    level: "warning",
                    message: "Plugin disable targeted a missing plugin.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["pluginId"] = id,
                    });
                return Results.NotFound(new ErrorResponse(
                    Code: "plugin.not_found",
                    Message: "Plugin was not found."));
            }

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.plugins",
                eventType: "gateway.plugins.disabled",
                level: "info",
                message: "Plugin enabled flag cleared.",
                attributes: new Dictionary<string, string?>
                {
                    ["pluginId"] = detail.Record.Id,
                    ["enabled"] = detail.Record.Enabled.ToString(),
                    ["runtimeState"] = detail.Record.RuntimeState.ToString(),
                });
            return Results.Ok(detail);
        });

        plugins.MapPost("/{id}/start", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IPluginGatewayService pluginGatewayService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            try
            {
                var detail = await pluginGatewayService.StartAsync(id, cancellationToken);
                if (detail is null)
                {
                    RecordDiagnosticEvent(
                        diagnosticsService,
                        context,
                        source: "gateway.plugins",
                        eventType: "gateway.plugins.not_found",
                        level: "warning",
                        message: "Plugin start targeted a missing plugin.",
                        attributes: new Dictionary<string, string?>
                        {
                            ["pluginId"] = id,
                        });
                    return Results.NotFound(new ErrorResponse(
                        Code: "plugin.not_found",
                        Message: "Plugin was not found."));
                }

                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.plugins",
                    eventType: "gateway.plugins.started",
                    level: "info",
                    message: "Plugin lifecycle host started a plugin runtime.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["pluginId"] = detail.Record.Id,
                        ["runtimeState"] = detail.Record.RuntimeState.ToString(),
                        ["availableTools"] = detail.AvailableTools.Count.ToString(),
                    });
                return Results.Ok(detail);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.plugins",
                    eventType: "gateway.plugins.conflict",
                    level: "warning",
                    message: ex.GetBaseException().Message,
                    attributes: new Dictionary<string, string?>
                    {
                        ["pluginId"] = id,
                    });
                return Results.Conflict(new ErrorResponse(
                    Code: "plugin.state_conflict",
                    Message: ex.GetBaseException().Message));
            }
        });

        plugins.MapPost("/{id}/stop", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IPluginGatewayService pluginGatewayService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            try
            {
                var detail = await pluginGatewayService.StopAsync(id, cancellationToken);
                if (detail is null)
                {
                    RecordDiagnosticEvent(
                        diagnosticsService,
                        context,
                        source: "gateway.plugins",
                        eventType: "gateway.plugins.not_found",
                        level: "warning",
                        message: "Plugin stop targeted a missing plugin.",
                        attributes: new Dictionary<string, string?>
                        {
                            ["pluginId"] = id,
                        });
                    return Results.NotFound(new ErrorResponse(
                        Code: "plugin.not_found",
                        Message: "Plugin was not found."));
                }

                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.plugins",
                    eventType: "gateway.plugins.stopped",
                    level: "info",
                    message: "Plugin lifecycle host stopped a plugin runtime.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["pluginId"] = detail.Record.Id,
                        ["runtimeState"] = detail.Record.RuntimeState.ToString(),
                    });
                return Results.Ok(detail);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.plugins",
                    eventType: "gateway.plugins.conflict",
                    level: "warning",
                    message: ex.GetBaseException().Message,
                    attributes: new Dictionary<string, string?>
                    {
                        ["pluginId"] = id,
                    });
                return Results.Conflict(new ErrorResponse(
                    Code: "plugin.state_conflict",
                    Message: ex.GetBaseException().Message));
            }
        });

        plugins.MapGet("/{id}/logs", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IPluginGatewayService pluginGatewayService,
            IDiagnosticsService diagnosticsService,
            [FromQuery] int? limit,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var detail = await pluginGatewayService.GetDetailAsync(id, cancellationToken);
            if (detail is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.plugins",
                    eventType: "gateway.plugins.not_found",
                    level: "warning",
                    message: "Plugin logs targeted a missing plugin.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["pluginId"] = id,
                    });
                return Results.NotFound(new ErrorResponse(
                    Code: "plugin.not_found",
                    Message: "Plugin was not found."));
            }

            var items = await pluginGatewayService.GetLogsAsync(
                id,
                NormalizePluginLogsLimit(limit),
                cancellationToken);
            return Results.Ok(items);
        });

    }
}
