using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KodaClaw.Contracts;

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
            // Try as account model ID first, then as account ID
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
        catch (TaskCanceledException)
        {
            return new ModelConnectionTestResponse(false, (int)sw.ElapsedMilliseconds, modelId, "timeout");
        }
        catch (Exception ex)
        {
            return new ModelConnectionTestResponse(false, (int)sw.ElapsedMilliseconds, modelId, "network_error", ex.Message);
        }
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
