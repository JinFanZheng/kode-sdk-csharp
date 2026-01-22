using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Kode.Agent.Boilerplate.Models;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using static Kode.Agent.Sdk.Core.Abstractions.IEventBus;
using AgentImpl = Kode.Agent.Sdk.Core.Agent.Agent;

namespace Kode.Agent.Boilerplate;

/// <summary>
/// Core assistant service handling OpenAI-compatible chat completions
/// </summary>
public sealed class AssistantService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AgentDependencies _globalDeps;
    private readonly BoilerplateOptions _options;
    private readonly ILogger<AssistantService> _logger;
    private readonly ActivitySource _activitySource;

    public AssistantService(
        AgentDependencies globalDeps,
        BoilerplateOptions options,
        ILogger<AssistantService> logger,
        ActivitySource activitySource)
    {
        _globalDeps = globalDeps;
        _options = options;
        _logger = logger;
        _activitySource = activitySource;
    }

    public async Task HandleChatCompletionsAsync(
        HttpContext httpContext,
        OpenAiChatCompletionRequest request)
    {
        using var activity = _activitySource.StartActivity("HandleChatCompletion");
        activity?.SetTag("stream", request.Stream);
        activity?.SetTag("request.path", httpContext.Request.Path);
        activity?.SetTag("request.method", httpContext.Request.Method);

        _logger.LogInformation("=== Chat Completion Request Started ===");
        _logger.LogInformation("Request Path: {Path}, Method: {Method}, Stream: {Stream}",
            httpContext.Request.Path, httpContext.Request.Method, request.Stream);
        _logger.LogInformation("Request Headers: {Headers}",
            string.Join(", ", httpContext.Request.Headers.Select(h => $"{h.Key}={h.Value}")));
        _logger.LogInformation("Messages Count: {Count}", request.Messages?.Count ?? 0);

        try
        {
            // Extract system prompt and input
            _logger.LogInformation("Extracting prompt and input from {MessageCount} messages", request.Messages?.Count ?? 0);
            var (systemPrompt, input) = ExtractPromptAndInput(request);
            activity?.SetTag("input_length", input.Length);
            _logger.LogInformation("Extracted input length: {Length} chars", input.Length);
            if (!string.IsNullOrEmpty(systemPrompt))
            {
                _logger.LogInformation("System prompt length: {Length} chars", systemPrompt.Length);
            }

            // Get or create agent
            _logger.LogInformation("Getting or creating agent...");
            var (agent, sessionId) = await GetOrCreateAgentAsync(
                httpContext,
                systemPrompt,
                request.Temperature,
                request.MaxTokens,
                httpContext.RequestAborted);

            activity?.SetTag("session_id", sessionId);
            _logger.LogInformation("Agent ready with session ID: {SessionId}", sessionId);

            // Set response header
            httpContext.Response.Headers["X-Session-Id"] = sessionId;
            _logger.LogInformation("Set X-Session-Id response header: {SessionId}", sessionId);

            // Execute agent
            _logger.LogInformation("Executing agent, stream mode: {Stream}", request.Stream);
            await using (agent)
            {
                if (request.Stream)
                {
                    _logger.LogInformation("Starting streaming response for session: {SessionId}", sessionId);
                    await StreamResponseAsync(httpContext, agent, input, sessionId);
                    _logger.LogInformation("Streaming response completed for session: {SessionId}", sessionId);
                }
                else
                {
                    _logger.LogInformation("Starting non-streaming response for session: {SessionId}", sessionId);
                    await NonStreamResponseAsync(httpContext, agent, input);
                    _logger.LogInformation("Non-streaming response completed for session: {SessionId}", sessionId);
                }
            }
            _logger.LogInformation("=== Chat Completion Request Completed Successfully ===");
        }
        catch (BadHttpRequestException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Bad request: {Message}, StatusCode: {StatusCode}", ex.Message, ex.StatusCode);
            httpContext.Response.StatusCode = ex.StatusCode;
            await httpContext.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    message = ex.Message,
                    type = "invalid_request_error"
                }
            }, JsonOptions);
            _logger.LogInformation("=== Chat Completion Request Failed (Bad Request) ===");
        }
        catch (OperationCanceledException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Request cancelled");
            _logger.LogWarning(ex, "Request cancelled by client");
            _logger.LogInformation("=== Chat Completion Request Cancelled ===");
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Unhandled error handling chat completion: {Message}\nStackTrace: {StackTrace}", 
                ex.Message, ex.StackTrace);
            
            if (!httpContext.Response.HasStarted)
            {
                httpContext.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = new
                    {
                        message = "Internal server error",
                        type = "server_error",
                        details = ex.Message
                    }
                }, JsonOptions);
            }
            else
            {
                _logger.LogError("Cannot send error response, response already started");
            }
            _logger.LogInformation("=== Chat Completion Request Failed (Internal Error) ===");
        }
    }

    /// <summary>
    /// Get or create agent based on session ID
    /// </summary>
    private async Task<(AgentImpl Agent, string SessionId)> GetOrCreateAgentAsync(
        HttpContext httpContext,
        string? systemPrompt,
        double? temperature,
        int? maxTokens,
        CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity("GetOrCreateAgent");

        // Get session ID from request (URL path or headers)
        var sessionId = GetSessionIdFromRequest(httpContext);
        activity?.SetTag("session_id_provided", sessionId != null);

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            // Verify session exists
            if (!await _globalDeps.Store.ExistsAsync(sessionId, cancellationToken))
            {
                throw new BadHttpRequestException(
                    $"Session '{sessionId}' not found",
                    StatusCodes.Status404NotFound);
            }

            _logger.LogInformation("Resuming agent session: {SessionId}", sessionId);
            var agent = await CreateOrResumeAgentAsync(
                sessionId,
                systemPrompt,
                temperature,
                maxTokens,
                cancellationToken);

            return (agent, sessionId);
        }

        // Create new session
        var newSessionId = GenerateSessionId();
        _logger.LogInformation("Creating new agent session: {SessionId}", newSessionId);
        var newAgent = await CreateOrResumeAgentAsync(
            newSessionId,
            systemPrompt,
            temperature,
            maxTokens,
            cancellationToken);

        return (newAgent, newSessionId);
    }

    /// <summary>
    /// Get session ID from HTTP request
    /// Priority: URL path > X-Session-Id > X-Kode-Agent-Id
    /// </summary>
    private static string? GetSessionIdFromRequest(HttpContext httpContext)
    {
        // Try URL path parameter
        if (httpContext.Request.RouteValues.TryGetValue("sessionId", out var pathValue))
        {
            var sessionId = pathValue?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(sessionId))
            {
                return sessionId;
            }
        }

        // Try X-Session-Id header
        if (httpContext.Request.Headers.TryGetValue("X-Session-Id", out var sessionHeader))
        {
            var sessionId = sessionHeader.ToString()?.Trim();
            if (!string.IsNullOrEmpty(sessionId))
            {
                return sessionId;
            }
        }

        // Try X-Kode-Agent-Id header (backward compatibility)
        if (httpContext.Request.Headers.TryGetValue("X-Kode-Agent-Id", out var agentHeader))
        {
            var sessionId = agentHeader.ToString()?.Trim();
            if (!string.IsNullOrEmpty(sessionId))
            {
                return sessionId;
            }
        }

        return null;
    }

    /// <summary>
    /// Generate unique session ID
    /// </summary>
    private static string GenerateSessionId()
    {
        var chars = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Timestamp part (10 chars)
        var timePart = new char[10];
        var num = now;
        for (var i = 9; i >= 0; i--)
        {
            timePart[i] = chars[(int)(num % chars.Length)];
            num /= chars.Length;
        }

        // Random part (16 chars)
        var random = new char[16];
        var rand = new Random();
        for (var i = 0; i < 16; i++)
        {
            random[i] = chars[rand.Next(chars.Length)];
        }

        return $"agt_{new string(timePart)}{new string(random)}";
    }

    /// <summary>
    /// Create or resume agent
    /// </summary>
    private async Task<AgentImpl> CreateOrResumeAgentAsync(
        string sessionId,
        string? systemPrompt,
        double? temperature,
        int? maxTokens,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Creating/Resuming agent with Model: {Model}, Temperature: {Temp}, MaxTokens: {Max}",
            _options.DefaultModel, temperature, maxTokens);
            
        var config = new AgentConfig
        {
            Model = _options.DefaultModel,
            SystemPrompt = systemPrompt ?? _options.DefaultSystemPrompt,
            Temperature = temperature,
            MaxTokens = maxTokens,
            Tools = ["*"],
            Permissions = _options.PermissionConfig,
            Skills = _options.SkillsConfig,
            SandboxOptions = new SandboxOptions
            {
                WorkingDirectory = _options.WorkDir,
                EnforceBoundary = true,
                AllowPaths = [_options.WorkDir, _options.StoreDir]
            }
        };

        _logger.LogDebug("Agent Config: SystemPrompt length={Length}, Tools={Tools}, WorkDir={WorkDir}",
            config.SystemPrompt?.Length ?? 0, string.Join(",", config.Tools), config.SandboxOptions?.WorkingDirectory);

        // Resume if exists, otherwise create new
        if (await _globalDeps.Store.ExistsAsync(sessionId, cancellationToken))
        {
            _logger.LogInformation("Session exists, resuming agent: {SessionId}", sessionId);
            return await AgentImpl.ResumeFromStoreAsync(
                sessionId,
                _globalDeps,
                options: null,
                overrides: new AgentConfigOverrides
                {
                    Model = config.Model,
                    SystemPrompt = config.SystemPrompt,
                    Temperature = config.Temperature,
                    MaxTokens = config.MaxTokens,
                    Tools = config.Tools,
                    Permissions = config.Permissions,
                    Skills = config.Skills,
                    SandboxOptions = config.SandboxOptions
                },
                cancellationToken);
        }
        else
        {
            _logger.LogInformation("Session does not exist, creating new agent: {SessionId}", sessionId);
            var agent = await AgentImpl.CreateAsync(
                sessionId,
                config,
                _globalDeps,
                cancellationToken);
            _logger.LogInformation("New agent created successfully: {SessionId}", sessionId);
            return agent;
        }
    }

    /// <summary>
    /// Extract system prompt and user input from messages
    /// </summary>
    private static (string? SystemPrompt, string Input) ExtractPromptAndInput(OpenAiChatCompletionRequest request)
    {
        if (request.Messages == null || request.Messages.Count == 0)
        {
            throw new BadHttpRequestException("Messages array is required and must not be empty");
        }

        string? systemPrompt = null;
        var userMessages = new List<string>();

        foreach (var msg in request.Messages)
        {
            if (msg.Role == "system")
            {
                systemPrompt = msg.GetTextContent();
            }
            else if (msg.Role == "user")
            {
                userMessages.Add(msg.GetTextContent());
            }
        }

        if (userMessages.Count == 0)
        {
            throw new BadHttpRequestException("At least one user message is required");
        }

        var input = string.Join("\n\n", userMessages);
        return (systemPrompt, input);
    }

    /// <summary>
    /// Handle streaming response
    /// </summary>
    private async Task StreamResponseAsync(
        HttpContext httpContext,
        AgentImpl agent,
        string input,
        string sessionId)
    {
        using var activity = _activitySource.StartActivity("StreamResponse");
        activity?.SetTag("session_id", sessionId);

        _logger.LogInformation("[Stream] Setting up SSE response headers");
        httpContext.Response.StatusCode = (int)HttpStatusCode.OK;
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers["Cache-Control"] = "no-cache";
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";

        var streamId = $"chatcmpl-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        
        _logger.LogInformation("[Stream] Starting agent chat stream, StreamId: {StreamId}", streamId);
        var chunkCount = 0;
        var totalChars = 0;
        var toolEventCount = 0;

        await foreach (var envelope in agent.ChatStreamAsync(input, null, httpContext.RequestAborted))
        {
            // Log ALL event types received
            _logger.LogDebug("[Stream] Received event: {EventType}", envelope.Event?.GetType().Name ?? "NULL");
            
            // Process text chunk events
            if (envelope.Event is TextChunkEvent textChunk)
            {
                chunkCount++;
                totalChars += textChunk.Delta?.Length ?? 0;
                
                var sseChunk = new OpenAiStreamChunk
                {
                    Id = streamId,
                    Created = created,
                    Model = _options.DefaultModel,
                    Choices =
                    [
                        new OpenAiStreamChoice
                        {
                            Index = 0,
                            Delta = new OpenAiStreamDelta { Content = textChunk.Delta },
                            FinishReason = null
                        }
                    ]
                };

                var json = JsonSerializer.Serialize(sseChunk, JsonOptions);
                var sseData = $"data: {json}\n\n";
                
                // Log first few chunks to verify content
                if (chunkCount <= 3)
                {
                    _logger.LogInformation("[Stream] Chunk #{Count} content: {Content}", chunkCount, textChunk.Delta);
                    _logger.LogDebug("[Stream] SSE data: {SSE}", sseData.Replace("\n", "\\n"));
                }
                
                await httpContext.Response.WriteAsync(sseData, httpContext.RequestAborted);
                await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
                
                if (chunkCount % 10 == 0)
                {
                    _logger.LogInformation("[Stream] Progress: {Count} chunks, {Chars} total chars", chunkCount, totalChars);
                }
            }
            // Process tool start events
            else if (envelope.Event is ToolStartEvent toolStart)
            {
                toolEventCount++;
                _logger.LogInformation("[Stream] ✅ Tool started: {ToolName} (ID: {CallId})", 
                    toolStart.Call.Name, toolStart.Call.Id);
                
                var toolEvent = new OpenAiToolEvent
                {
                    Id = streamId,
                    Event = "tool:start",
                    ToolCallId = toolStart.Call.Id,
                    ToolName = toolStart.Call.Name,
                    State = toolStart.Call.State.ToString(),
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };

                var json = JsonSerializer.Serialize(toolEvent, JsonOptions);
                _logger.LogInformation("[Stream] Sending tool:start event: {Json}", json);
                await httpContext.Response.WriteAsync($"data: {json}\n\n", httpContext.RequestAborted);
                await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
            }
            // Process tool end events
            else if (envelope.Event is ToolEndEvent toolEnd)
            {
                toolEventCount++;
                _logger.LogInformation("[Stream] ✅ Tool completed: {ToolName} (ID: {CallId}, Duration: {Duration}ms)", 
                    toolEnd.Call.Name, toolEnd.Call.Id, toolEnd.Call.DurationMs ?? 0);
                
                var toolEvent = new OpenAiToolEvent
                {
                    Id = streamId,
                    Event = "tool:end",
                    ToolCallId = toolEnd.Call.Id,
                    ToolName = toolEnd.Call.Name,
                    State = toolEnd.Call.State.ToString(),
                    DurationMs = toolEnd.Call.DurationMs,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };

                var json = JsonSerializer.Serialize(toolEvent, JsonOptions);
                _logger.LogInformation("[Stream] Sending tool:end event: {Json}", json);
                await httpContext.Response.WriteAsync($"data: {json}\n\n", httpContext.RequestAborted);
                await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
            }
            // Process tool error events
            else if (envelope.Event is ToolErrorEvent toolError)
            {
                toolEventCount++;
                _logger.LogWarning("[Stream] ❌ Tool error: {ToolName} (ID: {CallId}) - {Error}", 
                    toolError.Call.Name, toolError.Call.Id, toolError.Error);
                
                var toolEvent = new OpenAiToolEvent
                {
                    Id = streamId,
                    Event = "tool:error",
                    ToolCallId = toolError.Call.Id,
                    ToolName = toolError.Call.Name,
                    State = toolError.Call.State.ToString(),
                    Error = toolError.Error,
                    DurationMs = toolError.Call.DurationMs,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };

                var json = JsonSerializer.Serialize(toolEvent, JsonOptions);
                _logger.LogInformation("[Stream] Sending tool:error event: {Json}", json);
                await httpContext.Response.WriteAsync($"data: {json}\n\n", httpContext.RequestAborted);
                await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
            }
            // TextChunkEndEvent marks the end of text streaming (optional event)
            else if (envelope.Event is TextChunkEndEvent)
            {
                _logger.LogDebug("[Stream] Received TextChunkEndEvent");
            }
            // Break on done event
            else if (envelope.Event is DoneEvent)
            {
                _logger.LogInformation("[Stream] Received DoneEvent, breaking stream loop");
                break;
            }
            else
            {
                _logger.LogWarning("[Stream] ⚠️ Unhandled event type: {EventType}", envelope.Event?.GetType().Name ?? "NULL");
            }
        }
        
        _logger.LogInformation("[Stream] Stream completed: {TextChunks} text chunks ({Chars} chars), {ToolEvents} tool events", 
            chunkCount, totalChars, toolEventCount);

        // Send final chunk
        _logger.LogInformation("[Stream] Sending final chunk and [DONE] marker");
        var finalChunk = new OpenAiStreamChunk
        {
            Id = streamId,
            Created = created,
            Model = _options.DefaultModel,
            Choices =
            [
                new OpenAiStreamChoice
                {
                    Index = 0,
                    Delta = new OpenAiStreamDelta(),
                    FinishReason = "stop"
                }
            ]
        };

        var finalJson = JsonSerializer.Serialize(finalChunk, JsonOptions);
        await httpContext.Response.WriteAsync($"data: {finalJson}\n\n", httpContext.RequestAborted);
        await httpContext.Response.WriteAsync("data: [DONE]\n\n", httpContext.RequestAborted);
        await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
        _logger.LogInformation("[Stream] SSE stream finalized");
    }

    /// <summary>
    /// Handle non-streaming response
    /// </summary>
    private async Task NonStreamResponseAsync(
        HttpContext httpContext,
        AgentImpl agent,
        string input)
    {
        using var activity = _activitySource.StartActivity("NonStreamResponse");

        _logger.LogInformation("[NonStream] Calling agent.RunAsync with input length: {Length}", input.Length);
        var result = await agent.RunAsync(input, httpContext.RequestAborted);
        
        _logger.LogInformation("[NonStream] Agent.RunAsync completed, StopReason: {Reason}, Response length: {Length}",
            result.StopReason, result.Response?.Length ?? 0);
        
        if (string.IsNullOrEmpty(result.Response))
        {
            _logger.LogWarning("[NonStream] Agent returned empty response!");
        }

        var response = new OpenAiChatCompletionResponse
        {
            Id = $"chatcmpl-{Guid.NewGuid():N}",
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = _options.DefaultModel,
            Choices =
            [
                new OpenAiChatCompletionChoice
                {
                    Index = 0,
                    FinishReason = MapFinishReason(result.StopReason),
                    Message = new OpenAiChatCompletionMessage
                    {
                        Role = "assistant",
                        Content = result.Response ?? ""
                    }
                }
            ],
            Usage = new OpenAiUsage
            {
                PromptTokens = 0,
                CompletionTokens = 0,
                TotalTokens = 0
            }
        };

        _logger.LogInformation("[NonStream] Sending JSON response, ResponseId: {Id}, MessageLength: {Length}",
            response.Id, response.Choices?[0].Message?.Content?.Length ?? 0);
        await httpContext.Response.WriteAsJsonAsync(response, JsonOptions);
        _logger.LogInformation("[NonStream] JSON response sent successfully");
    }

    private static string MapFinishReason(StopReason stopReason) => stopReason switch
    {
        StopReason.EndTurn => "stop",
        StopReason.MaxIterations => "length",
        StopReason.AwaitingApproval => "stop",
        StopReason.Cancelled => "stop",
        StopReason.Error => "stop",
        _ => "stop"
    };
}
