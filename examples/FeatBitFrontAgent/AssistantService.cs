using System.Net;
using System.Text.Json;
using AgentImpl = Kode.Agent.Sdk.Core.Agent.Agent;
using Kode.Agent.Sdk.Core.Abstractions;
using AgentDependencies = Kode.Agent.Sdk.Core.Types.AgentDependencies;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Tools.Builtin;
using Kode.Agent.WebApiAssistant.Assistant;
using Kode.Agent.WebApiAssistant.OpenAI;
using Microsoft.Extensions.Configuration;

namespace Kode.Agent.WebApiAssistant;

public sealed class AssistantService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AgentDependencies _globalDeps;
    private readonly AssistantOptions _options;
    private readonly ILogger<AssistantService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;

    public AssistantService(
        AgentDependencies globalDeps,
        AssistantOptions options,
        ILogger<AssistantService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration)
    {
        _globalDeps = globalDeps;
        _options = options;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
    }

    public async Task HandleChatCompletionsAsync(HttpContext httpContext, OpenAiChatCompletionRequest request)
    {
        if (!Authorize(httpContext))
        {
            httpContext.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            await httpContext.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    message = "Unauthorized",
                    type = "authentication_error"
                }
            }, JsonOptions);
            return;
        }

        string? systemPrompt;
        string input;
        try
        {
            (systemPrompt, input) = ExtractPromptAndInput(request);
        }
        catch (BadHttpRequestException ex)
        {
            httpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    message = ex.Message,
                    type = "invalid_request_error"
                }
            }, JsonOptions);
            return;
        }

        var agentId = ResolveAgentId(httpContext, request);

        // 使用 AssistantBuilder 创建助手
        var createOptions = new CreateAssistantOptions
        {
            AgentId = agentId,
            UserId = request.User,
            WorkDir = _options.WorkDir,
            Model = _options.DefaultModel,
            SystemPrompt = systemPrompt ?? _options.DefaultSystemPrompt,
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
            Skills = _options.SkillsConfig,
            Permissions = _options.PermissionConfig
        };

        await using var agent = await AssistantBuilder.CreateAssistantAsync(
            createOptions,
            _globalDeps,
            _serviceProvider,
            _globalDeps.LoggerFactory!,
            httpContext.RequestAborted);

        if (request.Stream)
        {
            httpContext.Response.StatusCode = (int)HttpStatusCode.OK;
            httpContext.Response.ContentType = "text/event-stream";
            httpContext.Response.Headers["Cache-Control"] = "no-cache";
            httpContext.Response.Headers["X-Accel-Buffering"] = "no";

            await StreamAsOpenAiSseAsync(httpContext, agent, input, _options.DefaultModel!, agentId);
            return;
        }

        var result = await agent.RunAsync(input, httpContext.RequestAborted);

        var response = new OpenAiChatCompletionResponse
        {
            Id = "chatcmpl-" + Guid.NewGuid().ToString("N"),
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = _options.DefaultModel!,
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

        httpContext.Response.Headers["X-Kode-Agent-Id"] = agentId;
        await httpContext.Response.WriteAsJsonAsync(response, JsonOptions);
    }

    private bool Authorize(HttpContext httpContext)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)) return true;

        if (!httpContext.Request.Headers.TryGetValue("Authorization", out var auth))
        {
            return false;
        }

        var value = auth.ToString();
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var token = value["Bearer ".Length..].Trim();

        return string.Equals(token, _options.ApiKey, StringComparison.Ordinal);
    }

    private static (string? SystemPrompt, string Input) ExtractPromptAndInput(OpenAiChatCompletionRequest request)
    {
        if (request.Messages.Count == 0)
        {
            throw new BadHttpRequestException("messages is required");
        }

        var systemPrompts = request.Messages
            .Where(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.GetTextContent())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        var systemPrompt = systemPrompts.Count > 0 ? string.Join("\n", systemPrompts) : null;

        var lastNonSystem = request.Messages
            .LastOrDefault(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase));
        if (lastNonSystem == null)
        {
            throw new BadHttpRequestException("At least one non-system message is required");
        }

        if (!string.Equals(lastNonSystem.Role, "user", StringComparison.OrdinalIgnoreCase))
        {
            throw new BadHttpRequestException("The last non-system message must be a user message");
        }

        var input = lastNonSystem.GetTextContent();
        return (systemPrompt, input);
    }

    private static string ResolveAgentId(HttpContext httpContext, OpenAiChatCompletionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.User))
        {
            return request.User.Trim();
        }

        if (httpContext.Request.Headers.TryGetValue("X-Kode-Agent-Id", out var headerValue))
        {
            var id = headerValue.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(id)) return id;
        }

        return AssistantBuilder.GenerateAgentId();
    }

    private async Task StreamAsOpenAiSseAsync(
        HttpContext httpContext,
        AgentImpl agent,
        string input,
        string model,
        string agentId)
    {
        var streamId = "chatcmpl-" + Guid.NewGuid().ToString("N");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        httpContext.Response.Headers["X-Kode-Agent-Id"] = agentId;

        await WriteSseAsync(httpContext, new OpenAiChatCompletionChunk
        {
            Id = streamId,
            Created = created,
            Model = model,
            Choices =
            [
                new OpenAiChatCompletionChunkChoice
                {
                    Index = 0,
                    Delta = new OpenAiChatCompletionDelta { Role = "assistant" }
                }
            ]
        });

        try
        {
            await foreach (var envelope in agent.ChatStreamAsync(input, opts: null, cancellationToken: httpContext.RequestAborted))
            {
                switch (envelope.Event)
                {
                    case TextChunkEvent textChunk when !string.IsNullOrEmpty(textChunk.Delta):
                        await WriteSseAsync(httpContext, new OpenAiChatCompletionChunk
                        {
                            Id = streamId,
                            Created = created,
                            Model = model,
                            Choices =
                            [
                                new OpenAiChatCompletionChunkChoice
                                {
                                    Index = 0,
                                    Delta = new OpenAiChatCompletionDelta { Content = textChunk.Delta }
                                }
                            ]
                        });
                        break;

                    case DoneEvent done:
                        await WriteSseAsync(httpContext, new OpenAiChatCompletionChunk
                        {
                            Id = streamId,
                            Created = created,
                            Model = model,
                            Choices =
                            [
                                new OpenAiChatCompletionChunkChoice
                                {
                                    Index = 0,
                                    Delta = new OpenAiChatCompletionDelta(),
                                    FinishReason = "stop"
                                }
                            ]
                        });

                        await httpContext.Response.WriteAsync("data: [DONE]\n\n");
                        await httpContext.Response.Body.FlushAsync();
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected; align with TS assistant behavior: stop streaming without interrupting the agent.
            return;
        }
        catch (IOException)
        {
            // Connection aborted mid-write; ignore.
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
    }

    private static async Task WriteSseAsync(HttpContext httpContext, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await httpContext.Response.WriteAsync($"data: {json}\n\n");
        await httpContext.Response.Body.FlushAsync();
    }

    private static string MapFinishReason(StopReason stopReason)
    {
        return stopReason switch
        {
            StopReason.EndTurn => "stop",
            StopReason.MaxIterations => "length",
            StopReason.Cancelled => "stop",
            StopReason.AwaitingApproval => "stop",
            StopReason.Error => "stop",
            _ => "stop"
        };
    }
}
