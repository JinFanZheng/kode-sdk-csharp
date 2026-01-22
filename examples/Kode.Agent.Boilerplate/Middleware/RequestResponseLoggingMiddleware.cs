using System.Diagnostics;
using System.Text;

namespace Kode.Agent.Boilerplate.Middleware;

/// <summary>
/// Middleware to log detailed HTTP request and response information
/// </summary>
public class RequestResponseLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestResponseLoggingMiddleware> _logger;

    public RequestResponseLoggingMiddleware(RequestDelegate next, ILogger<RequestResponseLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = Activity.Current?.Id ?? Guid.NewGuid().ToString();
        
        // Log request
        await LogRequestAsync(context, requestId);

        // Check if this is a streaming request (chat completions with stream=true)
        // Match both /v1/chat/completions and /{sessionId}/v1/chat/completions
        var isStreamingRequest = context.Request.Path.Value?.Contains("/v1/chat/completions") == true && 
                                 context.Request.Method == "POST";

        if (isStreamingRequest)
        {
            // For streaming requests, don't buffer the response - let it stream directly
            var stopwatch = Stopwatch.StartNew();
            try
            {
                _logger.LogInformation("[{RequestId}] Streaming request detected, bypassing response buffering", requestId);
                
                // Call the next middleware without buffering
                await _next(context);
                stopwatch.Stop();

                // Log minimal response info for streaming
                _logger.LogInformation("╔═══════════════════════════════════════════════════════════════");
                _logger.LogInformation("║ [{RequestId}] RESPONSE END ({ElapsedMs}ms) [STREAMING]", requestId, stopwatch.ElapsedMilliseconds);
                _logger.LogInformation("╠═══════════════════════════════════════════════════════════════");
                _logger.LogInformation("║ StatusCode: {StatusCode}", context.Response.StatusCode);
                _logger.LogInformation("║ ContentType: {ContentType}", context.Response.ContentType);
                _logger.LogInformation("║ Body: [SSE Stream - Not Buffered]");
                _logger.LogInformation("╚═══════════════════════════════════════════════════════════════");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "[{RequestId}] Exception during streaming request processing after {ElapsedMs}ms", 
                    requestId, stopwatch.ElapsedMilliseconds);
                throw;
            }
        }
        else
        {
            // For non-streaming requests, use the original buffering logic
            var originalBodyStream = context.Response.Body;

            using var responseBody = new MemoryStream();
            context.Response.Body = responseBody;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                // Call the next middleware
                await _next(context);
                stopwatch.Stop();

                // Log response
                await LogResponseAsync(context, requestId, stopwatch.ElapsedMilliseconds, responseBody);

                // Copy the response back to the original stream
                await responseBody.CopyToAsync(originalBodyStream);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "[{RequestId}] Exception during request processing after {ElapsedMs}ms", 
                    requestId, stopwatch.ElapsedMilliseconds);
                throw;
            }
            finally
            {
                context.Response.Body = originalBodyStream;
            }
        }
    }

    private async Task LogRequestAsync(HttpContext context, string requestId)
    {
        _logger.LogInformation("╔═══════════════════════════════════════════════════════════════");
        _logger.LogInformation("║ [{RequestId}] REQUEST START", requestId);
        _logger.LogInformation("╠═══════════════════════════════════════════════════════════════");
        _logger.LogInformation("║ Method: {Method}", context.Request.Method);
        _logger.LogInformation("║ Path: {Path}", context.Request.Path);
        _logger.LogInformation("║ QueryString: {QueryString}", context.Request.QueryString);
        _logger.LogInformation("║ Protocol: {Protocol}", context.Request.Protocol);
        _logger.LogInformation("║ ContentType: {ContentType}", context.Request.ContentType);
        _logger.LogInformation("║ ContentLength: {ContentLength}", context.Request.ContentLength);
        
        _logger.LogInformation("║ Headers:");
        foreach (var header in context.Request.Headers)
        {
            // Mask sensitive headers
            var value = header.Key.ToLowerInvariant() switch
            {
                "authorization" => "***MASKED***",
                "cookie" => "***MASKED***",
                _ => header.Value.ToString()
            };
            _logger.LogInformation("║   {Key}: {Value}", header.Key, value);
        }

        // Log request body for non-streaming requests
        if (context.Request.ContentLength > 0 && context.Request.ContentLength < 10000)
        {
            context.Request.EnableBuffering();
            var body = await ReadStreamAsync(context.Request.Body);
            context.Request.Body.Position = 0;
            
            _logger.LogInformation("║ Body:");
            _logger.LogInformation("║ {Body}", body);
        }
        
        _logger.LogInformation("╚═══════════════════════════════════════════════════════════════");
    }

    private async Task LogResponseAsync(HttpContext context, string requestId, long elapsedMs, MemoryStream responseBody)
    {
        _logger.LogInformation("╔═══════════════════════════════════════════════════════════════");
        _logger.LogInformation("║ [{RequestId}] RESPONSE END ({ElapsedMs}ms)", requestId, elapsedMs);
        _logger.LogInformation("╠═══════════════════════════════════════════════════════════════");
        _logger.LogInformation("║ StatusCode: {StatusCode}", context.Response.StatusCode);
        _logger.LogInformation("║ ContentType: {ContentType}", context.Response.ContentType);
        _logger.LogInformation("║ ContentLength: {ContentLength}", context.Response.ContentLength);
        
        _logger.LogInformation("║ Headers:");
        foreach (var header in context.Response.Headers)
        {
            _logger.LogInformation("║   {Key}: {Value}", header.Key, header.Value);
        }

        // Log response body for non-streaming responses
        if (responseBody.Length > 0 && responseBody.Length < 10000 && 
            context.Response.ContentType?.Contains("text/event-stream") != true)
        {
            responseBody.Seek(0, SeekOrigin.Begin);
            var body = await ReadStreamAsync(responseBody);
            responseBody.Seek(0, SeekOrigin.Begin);
            
            _logger.LogInformation("║ Body:");
            _logger.LogInformation("║ {Body}", body);
        }
        else if (context.Response.ContentType?.Contains("text/event-stream") == true)
        {
            _logger.LogInformation("║ Body: [SSE Stream - {Length} bytes]", responseBody.Length);
        }
        else if (responseBody.Length >= 10000)
        {
            _logger.LogInformation("║ Body: [Large Response - {Length} bytes]", responseBody.Length);
        }
        else
        {
            _logger.LogInformation("║ Body: [Empty]");
        }
        
        _logger.LogInformation("╚═══════════════════════════════════════════════════════════════");
    }

    private static async Task<string> ReadStreamAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}

/// <summary>
/// Extension methods for registering the middleware
/// </summary>
public static class RequestResponseLoggingMiddlewareExtensions
{
    public static IApplicationBuilder UseRequestResponseLogging(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<RequestResponseLoggingMiddleware>();
    }
}
