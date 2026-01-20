using System.Net;
using System.Text.Json;

namespace Kode.Agent.WebApiAssistant.Middleware;

/// <summary>
/// API Key 认证中间件
/// </summary>
public class ApiKeyAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _apiKey;
    private readonly ILogger<ApiKeyAuthenticationMiddleware> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ApiKeyAuthenticationMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        ILogger<ApiKeyAuthenticationMiddleware> logger)
    {
        _next = next;
        _apiKey = configuration["Kode:ApiKey"]
            ?? configuration["KODE_API_KEY"]
            ?? string.Empty;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 如果没有配置 API Key，跳过认证
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            await _next(context);
            return;
        }

        // 检查是否跳过认证路径
        if (ShouldSkipAuthentication(context.Request.Path))
        {
            await _next(context);
            return;
        }

        // 验证 API Key
        if (!TryAuthenticate(context.Request, out var errorResponse))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    message = "Unauthorized",
                    type = "authentication_error"
                }
            }, _jsonOptions);
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// 验证认证信息
    /// </summary>
    private bool TryAuthenticate(HttpRequest request, out object? errorResponse)
    {
        errorResponse = null;

        // 1. 检查 Authorization header
        if (request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var value = authHeader.ToString();
            if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = value["Bearer ".Length..].Trim();
                if (string.Equals(token, _apiKey, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        // 2. 检查查询参数
        if (request.Query.TryGetValue("api_key", out var queryKey))
        {
            if (string.Equals(queryKey, _apiKey, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // 3. 检查自定义 header
        if (request.Headers.TryGetValue("X-API-Key", out var headerKey))
        {
            if (string.Equals(headerKey, _apiKey, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 检查是否应该跳过认证
    /// </summary>
    private static bool ShouldSkipAuthentication(PathString path)
    {
        // 跳过 Swagger 和健康检查
        var skipPaths = new[]
        {
            "/swagger",
            "/healthz",
            "/"
        };

        return skipPaths.Any(skipPath => path.StartsWithSegments(skipPath));
    }
}

/// <summary>
/// 扩展方法
/// </summary>
public static class ApiKeyAuthenticationMiddlewareExtensions
{
    public static IApplicationBuilder UseApiKeyAuthentication(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ApiKeyAuthenticationMiddleware>();
    }
}
