using System.Text.Json;
using System.Text.Json.Serialization;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Models;
using KodaClaw.Contracts.Secrets;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Runtime;
using KodaClaw.Workspace;
using KodaClaw.Workspace.Secrets;
using Microsoft.Extensions.Configuration;

namespace KodaClaw.Gateway;

internal static class RuntimeConfigurationBootstrap
{
    public static RuntimeConfigurationSnapshot Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var workspaceOptions = new KodaClawWorkspaceOptions
        {
            RootPath = configuration["KODACLAW_WORKSPACE_ROOT"] ?? configuration["Workspace:RootPath"],
        };
        var secretStore = PlatformSecretStore.CreateForCurrentPlatform(workspaceOptions);
        var directResult = ResolveDirectConfiguration(configuration, secretStore);
        if (!string.IsNullOrWhiteSpace(directResult.DefaultModel))
        {
            return directResult;
        }

        var defaultModel = TryLoadDefaultAccountModel(workspaceOptions);
        if (defaultModel is null || !defaultModel.AccountEnabled || !defaultModel.ModelEnabled)
        {
            return directResult;
        }

        var resolvedApiKey = ResolveAccountApiKey(defaultModel, secretStore, configuration);
        return defaultModel.ProviderKind switch
        {
            ModelProviderKind.OpenAI or ModelProviderKind.OpenAICompatible => directResult with
            {
                DefaultModel = defaultModel.ModelId,
                OpenAIApiKey = resolvedApiKey,
                OpenAIBaseUrl = NormalizeBaseUrl(defaultModel.BaseUrl),
                AnthropicApiKey = null,
                AnthropicBaseUrl = null,
            },
            ModelProviderKind.Anthropic or ModelProviderKind.AnthropicCompatible => directResult with
            {
                DefaultModel = defaultModel.ModelId,
                OpenAIApiKey = null,
                OpenAIBaseUrl = null,
                AnthropicApiKey = resolvedApiKey,
                AnthropicBaseUrl = NormalizeBaseUrl(defaultModel.BaseUrl),
            },
            ModelProviderKind.OpenAIResponses => directResult with
            {
                DefaultModel = defaultModel.ModelId,
                OpenAIApiKey = resolvedApiKey,
                OpenAIBaseUrl = NormalizeBaseUrl(defaultModel.BaseUrl),
                AnthropicApiKey = null,
                AnthropicBaseUrl = null,
            },
            _ => directResult,
        };
    }

    private static RuntimeConfigurationSnapshot ResolveDirectConfiguration(
        IConfiguration configuration,
        ISecretStore secretStore)
    {
        return new RuntimeConfigurationSnapshot(
            DefaultModel: Normalize(configuration["KODACLAW_DEFAULT_MODEL"] ?? configuration["Runtime:DefaultModel"]),
            OpenAIApiKey: ResolveSecretOrValue(
                configuration,
                secretStore,
                secretRefKeys: ["OPENAI_API_KEY_SECRET_REF", "Runtime:OpenAIApiKeySecretRef"],
                valueKeys: ["OPENAI_API_KEY", "Runtime:OpenAIApiKey"]),
            OpenAIBaseUrl: NormalizeBaseUrl(configuration["Runtime:OpenAIBaseUrl"]),
            AnthropicApiKey: ResolveSecretOrValue(
                configuration,
                secretStore,
                secretRefKeys: ["ANTHROPIC_API_KEY_SECRET_REF", "Runtime:AnthropicApiKeySecretRef"],
                valueKeys: ["ANTHROPIC_API_KEY", "Runtime:AnthropicApiKey"]),
            AnthropicBaseUrl: NormalizeBaseUrl(configuration["Runtime:AnthropicBaseUrl"]));
    }

    private static string? ResolveSecretOrValue(
        IConfiguration configuration,
        ISecretStore secretStore,
        IReadOnlyList<string> secretRefKeys,
        IReadOnlyList<string> valueKeys)
    {
        foreach (var secretRefKey in secretRefKeys)
        {
            var secretRefValue = Normalize(configuration[secretRefKey]);
            if (!SecretRef.TryParse(secretRefValue, out var secretRef))
            {
                continue;
            }

            var resolved = secretStore.GetAsync(secretRef).GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved.Trim();
            }
        }

        foreach (var valueKey in valueKeys)
        {
            var configuredValue = Normalize(configuration[valueKey]);
            if (!string.IsNullOrWhiteSpace(configuredValue))
            {
                return configuredValue;
            }
        }

        return null;
    }

    /// <summary>
    /// Scans config/accounts/ and config/account-models/ to find the global-default model
    /// and its owning account. Falls back to old config/models/ for migration compatibility.
    /// </summary>
    private static StoredDefaultModel? TryLoadDefaultAccountModel(KodaClawWorkspaceOptions workspaceOptions)
    {
        var rootPath = workspaceOptions.ResolveRootPath();
        var configDir = Path.Combine(rootPath, KodaClawWorkspaceLayout.ConfigDirectory);
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());

        // Try new format: config/accounts/ + config/account-models/
        var accountsDir = Path.Combine(configDir, "accounts");
        var modelsDir = Path.Combine(configDir, "account-models");

        if (Directory.Exists(accountsDir) && Directory.Exists(modelsDir))
        {
            var result = TryLoadFromNewFormat(accountsDir, modelsDir, jsonOptions);
            if (result is not null) return result;
        }

        // Fallback: old config/models/ format for migration compat
        var oldModelsDir = Path.Combine(configDir, "models");
        if (Directory.Exists(oldModelsDir))
        {
            return TryLoadFromLegacyFormat(oldModelsDir, jsonOptions);
        }

        return null;
    }

    private static StoredDefaultModel? TryLoadFromNewFormat(
        string accountsDir, string modelsDir, JsonSerializerOptions jsonOptions)
    {
        // Find the global-default model
        AccountModel? defaultModel = null;
        foreach (var file in Directory.EnumerateFiles(modelsDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var model = JsonSerializer.Deserialize<AccountModel>(json, jsonOptions);
                if (model is { IsGlobalDefault: true, Enabled: true })
                {
                    defaultModel = model;
                    break;
                }
            }
            catch { /* skip corrupt files */ }
        }

        if (defaultModel is null) return null;

        // Load the owning account
        var accountFile = Path.Combine(accountsDir, $"{defaultModel.AccountId}.json");
        if (!File.Exists(accountFile)) return null;

        try
        {
            var json = File.ReadAllText(accountFile);
            var account = JsonSerializer.Deserialize<ProviderAccount>(json, jsonOptions);
            if (account is null) return null;

            return new StoredDefaultModel(
                ProviderKind: account.ProviderKind,
                ModelId: defaultModel.ModelId,
                BaseUrl: account.BaseUrl,
                ApiKeyEnvironmentVariable: account.ApiKeyEnvironmentVariable,
                ApiKeySecretRef: account.ApiKeySecretRef,
                AccountEnabled: account.Enabled,
                ModelEnabled: defaultModel.Enabled);
        }
        catch { return null; }
    }

    private static StoredDefaultModel? TryLoadFromLegacyFormat(
        string modelsDir, JsonSerializerOptions jsonOptions)
    {
        foreach (var file in Directory.EnumerateFiles(modelsDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var isDefault = root.TryGetProperty("IsDefault", out var isDefaultProp)
                    ? isDefaultProp.GetBoolean()
                    : (root.TryGetProperty("isDefault", out isDefaultProp) && isDefaultProp.GetBoolean());

                if (!isDefault) continue;

                var providerStr = root.TryGetProperty("Provider", out var pProp)
                    ? pProp.GetString()
                    : (root.TryGetProperty("provider", out pProp) ? pProp.GetString() : null);

                if (!Enum.TryParse<ModelProviderKind>(providerStr, ignoreCase: true, out var provider))
                    continue;

                var modelId = root.TryGetProperty("ModelId", out var mProp)
                    ? mProp.GetString()
                    : (root.TryGetProperty("modelId", out mProp) ? mProp.GetString() : null);

                if (string.IsNullOrWhiteSpace(modelId)) continue;

                var baseUrl = root.TryGetProperty("BaseUrl", out var bProp)
                    ? bProp.GetString()
                    : (root.TryGetProperty("baseUrl", out bProp) ? bProp.GetString() : null);

                var apiKeyEnvVar = root.TryGetProperty("ApiKeyEnvironmentVariable", out var eProp)
                    ? eProp.GetString()
                    : (root.TryGetProperty("apiKeyEnvironmentVariable", out eProp) ? eProp.GetString() : null);

                var apiKeySecretRef = root.TryGetProperty("ApiKeySecretRef", out var sProp)
                    ? sProp.GetString()
                    : (root.TryGetProperty("apiKeySecretRef", out sProp) ? sProp.GetString() : null);

                var enabled = root.TryGetProperty("Enabled", out var enProp)
                    ? enProp.GetBoolean()
                    : (!root.TryGetProperty("enabled", out enProp) || enProp.GetBoolean());

                return new StoredDefaultModel(
                    ProviderKind: provider,
                    ModelId: modelId,
                    BaseUrl: baseUrl,
                    ApiKeyEnvironmentVariable: apiKeyEnvVar,
                    ApiKeySecretRef: apiKeySecretRef,
                    AccountEnabled: enabled,
                    ModelEnabled: enabled);
            }
            catch
            {
                // 忽略单文件解析失败，继续扫描其他文件
            }
        }

        return null;
    }

    private static string ResolveAccountApiKey(
        StoredDefaultModel model,
        ISecretStore secretStore,
        IConfiguration configuration)
    {
        if (SecretRef.TryParse(model.ApiKeySecretRef, out var secretRef))
        {
            var resolvedFromSecretStore = secretStore.GetAsync(secretRef).GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(resolvedFromSecretStore))
            {
                return resolvedFromSecretStore.Trim();
            }
        }

        var environmentVariable = Normalize(model.ApiKeyEnvironmentVariable);
        return string.IsNullOrWhiteSpace(environmentVariable)
            ? string.Empty
            : Normalize(Environment.GetEnvironmentVariable(environmentVariable) ?? configuration[environmentVariable]) ?? string.Empty;
    }

    private static string? Normalize(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static string? NormalizeBaseUrl(string? value)
    {
        var normalized = Normalize(value);
        return normalized?.TrimEnd('/');
    }

    private sealed record StoredDefaultModel(
        ModelProviderKind ProviderKind,
        string ModelId,
        string? BaseUrl,
        string? ApiKeyEnvironmentVariable,
        string? ApiKeySecretRef,
        bool AccountEnabled,
        bool ModelEnabled);
}
