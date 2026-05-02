using System.Text.RegularExpressions;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Models;
using KodaClaw.Contracts.Secrets;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.Runtime.Tools;

/// <summary>
/// Agent tool for managing provider accounts and models at runtime.
/// Supports listing, adding accounts, setting a default model, and deleting accounts.
/// API keys are stored securely in the OS keychain via ISecretStore.
/// </summary>
public sealed class ConfigUpdateTool : ToolBase<ConfigUpdateArgs>
{
    private readonly IProviderAccountRepository _repo;
    private readonly ISecretStore _secretStore;
    private readonly IDiagnosticsService? _diagnosticsService;

    public ConfigUpdateTool(
        IProviderAccountRepository repo,
        ISecretStore secretStore,
        IDiagnosticsService? diagnosticsService = null)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _diagnosticsService = diagnosticsService;
    }

    public override string Name => "config_update";

    public override string Description =>
        "Manage KodaClaw model provider configuration.\n\n" +
        "Actions:\n" +
        "- list: Show all configured provider accounts and their models.\n" +
        "- add: Add a new provider account with a model. Requires provider, modelId, apiKey. " +
        "Use this first to create an account, then use add_model to append additional models.\n" +
        "- add_model: Append a model to an existing account. Requires endpointId (account ID) and modelId. " +
        "Does NOT need provider, apiKey, or baseUrl.\n" +
        "- set_default: Change the default model. Requires endpointId (model ID).\n" +
        "- delete: Remove a provider account and its models. Requires endpointId (account ID).\n\n" +
        "Token size format (contextWindowSize, maxOutputTokens): " +
        "Use human-friendly notation like '128k', '200k', '1m', '2m', or a plain number like '131072'. " +
        "Suffix 'k' = ×1024, 'm' = ×1_000_000. Omit to use defaults.\n\n" +
        "Capabilities (for add/add_model): Comma-separated from Text, Image, Video, Audio, File. " +
        "Default: 'Text'. Example: 'Text,Image'.\n\n" +
        "IsReasoning: Set true for reasoning-focused models (e.g. o1, DeepSeek-R1). Default: false.\n" +
        "SupportsToolCalling: Set false for models that lack tool/function calling. Default: true.\n\n" +
        "Tip: Before configuring, check the model's documentation to confirm its context window size.\n" +
        "Provider values: 'Anthropic', 'AnthropicCompatible', 'OpenAI', 'OpenAICompatible'.\n" +
        "API keys are stored securely in the OS keychain and never appear in plain text on disk.\n\n" +
        "Actions update only the fields you pass; omitted fields keep their current values.\n" +
        "- update_model: Modify a model's settings. Requires endpointId (model ID). " +
        "Optional: displayName, contextWindowSize, maxOutputTokens, capabilities, isReasoning, supportsToolCalling.\n" +
        "- update_account: Modify an account's settings. Requires endpointId (account ID). " +
        "Optional: displayName, baseUrl, enabled, apiKey (to rotate the API key).";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<ConfigUpdateArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = false,
    };

    protected override async Task<ToolResult> ExecuteAsync(
        ConfigUpdateArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        return args.Action?.ToLowerInvariant() switch
        {
            "list" => await ListAsync(cancellationToken),
            "add"  => await AddAsync(args, cancellationToken),
            "add_model" => await AddModelAsync(args, cancellationToken),
            "set_default" => await SetDefaultAsync(args, cancellationToken),
            "delete" => await DeleteAsync(args, cancellationToken),
            "update_model" => await UpdateModelAsync(args, cancellationToken),
            "update_account" => await UpdateAccountAsync(args, cancellationToken),
            _ => ToolResult.Fail($"Unknown action '{args.Action}'. Valid values: list, add, add_model, set_default, delete, update_model, update_account."),
        };
    }

    private async Task<ToolResult> ListAsync(CancellationToken ct)
    {
        var accounts = await _repo.ListAccountsAsync(ct);
        var allModels = await _repo.ListAllModelsAsync(ct);
        var modelsByAccount = allModels.ToLookup(m => m.AccountId);

        var result = accounts.Select(a => new
        {
            id = a.Id,
            displayName = a.DisplayName,
            providerKind = a.ProviderKind.ToString(),
            baseUrl = a.BaseUrl,
            accessMode = a.AccessMode,
            enabled = a.Enabled,
            models = modelsByAccount[a.Id].Select(m => new
            {
                id = m.Id,
                displayName = m.DisplayName,
                modelId = m.ModelId,
                isGlobalDefault = m.IsGlobalDefault,
                capabilities = m.Capabilities.ToString(),
                contextWindowSize = m.ContextWindowSize,
                maxOutputTokens = m.MaxOutputTokens,
                isReasoning = m.IsReasoning,
                supportsToolCalling = m.SupportsToolCalling,
            }).ToList(),
        }).ToList();

        return ToolResult.Ok(new { accounts = result, count = result.Count });
    }

    private async Task<ToolResult> AddAsync(ConfigUpdateArgs args, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.ModelId))
            return ToolResult.Fail("'modelId' is required for action='add'.");
        if (string.IsNullOrWhiteSpace(args.Provider))
            return ToolResult.Fail("'provider' is required for action='add'.");
        if (string.IsNullOrWhiteSpace(args.ApiKey))
            return ToolResult.Fail("'apiKey' is required for action='add'.");

        if (!Enum.TryParse<ModelProviderKind>(args.Provider, ignoreCase: true, out var providerKind))
            return ToolResult.Fail($"Unknown provider '{args.Provider}'. Valid values: Anthropic, AnthropicCompatible, OpenAI, OpenAICompatible.");

        var capabilities = ParseCapabilities(args.Capabilities);
        var contextWindowSize = ParseTokenCount(args.ContextWindowSize, nameof(args.ContextWindowSize), 128_000);
        var maxOutputTokens = ParseTokenCount(args.MaxOutputTokens, nameof(args.MaxOutputTokens), 8192);

        var displayName = !string.IsNullOrWhiteSpace(args.DisplayName)
            ? args.DisplayName.Trim()
            : $"{args.Provider} / {args.ModelId}";

        var accountId = $"account-{Guid.NewGuid():N}";
        var modelId = $"model-{Guid.NewGuid():N}";

        // Store the API key in the OS keychain.
        var secretRef = new SecretRef("keychain", "accounts", accountId);
        await _secretStore.UpsertAsync(secretRef, args.ApiKey.Trim(), ct);

        var now = DateTimeOffset.UtcNow;
        var account = new ProviderAccount(
            Id: accountId,
            DisplayName: displayName,
            ProviderKind: providerKind,
            BaseUrl: string.IsNullOrWhiteSpace(args.BaseUrl) ? null : args.BaseUrl.Trim().TrimEnd('/'),
            ApiKeySecretRef: secretRef.ToReferenceString(),
            ApiKeyEnvironmentVariable: null,
            AccessMode: "api",
            Enabled: true,
            CreatedAt: now,
            UpdatedAt: now);

        await _repo.AddAccountAsync(account, ct);

        // If no global default exists, set this model as default.
        var existingModels = await _repo.ListAllModelsAsync(ct);
        var hasDefault = existingModels.Any(m => m.IsGlobalDefault);

        var model = new AccountModel(
            Id: modelId,
            AccountId: accountId,
            DisplayName: displayName,
            ModelId: args.ModelId.Trim(),
            Capabilities: capabilities,
            IsDefaultForAccount: true,
            IsGlobalDefault: !hasDefault,
            Enabled: true,
            CreatedAt: now,
            UpdatedAt: now,
            ContextWindowSize: contextWindowSize,
            MaxOutputTokens: maxOutputTokens,
            IsReasoning: args.IsReasoning ?? false,
            SupportsToolCalling: args.SupportsToolCalling ?? true);

        await _repo.AddModelAsync(model, ct);

        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: "runtime.config_update",
            EventType: "provider_account.added",
            Level: "info",
            Message: $"Provider account '{displayName}' (id={accountId}) with model '{args.ModelId}' added by agent.",
            Timestamp: DateTimeOffset.UtcNow));

        return ToolResult.Ok(new
        {
            ok = true,
            accountId,
            modelId,
            displayName,
            provider = providerKind.ToString(),
            model = args.ModelId.Trim(),
        });
    }

    private async Task<ToolResult> SetDefaultAsync(ConfigUpdateArgs args, CancellationToken ct)
    {
        var targetModelId = args.EndpointId?.Trim(); // reuse endpointId parameter for modelId
        if (string.IsNullOrWhiteSpace(targetModelId))
            return ToolResult.Fail("'endpointId' (model ID) is required for action='set_default'.");

        var success = await _repo.SetGlobalDefaultAsync(targetModelId, DateTimeOffset.UtcNow, ct);
        if (!success)
            return ToolResult.Fail($"Model '{targetModelId}' not found.");

        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: "runtime.config_update",
            EventType: "provider_account.default_changed",
            Level: "info",
            Message: $"Default model changed to '{targetModelId}' by agent.",
            Timestamp: DateTimeOffset.UtcNow));

        return ToolResult.Ok(new { ok = true, defaultModelId = targetModelId });
    }

    private async Task<ToolResult> DeleteAsync(ConfigUpdateArgs args, CancellationToken ct)
    {
        var id = args.EndpointId?.Trim(); // reuse endpointId parameter for accountId
        if (string.IsNullOrWhiteSpace(id))
            return ToolResult.Fail("'endpointId' (account ID) is required for action='delete'.");

        var account = await _repo.GetAccountByIdAsync(id, ct);
        if (account is null)
            return ToolResult.Fail($"Provider account '{id}' not found.");

        // Cascade delete handled by repository
        await _repo.DeleteAccountAsync(id, ct);

        // Best-effort: delete the associated keychain secret.
        try
        {
            if (!string.IsNullOrWhiteSpace(account.ApiKeySecretRef) &&
                SecretRef.TryParse(account.ApiKeySecretRef, out var secretRef))
            {
                await _secretStore.DeleteAsync(secretRef, ct);
            }
        }
        catch
        {
            // Secret may not exist; ignore.
        }

        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: "runtime.config_update",
            EventType: "provider_account.deleted",
            Level: "info",
            Message: $"Provider account '{account.DisplayName}' (id={id}) deleted by agent.",
            Timestamp: DateTimeOffset.UtcNow));

        return ToolResult.Ok(new { ok = true, deletedAccountId = id });
    }

    private async Task<ToolResult> AddModelAsync(ConfigUpdateArgs args, CancellationToken ct)
    {
        var accountId = args.EndpointId?.Trim();
        if (string.IsNullOrWhiteSpace(accountId))
            return ToolResult.Fail("'endpointId' (account ID) is required for action='add_model'.");
        if (string.IsNullOrWhiteSpace(args.ModelId))
            return ToolResult.Fail("'modelId' is required for action='add_model'.");

        var account = await _repo.GetAccountByIdAsync(accountId, ct);
        if (account is null)
            return ToolResult.Fail($"Provider account '{accountId}' not found.");

        var capabilities = ParseCapabilities(args.Capabilities);
        var contextWindowSize = ParseTokenCount(args.ContextWindowSize, nameof(args.ContextWindowSize), 128_000);
        var maxOutputTokens = ParseTokenCount(args.MaxOutputTokens, nameof(args.MaxOutputTokens), 8192);

        var displayName = !string.IsNullOrWhiteSpace(args.DisplayName)
            ? args.DisplayName.Trim()
            : $"{account.ProviderKind} / {args.ModelId}";

        var modelId = $"model-{Guid.NewGuid():N}";

        var existingModels = await _repo.ListAllModelsAsync(ct);
        var hasDefault = existingModels.Any(m => m.IsGlobalDefault);

        var model = new AccountModel(
            Id: modelId,
            AccountId: accountId,
            DisplayName: displayName,
            ModelId: args.ModelId.Trim(),
            Capabilities: capabilities,
            IsDefaultForAccount: false,
            IsGlobalDefault: !hasDefault,
            Enabled: true,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            ContextWindowSize: contextWindowSize,
            MaxOutputTokens: maxOutputTokens,
            IsReasoning: args.IsReasoning ?? false,
            SupportsToolCalling: args.SupportsToolCalling ?? true);

        await _repo.AddModelAsync(model, ct);

        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: "runtime.config_update",
            EventType: "provider_account.model_added",
            Level: "info",
            Message: $"Model '{args.ModelId}' added to account '{account.DisplayName}' (id={accountId}) by agent.",
            Timestamp: DateTimeOffset.UtcNow));

        return ToolResult.Ok(new
        {
            ok = true,
            accountId,
            modelId,
            displayName,
            model = args.ModelId.Trim(),
        });
    }


    private async Task<ToolResult> UpdateModelAsync(ConfigUpdateArgs args, CancellationToken ct)
    {
        var modelId = args.EndpointId?.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
            return ToolResult.Fail("'endpointId' (model ID) is required for action='update_model'.");

        var model = await _repo.GetModelByIdAsync(modelId, ct);
        if (model is null)
            return ToolResult.Fail($"Model '{modelId}' not found.");

        var now = DateTimeOffset.UtcNow;
        var displayName = !string.IsNullOrWhiteSpace(args.DisplayName) ? args.DisplayName.Trim() : model.DisplayName;
        var contextWindowSize = args.ContextWindowSize != null
            ? ParseTokenCount(args.ContextWindowSize, nameof(args.ContextWindowSize), model.ContextWindowSize)
            : model.ContextWindowSize;
        var maxOutputTokens = args.MaxOutputTokens != null
            ? ParseTokenCount(args.MaxOutputTokens, nameof(args.MaxOutputTokens), model.MaxOutputTokens)
            : model.MaxOutputTokens;
        var capabilities = !string.IsNullOrWhiteSpace(args.Capabilities)
            ? ParseCapabilities(args.Capabilities)
            : model.Capabilities;
        var isReasoning = args.IsReasoning ?? model.IsReasoning;
        var supportsToolCalling = args.SupportsToolCalling ?? model.SupportsToolCalling;

        var updated = model with
        {
            DisplayName = displayName,
            ContextWindowSize = contextWindowSize,
            MaxOutputTokens = maxOutputTokens,
            Capabilities = capabilities,
            IsReasoning = isReasoning,
            SupportsToolCalling = supportsToolCalling,
            UpdatedAt = now,
        };

        var success = await _repo.UpdateModelAsync(updated, ct);
        if (!success)
            return ToolResult.Fail($"Failed to update model '{modelId}'.");

        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: "runtime.config_update",
            EventType: "provider_account.model_updated",
            Level: "info",
            Message: $"Model '{modelId}' updated by agent.",
            Timestamp: DateTimeOffset.UtcNow));

        return ToolResult.Ok(new
        {
            ok = true,
            modelId,
            displayName = updated.DisplayName,
            contextWindowSize = updated.ContextWindowSize,
            maxOutputTokens = updated.MaxOutputTokens,
            isReasoning = updated.IsReasoning,
            supportsToolCalling = updated.SupportsToolCalling,
        });
    }

    private async Task<ToolResult> UpdateAccountAsync(ConfigUpdateArgs args, CancellationToken ct)
    {
        var accountId = args.EndpointId?.Trim();
        if (string.IsNullOrWhiteSpace(accountId))
            return ToolResult.Fail("'endpointId' (account ID) is required for action='update_account'.");

        var account = await _repo.GetAccountByIdAsync(accountId, ct);
        if (account is null)
            return ToolResult.Fail($"Provider account '{accountId}' not found.");

        var now = DateTimeOffset.UtcNow;
        var displayName = !string.IsNullOrWhiteSpace(args.DisplayName) ? args.DisplayName.Trim() : account.DisplayName;
        var baseUrl = args.BaseUrl != null
            ? (string.IsNullOrWhiteSpace(args.BaseUrl) ? null : args.BaseUrl.Trim().TrimEnd('/'))
            : account.BaseUrl;
        var enabled = args.Enabled ?? account.Enabled;

        var updated = account with
        {
            DisplayName = displayName,
            BaseUrl = baseUrl,
            Enabled = enabled,
            UpdatedAt = now,
        };

        var success = await _repo.UpdateAccountAsync(updated, ct);
        if (!success)
            return ToolResult.Fail($"Failed to update account '{accountId}'.");

        // Rotate API key if a new one was provided.
        if (!string.IsNullOrWhiteSpace(args.ApiKey))
        {
            if (SecretRef.TryParse(account.ApiKeySecretRef, out var secretRef))
            {
                await _secretStore.UpsertAsync(secretRef, args.ApiKey.Trim(), ct);
            }
        }

        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: "runtime.config_update",
            EventType: "provider_account.updated",
            Level: "info",
            Message: $"Provider account '{accountId}' updated by agent.",
            Timestamp: DateTimeOffset.UtcNow));

        return ToolResult.Ok(new
        {
            ok = true,
            accountId,
            displayName = updated.DisplayName,
            baseUrl = updated.BaseUrl,
            enabled = updated.Enabled,
        });
    }

    private static int ParseTokenCount(string? raw, string fieldName, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        var trimmed = raw.Trim();

        var match = Regex.Match(trimmed, @"^(\d+(?:\.\d+)?)\s*(k|m)$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var number = double.Parse(match.Groups[1].Value);
            var suffix = match.Groups[2].Value.ToLowerInvariant();
            var value = suffix == "k" ? (int)(number * 1024) : (int)(number * 1_000_000);
            return value;
        }

        if (int.TryParse(trimmed, out var plain))
            return plain;

        throw new ArgumentException(
            $"Cannot parse '{raw}' for {fieldName}. Use formats like '128k', '1m', '200k', '2m', or a plain integer.");
    }

    private static ModelCapabilitySet ParseCapabilities(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return ModelCapabilitySet.Text;

        ModelCapabilitySet result = 0;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<ModelCapabilitySet>(part, ignoreCase: true, out var flag))
                result |= flag;
            else
                throw new ArgumentException(
                    $"Unknown capability '{part}'. Valid values: {string.Join(", ", Enum.GetNames<ModelCapabilitySet>())}.");
        }

        return result == 0 ? ModelCapabilitySet.Text : result;
    }
}

/// <summary>
/// Arguments for the config_update tool.
/// </summary>
public sealed class ConfigUpdateArgs
{
    [ToolParameter(Description = "Action to perform: 'list', 'add', 'add_model', 'set_default', or 'delete'.")]
    public required string Action { get; init; }

    [ToolParameter(Description = "Human-readable display name. Used for 'add' and 'add_model'.", Required = false)]
    public string? DisplayName { get; init; }

    [ToolParameter(Description = "Provider kind: 'Anthropic', 'AnthropicCompatible', 'OpenAI', 'OpenAICompatible'. Required for 'add'.", Required = false)]
    public string? Provider { get; init; }

    [ToolParameter(Description = "Model identifier (e.g. 'claude-sonnet-4-6', 'gpt-4o'). Required for 'add' and 'add_model'.", Required = false)]
    public string? ModelId { get; init; }

    [ToolParameter(Description = "API key value. Stored securely in the OS keychain. Required for 'add'.", Required = false)]
    public string? ApiKey { get; init; }

    [ToolParameter(Description = "Base URL for compatible providers (e.g. 'https://my-proxy.com/v1'). Omit for official endpoints.", Required = false)]
    public string? BaseUrl { get; init; }

    [ToolParameter(Description = "Account or model ID. For 'add_model' and 'delete' (account ID), 'set_default' (model ID).", Required = false)]
    public string? EndpointId { get; init; }

    [ToolParameter(Description = "Context window size. Accepts '128k', '1m', '2m', or plain integer. Default: 128000.", Required = false)]
    public string? ContextWindowSize { get; init; }

    [ToolParameter(Description = "Max output tokens. Accepts '8k', '16k', or plain integer. Default: 8192.", Required = false)]
    public string? MaxOutputTokens { get; init; }

    [ToolParameter(Description = "Model capabilities, comma-separated: Text, Image, Video, Audio, File. Default: 'Text'.", Required = false)]
    public string? Capabilities { get; init; }

    [ToolParameter(Description = "Set true for reasoning-focused models (e.g. o1, DeepSeek-R1). Default: false.", Required = false)]
    public bool? IsReasoning { get; init; }

    [ToolParameter(Description = "Set false if the model does not support tool/function calling. Default: true.", Required = false)]
    public bool? SupportsToolCalling { get; init; }

    [ToolParameter(Description = "Enable or disable a model/account. Set true to enable, false to disable. Optional for 'update_model' and 'update_account'.", Required = false)]
    public bool? Enabled { get; init; }
}
