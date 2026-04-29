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
        "Manage KodaClaw model provider configuration. " +
        "Use action='list' to see all configured provider accounts and their models. " +
        "Use action='add' to add a new provider account with a model (provider, modelId, and apiKey required). " +
        "Use action='set_default' to change which model is used by default (modelId required). " +
        "Use action='delete' to remove a provider account (accountId required).\n\n" +
        "Provider values: 'Anthropic', 'AnthropicCompatible', 'OpenAI', 'OpenAICompatible'.\n" +
        "API keys are stored securely in the OS keychain and never appear in plain text on disk.";

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
            "set_default" => await SetDefaultAsync(args, cancellationToken),
            "delete" => await DeleteAsync(args, cancellationToken),
            _ => ToolResult.Fail($"Unknown action '{args.Action}'. Valid values: list, add, set_default, delete."),
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
            Capabilities: ModelCapabilitySet.Text,
            IsDefaultForAccount: true,
            IsGlobalDefault: !hasDefault,
            Enabled: true,
            CreatedAt: now,
            UpdatedAt: now,
            ContextWindowSize: args.ContextWindowSize ?? 128_000);

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
}

/// <summary>
/// Arguments for the config_update tool.
/// </summary>
public sealed class ConfigUpdateArgs
{
    [ToolParameter(Description = "Action to perform: 'list', 'add', 'set_default', or 'delete'.")]
    public required string Action { get; init; }

    [ToolParameter(Description = "Human-readable display name for the provider account. Used for 'add'.", Required = false)]
    public string? DisplayName { get; init; }

    [ToolParameter(Description = "Provider kind: 'Anthropic', 'AnthropicCompatible', 'OpenAI', 'OpenAICompatible'. Required for 'add'.", Required = false)]
    public string? Provider { get; init; }

    [ToolParameter(Description = "Model identifier (e.g. 'claude-sonnet-4-6', 'gpt-4o'). Required for 'add'.", Required = false)]
    public string? ModelId { get; init; }

    [ToolParameter(Description = "API key value. Stored securely in the OS keychain. Required for 'add'.", Required = false)]
    public string? ApiKey { get; init; }

    [ToolParameter(Description = "Base URL for compatible providers (e.g. 'https://my-proxy.com/v1'). Omit for official Anthropic/OpenAI endpoints.", Required = false)]
    public string? BaseUrl { get; init; }

    [ToolParameter(Description = "Account or model ID. Required for 'set_default' (model ID) and 'delete' (account ID).", Required = false)]
    public string? EndpointId { get; init; }

    [ToolParameter(Description = "Context window size in tokens. Defaults to 128000.", Required = false)]
    public int? ContextWindowSize { get; init; }
}
