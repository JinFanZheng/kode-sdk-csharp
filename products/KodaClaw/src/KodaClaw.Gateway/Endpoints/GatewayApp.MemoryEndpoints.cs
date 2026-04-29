using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Memory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

public static partial class GatewayApp
{
    private static void MapMemoryEndpoints(WebApplication app)
    {
        var memory = app.MapGroup("/api/memory");

        memory.MapGet("/stats", async (
            HttpContext context,
            IConfiguration configuration,
            IMemoryFileService memoryFileService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var stats = await memoryFileService.GetStatsAsync(cancellationToken);

            return Results.Ok(new
            {
                activeCount = stats.ActiveCount,
                dormantCount = stats.DormantCount,
                archivedCount = stats.ArchivedCount,
                topicsCount = stats.TopicsCount,
                sessionsCount = stats.SessionsCount,
            });
        });

        memory.MapGet("/entries", async (
            HttpContext context,
            IConfiguration configuration,
            IMemoryFileService memoryFileService,
            IDiagnosticsService diagnosticsService,
            string? status,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var entries = await memoryFileService.ListEntriesAsync(
                statusFilter: string.IsNullOrWhiteSpace(status) ? null : status,
                limit: limit ?? 100,
                cancellationToken: cancellationToken);

            var items = entries.Select(e => new
            {
                key = e.Key,
                title = e.Title,
                priority = e.Priority,
                status = e.Status,
                created = e.Created,
                sourcePath = e.SourcePath,
                tags = e.Tags,
            }).ToArray();

            return Results.Ok(new { count = items.Length, entries = items });
        });

        memory.MapPost("/entries/{key}/promote", async (
            HttpContext context,
            IConfiguration configuration,
            IMemoryFileService memoryFileService,
            IDiagnosticsService diagnosticsService,
            string key,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var promoted = await memoryFileService.PromoteEntryAsync(key, cancellationToken);
            if (!promoted)
            {
                return Results.NotFound(new { error = $"Memory entry '{key}' not found in dormant or archive." });
            }

            RecordDiagnosticEvent(diagnosticsService, context, source: "gateway.memory",
                eventType: "memory.entry_promoted", level: "info",
                message: $"Memory entry '{key}' promoted to active.");

            return Results.Ok(new { key, status = "active", message = "Entry promoted to active." });
        });
    }
}
