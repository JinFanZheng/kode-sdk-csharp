using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

public static partial class GatewayApp
{
    private static int NormalizeDiagnosticsLimit(int? requestedLimit)
    {
        if (requestedLimit is null)
        {
            return DefaultDiagnosticsLimit;
        }

        return Math.Clamp(requestedLimit.Value, 1, MaxDiagnosticsLimit);
    }

    private static int NormalizeApprovalsLimit(int? requestedLimit)
    {
        if (requestedLimit is null)
        {
            return DefaultApprovalsLimit;
        }

        return Math.Clamp(requestedLimit.Value, 1, MaxApprovalsLimit);
    }

    private static IResult QueryDiagnosticsEndpoint(
        HttpContext context,
        IConfiguration configuration,
        IDiagnosticsService diagnosticsService,
        string endpointName,
        int? limit,
        string? correlationId,
        string? sessionId,
        string? source,
        string? eventType,
        string[]? levels,
        DateTimeOffset? dateFrom = null,
        DateTimeOffset? dateTo = null)
    {
        if (!TryAuthorize(context, configuration))
        {
            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.auth",
                eventType: "gateway.auth.failed",
                level: "warning",
                message: $"Unauthorized access to {endpointName} endpoint.");
            return Results.Unauthorized();
        }

        var query = new DiagnosticsQuery(
            Limit: NormalizeDiagnosticsLimit(limit),
            CorrelationId: correlationId,
            SessionId: sessionId,
            Source: source,
            EventType: eventType,
            Levels: levels,
            DateFrom: dateFrom,
            DateTo: dateTo);

        return Results.Ok(new DiagnosticsQueryResponse(
            diagnosticsService.Query(query)));
    }

    private static int NormalizeInboxLimit(int? requestedLimit)
    {
        if (requestedLimit is null)
        {
            return DefaultInboxLimit;
        }

        return Math.Clamp(requestedLimit.Value, 1, MaxInboxLimit);
    }

    private static int NormalizeSessionsLimit(int? requestedLimit)
    {
        if (requestedLimit is null)
        {
            return DefaultSessionsLimit;
        }

        return Math.Clamp(requestedLimit.Value, 1, MaxSessionsLimit);
    }

    private static int NormalizeAutomationDefinitionsLimit(int? requestedLimit)
    {
        if (requestedLimit is null)
        {
            return DefaultAutomationDefinitionsLimit;
        }

        return Math.Clamp(requestedLimit.Value, 1, MaxAutomationDefinitionsLimit);
    }

    private static int NormalizeAutomationRunsLimit(int? requestedLimit)
    {
        if (requestedLimit is null)
        {
            return DefaultAutomationRunsLimit;
        }

        return Math.Clamp(requestedLimit.Value, 1, MaxAutomationRunsLimit);
    }

    private static int NormalizePluginsLimit(int? requestedLimit)
    {
        if (requestedLimit is null)
        {
            return DefaultPluginsLimit;
        }

        return Math.Clamp(requestedLimit.Value, 1, MaxPluginsLimit);
    }

    private static int NormalizePluginLogsLimit(int? requestedLimit)
    {
        if (requestedLimit is null)
        {
            return DefaultPluginLogsLimit;
        }

        return Math.Clamp(requestedLimit.Value, 1, MaxPluginLogsLimit);
    }

    private static int NormalizeCanvasLimit(int? requestedLimit)
    {
        if (requestedLimit is null)
        {
            return DefaultCanvasLimit;
        }

        return Math.Clamp(requestedLimit.Value, 1, MaxCanvasLimit);
    }

    private static int NormalizeChannelsLimit(int? requestedLimit)
    {
        if (requestedLimit is null)
        {
            return DefaultChannelsLimit;
        }

        return Math.Clamp(requestedLimit.Value, 1, MaxChannelsLimit);
    }

    private static bool TryParseEnum<TEnum>(string? rawValue, out TEnum? parsed)
        where TEnum : struct, Enum
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return true;
        }

        if (!Enum.TryParse<TEnum>(rawValue, ignoreCase: true, out var value))
        {
            return false;
        }

        parsed = value;
        return true;
    }
}
