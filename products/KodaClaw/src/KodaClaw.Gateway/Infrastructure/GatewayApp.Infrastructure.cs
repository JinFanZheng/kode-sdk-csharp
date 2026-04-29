using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Gateway;
using KodaClaw.Runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

public static partial class GatewayApp
{
    private static HashSet<string> GetConfiguredCorsOrigins(IConfiguration configuration)
    {
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddConfiguredCorsOrigins(origins, configuration["KODACLAW_CORS_ALLOWED_ORIGINS"]);
        AddConfiguredCorsOrigins(origins, configuration["Gateway:CorsAllowedOrigins"]);

        foreach (var child in configuration.GetSection("Gateway:CorsAllowedOrigins").GetChildren())
        {
            AddConfiguredCorsOrigins(origins, child.Value);
        }

        return origins;
    }

    private static void AddConfiguredCorsOrigins(ISet<string> origins, string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return;
        }

        foreach (var candidate in rawValue.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = candidate.EndsWith("/", StringComparison.Ordinal)
                ? candidate.TrimEnd('/')
                : candidate;
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                origins.Add(normalized);
            }
        }
    }

    private static bool IsAllowedCorsOrigin(string? origin, IReadOnlySet<string> configuredOrigins)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        if (configuredOrigins.Contains(origin))
        {
            return true;
        }

        if (string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsLoopbackCorsOrigin(origin);
    }

    private static bool IsLoopbackCorsOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.IsLoopback && string.IsNullOrEmpty(uri.UserInfo);
    }

    private static string GetOrCreateCorrelationId(HttpContext context)
    {
        if (context.Items.TryGetValue(CorrelationHeaderName, out var existing) &&
            existing is string cached &&
            !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        if (context.Request.Headers.TryGetValue(CorrelationHeaderName, out var rawCorrelationId))
        {
            var supplied = rawCorrelationId.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(supplied))
            {
                return supplied;
            }
        }

        return string.IsNullOrWhiteSpace(context.TraceIdentifier)
            ? $"corr-{Guid.NewGuid():N}"
            : context.TraceIdentifier;
    }

    private static void RecordDiagnosticEvent(
        IDiagnosticsService diagnosticsService,
        HttpContext context,
        string source,
        string eventType,
        string level,
        string message,
        string? sessionId = null,
        IReadOnlyDictionary<string, string?>? attributes = null)
    {
        var mergedAttributes = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["method"] = context.Request.Method,
            ["path"] = context.Request.Path.Value,
        };

        if (attributes is not null)
        {
            foreach (var pair in attributes)
            {
                mergedAttributes[pair.Key] = pair.Value;
            }
        }

        diagnosticsService.Record(new DiagnosticEvent(
            Id: $"diag-{Guid.NewGuid():N}",
            Source: source,
            EventType: eventType,
            Level: level,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: GetOrCreateCorrelationId(context),
            SessionId: sessionId,
            Attributes: mergedAttributes));
    }

    private static bool IsRuntimeSnapshotReady(RuntimeConfigurationSnapshot? snapshot)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.DefaultModel))
        {
            return false;
        }

        var hasOpenAi = !string.IsNullOrWhiteSpace(snapshot.OpenAIApiKey);
        var hasAnthropic = !string.IsNullOrWhiteSpace(snapshot.AnthropicApiKey);
        if (!hasOpenAi && !hasAnthropic)
        {
            return false;
        }

        var model = snapshot.DefaultModel!;
        var looksLikeOpenAi = model.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o4", StringComparison.OrdinalIgnoreCase);
        var looksLikeAnthropic = model.StartsWith("claude", StringComparison.OrdinalIgnoreCase);

        if (hasOpenAi && !hasAnthropic)
        {
            return !looksLikeAnthropic;
        }

        if (hasAnthropic && !hasOpenAi)
        {
            return !looksLikeOpenAi;
        }

        return looksLikeOpenAi || looksLikeAnthropic;
    }

    private static bool LooksLikeRuntimeConfigurationError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("KodaClaw chat is not configured", StringComparison.Ordinal)
            || message.Contains("KODACLAW_DEFAULT_MODEL", StringComparison.Ordinal)
            || message.Contains("OPENAI_API_KEY", StringComparison.Ordinal)
            || message.Contains("ANTHROPIC_API_KEY", StringComparison.Ordinal);
    }

    private static bool TryAuthorize(HttpContext context, IConfiguration configuration)
    {
        var configuredToken = GetConfiguredGatewayToken(context, configuration);
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            // No token configured → open mode (Docker loopback or local dev without auth).
            return true;
        }

        if (!context.Request.Headers.TryGetValue("Authorization", out var rawAuthorization))
        {
            return false;
        }

        var authorization = rawAuthorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (!authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = authorization[bearerPrefix.Length..].Trim();
        return string.Equals(token, configuredToken, StringComparison.Ordinal);
    }

    private static bool TryAuthorize(HttpContext context, IConfiguration configuration, IDiagnosticsService diagnosticsService)
    {
        var result = TryAuthorize(context, configuration);
        if (!result)
        {
            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.auth",
                eventType: "gateway.auth.failed",
                level: "warning",
                message: $"Unauthorized access to {context.Request.Method} {context.Request.Path}.");
        }

        return result;
    }

    private static string? GetConfiguredGatewayToken(HttpContext context, IConfiguration configuration)
    {
        return context.RequestServices
            .GetService<GatewayAuthTokenAccessor>()?
            .GetConfiguredToken()
            ?? configuration["KODACLAW_GATEWAY_TOKEN"]
            ?? configuration["Gateway:Token"];
    }
}
