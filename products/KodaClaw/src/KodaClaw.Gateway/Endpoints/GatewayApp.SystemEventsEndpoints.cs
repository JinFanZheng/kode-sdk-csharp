using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

public static partial class GatewayApp
{
    private static void MapSystemEventsEndpoints(WebApplication app)
    {
        app.MapGet("/api/events/stream", async (
            HttpContext context,
            IConfiguration configuration,
            IDiagnosticsService diagnosticsService,
            IInboxRepository inboxRepository,
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

            // Push initial snapshot immediately on connect
            await PushSystemStatsAsync(context, diagnosticsService, inboxRepository, token);

            long lastPushedMs = 0;
            const long ThrottleMs = 1_000; // at most one push per second

            try
            {
                await foreach (var _ in diagnosticsService.SubscribeAsync(token))
                {
                    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (nowMs - lastPushedMs < ThrottleMs) continue;
                    lastPushedMs = nowMs;
                    await PushSystemStatsAsync(context, diagnosticsService, inboxRepository, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // client disconnected or application shutting down
            }
        });
    }

    private static async Task PushSystemStatsAsync(
        HttpContext context,
        IDiagnosticsService diagnosticsService,
        IInboxRepository inboxRepository,
        CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-1);
        var stats = diagnosticsService.GetStats(since);
        var inboxItems = await inboxRepository.ListAsync(
            new InboxQuery(Status: InboxItemStatus.Open, Limit: 200),
            cancellationToken);

        var payload = new SystemStatsEvent(
            ErrorCount: stats.ErrorCount,
            WarningCount: stats.WarningCount,
            InboxUnreadCount: inboxItems.Count);

        var serialized = JsonSerializer.Serialize(payload, GatewayJson.Options);
        var id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        await context.Response.WriteAsync($"id: {id}\nevent: system.stats\ndata: {serialized}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }
}
