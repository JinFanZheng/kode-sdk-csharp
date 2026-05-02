using System.ClientModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Models;
using KodaClaw.Contracts.Secrets;
using OpenAI;
using OpenAI.Chat;

namespace KodaClaw.Gateway;

public sealed class ModelConnectionTestService(
    ModelPresetService presetService,
    IProviderAccountRepository accountRepo,
    ISecretStore secretStore,
    IHttpClientFactory httpClientFactory)
{
    private static readonly Dictionary<string, string> ProviderBaseUrls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Anthropic"] = "https://api.anthropic.com/v1",
        ["OpenAI"] = "https://api.openai.com/v1",
        ["OpenAIResponses"] = "https://api.openai.com/v1",
        ["DeepSeek"] = "https://api.deepseek.com/v1",
        ["Google"] = "https://generativelanguage.googleapis.com/v1beta/openai",
        ["Ollama"] = "http://localhost:11434/v1",
    };

    public async Task<ModelConnectionTestResponse> TestAsync(ModelConnectionTestRequest request, CancellationToken ct)
    {
        string? modelId = request.ModelId;
        string? baseUrl = request.BaseUrl;
        string? provider = request.Provider;
        string apiKey = request.ApiKey;

        // When EndpointId is provided, resolve from account repository and look up stored key
        if (!string.IsNullOrEmpty(request.EndpointId))
        {
            var accountModel = await accountRepo.GetModelByIdAsync(request.EndpointId, ct);
            if (accountModel is not null)
            {
                modelId ??= accountModel.ModelId;
                var account = await accountRepo.GetAccountByIdAsync(accountModel.AccountId, ct);
                if (account is not null)
                {
                    baseUrl ??= account.BaseUrl;
                    provider ??= account.ProviderKind.ToString();
                    if (string.IsNullOrEmpty(apiKey))
                        apiKey = await ResolveApiKeyAsync(account, ct) ?? string.Empty;
                }
            }
        }

        if (!string.IsNullOrEmpty(request.PresetId))
        {
            var preset = presetService.GetById(request.PresetId);
            if (preset != null)
            {
                modelId ??= preset.ModelId;
                baseUrl ??= preset.BaseUrl;
                provider ??= preset.Provider;
            }
        }

        if (string.IsNullOrEmpty(modelId))
            return new ModelConnectionTestResponse(false, 0, null, "model_id_required");

        // Determine base URL
        if (string.IsNullOrEmpty(baseUrl) && !string.IsNullOrEmpty(provider))
            ProviderBaseUrls.TryGetValue(provider, out baseUrl);

        if (string.IsNullOrEmpty(baseUrl))
            baseUrl = "https://api.openai.com/v1";

        var sw = Stopwatch.StartNew();
        try
        {
            return provider switch
            {
                "Anthropic" => await TestAnthropicSdkAsync(modelId, baseUrl!, apiKey, sw, ct),
                "AnthropicCompatible" => await TestAnthropicRawAsync(modelId, baseUrl!, apiKey, sw, ct),
                "OpenAI" or "OpenAICompatible"
                    => await TestOpenAIAsync(modelId, baseUrl!, apiKey, sw, ct),
                _ => await TestRawHttpAsync(modelId, baseUrl!, apiKey, provider, sw, ct)
            };
        }
        catch (TaskCanceledException)
        {
            return new ModelConnectionTestResponse(false, (int)sw.ElapsedMilliseconds, modelId, "timeout");
        }
        catch (Exception ex)
        {
            return new ModelConnectionTestResponse(false, (int)sw.ElapsedMilliseconds, modelId, "network_error", ex.Message);
        }
    }

    private async Task<ModelConnectionTestResponse> TestAnthropicSdkAsync(
        string modelId, string baseUrl, string apiKey, Stopwatch sw, CancellationToken ct)
    {
        var client = new AnthropicClient
        {
            ApiKey = apiKey,
            BaseUrl = baseUrl.Replace("/v1", "").TrimEnd('/')
        };

        var parameters = new MessageCreateParams
        {
            Model = modelId,
            Messages = [new MessageParam { Role = Role.User, Content = "Hi" }],
            MaxTokens = 1
        };

        try
        {
            await client.Messages.Create(parameters, ct);
            sw.Stop();
            return new ModelConnectionTestResponse(true, (int)sw.ElapsedMilliseconds, modelId, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var (errorCode, errorMessage) = MapAnthropicError(ex);
            return new ModelConnectionTestResponse(false, (int)sw.ElapsedMilliseconds, modelId, errorCode, errorMessage);
        }
    }

    private async Task<ModelConnectionTestResponse> TestAnthropicRawAsync(
        string modelId, string baseUrl, string apiKey, Stopwatch sw, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);

        var requestBody = JsonSerializer.Serialize(new
        {
            model = modelId,
            messages = new[] { new { role = "user", content = "Hi" } },
            max_tokens = 512  // Reasoning models (e.g. deepseek-reasoner) require a minimum budget; 1 is too low and causes "thinking content must be passed back" errors.
        });

        var normalizedBase = baseUrl.TrimEnd('/');
        if (!normalizedBase.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            normalizedBase += "/v1";
        var endpoint = $"{normalizedBase}/messages";

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, ct);
        sw.Stop();

        if (resp.IsSuccessStatusCode)
            return new ModelConnectionTestResponse(true, (int)sw.ElapsedMilliseconds, modelId, null);

        var errorCode = resp.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "authentication_error",
            System.Net.HttpStatusCode.Forbidden => "permission_denied",
            System.Net.HttpStatusCode.NotFound => "model_not_found",
            System.Net.HttpStatusCode.TooManyRequests => "rate_limit_exceeded",
            _ => $"http_{(int)resp.StatusCode}"
        };

        var errorMessage = await TryExtractErrorMessageAsync(resp, ct);
        return new ModelConnectionTestResponse(false, (int)sw.ElapsedMilliseconds, modelId, errorCode, errorMessage);
    }

    private async Task<ModelConnectionTestResponse> TestOpenAIAsync(
        string modelId, string baseUrl, string apiKey, Stopwatch sw, CancellationToken ct)
    {
        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(baseUrl.TrimEnd('/')) };
        var openaiClient = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
        var chatClient = openaiClient.GetChatClient(modelId);

        try
        {
            await chatClient.CompleteChatAsync([new UserChatMessage("Hi")], new ChatCompletionOptions { MaxOutputTokenCount = 1 }, ct);
            sw.Stop();
            return new ModelConnectionTestResponse(true, (int)sw.ElapsedMilliseconds, modelId, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var (errorCode, errorMessage) = MapOpenAIError(ex);
            return new ModelConnectionTestResponse(false, (int)sw.ElapsedMilliseconds, modelId, errorCode, errorMessage);
        }
    }

    private async Task<ModelConnectionTestResponse> TestRawHttpAsync(
        string modelId, string baseUrl, string apiKey, string? provider, Stopwatch sw, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        bool isResponsesApi = string.Equals(provider, "OpenAIResponses", StringComparison.OrdinalIgnoreCase);

        string requestBody;
        string endpoint;
        if (isResponsesApi)
        {
            requestBody = JsonSerializer.Serialize(new
            {
                model = modelId,
                input = new[] { new { role = "user", content = "Hi" } },
                max_output_tokens = 1
            });
            endpoint = $"{baseUrl.TrimEnd('/')}/responses";
        }
        else
        {
            requestBody = JsonSerializer.Serialize(new
            {
                model = modelId,
                messages = new[] { new { role = "user", content = "Hi" } },
                max_tokens = 1
            });
            endpoint = $"{baseUrl.TrimEnd('/')}/chat/completions";
        }

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpReq.Headers.Add("Authorization", $"Bearer {apiKey}");
        httpReq.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(httpReq, ct);
        sw.Stop();

        if (response.IsSuccessStatusCode)
            return new ModelConnectionTestResponse(true, (int)sw.ElapsedMilliseconds, modelId, null);

        var errorCode = response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => "authentication_error",
            System.Net.HttpStatusCode.Forbidden => "permission_denied",
            System.Net.HttpStatusCode.TooManyRequests => "rate_limit_exceeded",
            System.Net.HttpStatusCode.NotFound => "model_not_found",
            _ => $"http_{(int)response.StatusCode}"
        };

        var errorMessage = await TryExtractErrorMessageAsync(response, ct);
        return new ModelConnectionTestResponse(false, (int)sw.ElapsedMilliseconds, modelId, errorCode, errorMessage);
    }

    private static (string ErrorCode, string? ErrorMessage) MapAnthropicError(Exception ex)
    {
        // Anthropic SDK: AnthropicUnauthorizedException (401), AnthropicForbiddenException (403),
        // AnthropicNotFoundException (404), AnthropicRateLimitException (429), Anthropic5xxException, etc.
        // All inherit from AnthropicApiException with a StatusCode property.
        if (ex is Anthropic.Exceptions.AnthropicApiException apiEx)
        {
            var code = (int)apiEx.StatusCode switch
            {
                401 => "authentication_error",
                403 => "permission_denied",
                404 => "model_not_found",
                429 => "rate_limit_exceeded",
                _ => $"http_{(int)apiEx.StatusCode}"
            };
            return (code, apiEx.Message);
        }

        return ("network_error", ex.Message);
    }

    private static (string ErrorCode, string? ErrorMessage) MapOpenAIError(Exception ex)
    {
        return ex switch
        {
            System.ClientModel.ClientResultException cre => cre.Status switch
            {
                401 => ("authentication_error", cre.Message),
                403 => ("permission_denied", cre.Message),
                404 => ("model_not_found", cre.Message),
                429 => ("rate_limit_exceeded", cre.Message),
                _ => ($"http_{cre.Status}", cre.Message)
            },
            HttpRequestException hre => hre.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => ("authentication_error", hre.Message),
                System.Net.HttpStatusCode.Forbidden => ("permission_denied", hre.Message),
                System.Net.HttpStatusCode.NotFound => ("model_not_found", hre.Message),
                System.Net.HttpStatusCode.TooManyRequests => ("rate_limit_exceeded", hre.Message),
                _ => ($"http_{(int?)hre.StatusCode}", hre.Message)
            },
            _ => ("network_error", ex.Message)
        };
    }

    private async Task<string?> ResolveApiKeyAsync(ProviderAccount account, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(account.ApiKeySecretRef) &&
            SecretRef.TryParse(account.ApiKeySecretRef, out var secretRef))
        {
            var secret = await secretStore.GetAsync(secretRef, ct);
            if (!string.IsNullOrWhiteSpace(secret))
                return secret;
        }

        if (!string.IsNullOrWhiteSpace(account.ApiKeyEnvironmentVariable))
        {
            var envVal = Environment.GetEnvironmentVariable(account.ApiKeyEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(envVal))
                return envVal;
        }

        return null;
    }

    private static async Task<string?> TryExtractErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) return null;

            // OpenAI / Anthropic / most compatible APIs: {"error": {"message": "..."}}
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var errEl))
            {
                if (errEl.ValueKind == JsonValueKind.Object &&
                    errEl.TryGetProperty("message", out var msgEl))
                    return msgEl.GetString();

                // Some APIs put the message directly as a string in "error"
                if (errEl.ValueKind == JsonValueKind.String)
                    return errEl.GetString();
            }

            // Fallback: return raw body if short enough to be readable
            if (body.Length <= 300) return body;
        }
        catch
        {
            // Ignore parse errors — error message is best-effort
        }

        return null;
    }
}
