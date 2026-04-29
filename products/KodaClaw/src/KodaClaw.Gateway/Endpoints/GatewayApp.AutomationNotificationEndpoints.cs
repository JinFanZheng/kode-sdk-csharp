using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

public static partial class GatewayApp
{
    private static void MapAutomationNotificationEndpoints(WebApplication app)
    {
        app.MapPost("/api/inbox/{id}/push-to-channel", async (
            string id,
            [FromBody] PushToChannelRequest? request,
            HttpContext context,
            IConfiguration configuration,
            IInboxRepository inboxRepository,
            IAutomationDefinitionRepository definitionRepository,
            IAutomationNotificationService notificationService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var inboxItem = await inboxRepository.GetByIdAsync(id, cancellationToken);
            if (inboxItem is null)
            {
                return Results.NotFound(new ErrorResponse(
                    Code: "inbox.item_not_found",
                    Message: "Inbox item was not found."));
            }

            // Extract automationId from PayloadJson
            string? automationId = null;
            if (!string.IsNullOrWhiteSpace(inboxItem.PayloadJson))
            {
                try
                {
                    var payload = JsonDocument.Parse(inboxItem.PayloadJson);
                    if (payload.RootElement.TryGetProperty("automationId", out var prop))
                    {
                        automationId = prop.GetString();
                    }
                }
                catch (JsonException ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AutomationNotificationEndpoints] Failed to parse inbox payload: {ex.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(automationId))
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "inbox.no_automation_id",
                    Message: "Inbox item has no automationId in payload."));
            }

            var definition = await definitionRepository.GetByIdAsync(automationId, cancellationToken);
            if (definition is null)
            {
                return Results.NotFound(new ErrorResponse(
                    Code: "automation.definition_not_found",
                    Message: $"Automation '{automationId}' not found."));
            }

            // Determine target binding IDs: request body takes priority, fallback to definition
            IReadOnlyList<string> bindingIds = request?.BindingIds is { Count: > 0 }
                ? request.BindingIds
                : definition.NotificationChannels ?? [];

            if (bindingIds.Count == 0)
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "automation.no_channel_bindings",
                    Message: "No channel binding IDs configured or provided."));
            }

            // Get summary text to push
            var text = inboxItem.Summary ?? "Automation run completed.";

            var results = await notificationService.PushAsync(bindingIds, text, cancellationToken);

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.automation",
                eventType: "gateway.automation.pushed_to_channel",
                level: "info",
                message: $"Pushed inbox item '{id}' to {results.Count} channel binding(s).",
                attributes: new Dictionary<string, string?>
                {
                    ["inboxItemId"] = id,
                    ["automationId"] = automationId,
                    ["bindingCount"] = results.Count.ToString(),
                    ["successCount"] = results.Count(r => r.Ok).ToString(),
                });

            return Results.Ok(new { results });
        });
    }
}

public sealed record PushToChannelRequest(IReadOnlyList<string>? BindingIds);
