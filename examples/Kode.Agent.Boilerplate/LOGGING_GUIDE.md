# Logging and Diagnostics Guide

## Overview

This project has been enhanced with detailed OpenTelemetry-based logging capabilities to help you diagnose API request and response issues.

## New Features

### 1. Detailed Request/Response Logging

Through `RequestResponseLoggingMiddleware`, every HTTP request logs:

- **Request Information**:
  - HTTP method, path, query string
  - All request headers (sensitive information masked)
  - Request body content (less than 10KB)
  
- **Response Information**:
  - HTTP status code
  - Response headers
  - Response body content (less than 10KB, non-streaming responses)
  - Request processing time

### 2. Detailed Agent Execution Logs

Added detailed execution logs in `AssistantService`:

- Request reception and parameter extraction
- Agent creation/resume process
- Streaming response progress (logged every 10 chunks)
- Agent execution results for non-streaming responses
- Detailed stack traces for errors and exceptions

### 3. OpenTelemetry Integration

- Enabled Activity/Span tracing
- Supports Console and OTLP exporters
- Records trace information for key operations

## Log Level Configuration

The following log levels are configured in `appsettings.Development.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",                                    // Default Debug level
      "Microsoft.AspNetCore": "Information",                 // ASP.NET Core framework
      "Kode.Agent.Boilerplate": "Debug",                     // Main application code
      "Kode.Agent.Boilerplate.Middleware": "Information",    // HTTP middleware
      "Kode.Agent.Sdk": "Debug",                             // Agent SDK
      "Kode.Agent.Mcp": "Information"                        // MCP client
    }
  }
}
```

## How to Use

### Viewing Real-time Logs

After running the application, the console displays detailed logs with timestamps:

```
[14:45:56.123 INF] Program: Starting Kode.Agent Boilerplate
[14:45:56.456 INF] RequestResponseLoggingMiddleware: ╔═══════════════════════════════════════
[14:45:56.456 INF] RequestResponseLoggingMiddleware: ║ [abc123] REQUEST START
[14:45:56.456 INF] RequestResponseLoggingMiddleware: ║ Method: POST
[14:45:56.456 INF] RequestResponseLoggingMiddleware: ║ Path: /v1/chat/completions
```

### Viewing File Logs

Log files are saved in the `logs/` directory:

```
logs/
  kode-2026-01-22.log    # Today's logs
  kode-2026-01-21.log    # Yesterday's logs
```

### Diagnosing API Not Returning Content

Look for the following key log markers:

1. **Did the request arrive**:
   ```
   ║ [RequestId] REQUEST START
   ```

2. **Was the Agent created successfully**:
   ```
   Agent ready with session ID: agt_xxx
   ```

3. **Streaming response progress**:
   ```
   [Stream] Progress: 10 chunks, 1234 total chars
   ```

4. **Was the response sent**:
   ```
   ║ [RequestId] RESPONSE END (1234ms)
   ║ StatusCode: 200
   ```

5. **Are there any exceptions**:
   ```
   [ERR] Unhandled error handling chat completion: ...
   ```

### OpenTelemetry Configuration

Enable OTLP exporter (e.g., send to Jaeger):

```json
{
  "OpenTelemetry": {
    "Enabled": true,
    "ServiceName": "Kode.Agent.Boilerplate",
    "ServiceVersion": "1.0.0",
    "Exporter": "otlp",
    "OtlpEndpoint": "http://localhost:4317"
  }
}
```

Use Console exporter (development environment):

```json
{
  "OpenTelemetry": {
    "Enabled": true,
    "Exporter": "console"
  }
}
```

## Common Troubleshooting

### API Not Returning Content

Check the following logs:

1. **Did Agent.RunAsync complete**:
   ```
   [NonStream] Agent.RunAsync completed, StopReason: EndTurn, Response length: 0
   ```
   If Response length is 0, the Agent did not generate content.

2. **Does streaming response have chunks**:
   ```
   [Stream] Stream completed: 0 chunks, 0 total chars
   ```
   If chunks is 0, no content was received.

3. **Are there network/timeout errors**:
   ```
   Request cancelled by client
   ```

4. **Did response start sending**:
   ```
   Cannot send error response, response already started
   ```
   If you see this message, the response started but failed midway.

### Request Timeout

Look for timing information in logs:

```
║ [RequestId] RESPONSE END (30000ms)
```

If the duration is too long, it might be:
- Slow LLM API response
- Long tool execution time
- Network latency

### Reducing Log Volume

If there are too many logs, adjust the log level:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",                          // Change from Debug to Information
      "Kode.Agent.Boilerplate.Middleware": "Warning"     // Disable detailed request/response logs
    }
  }
}
```

## Performance Impact

- **Request/Response Logging**: Minimal impact on small requests (< 5ms), may have 10-50ms impact on large requests
- **OpenTelemetry**: Very small impact (< 1ms per operation)
- **Recommendation**: Set Middleware log level to `Warning` in production

## Next Steps

If you discover issues through logs:

1. Check if LLM Provider configuration is correct
2. Verify API Key is valid
3. Confirm network connection is working
4. Review Agent SDK detailed error messages

For more help, please provide complete log output.
