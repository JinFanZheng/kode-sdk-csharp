using KodaClaw.Contracts;
using KodaClaw.Contracts.Models;
using KodaClaw.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KodaClaw.Gateway;

/// <summary>
/// Runs once at startup: seeds the provider account repository from env-var
/// configuration when the repository is empty. This ensures a smooth upgrade
/// path for users who previously configured the agent via environment variables.
///
/// After seeding, the Repository becomes the single source of truth and env vars
/// are no longer consulted for model routing (see AccountAwareModelProvider).
/// </summary>
internal sealed class ModelRegistrySeedService : IHostedService
{
    private readonly IProviderAccountRepository _repo;
    private readonly IRuntimeConfigurationResolver _runtimeConfig;
    private readonly ILogger<ModelRegistrySeedService>? _logger;

    public ModelRegistrySeedService(
        IProviderAccountRepository repo,
        IRuntimeConfigurationResolver runtimeConfig,
        ILogger<ModelRegistrySeedService>? logger = null)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _runtimeConfig = runtimeConfig ?? throw new ArgumentNullException(nameof(runtimeConfig));
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var existing = await _repo.ListAccountsAsync(cancellationToken);
        if (existing.Count > 0)
        {
            _logger?.LogDebug("ModelRegistrySeedService: repository has {Count} accounts, skipping seed.", existing.Count);
            return;
        }

        var snapshot = _runtimeConfig.Resolve();
        var result = TryBuildAccountAndModel(snapshot);
        if (result is null)
        {
            _logger?.LogDebug("ModelRegistrySeedService: no env-var config found, nothing to seed.");
            return;
        }

        var (account, model) = result.Value;
        await _repo.AddAccountAsync(account, cancellationToken);
        await _repo.AddModelAsync(model, cancellationToken);
        await _repo.SetGlobalDefaultAsync(model.Id, DateTimeOffset.UtcNow, cancellationToken);

        _logger?.LogInformation(
            "ModelRegistrySeedService: seeded account '{DisplayName}' (provider={Provider}, modelId={ModelId}) from env-var config.",
            account.DisplayName, account.ProviderKind, model.ModelId);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static (ProviderAccount Account, AccountModel Model)? TryBuildAccountAndModel(
        RuntimeConfigurationSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.DefaultModel))
            return null;

        ModelProviderKind provider;
        string? apiKeyEnvVar;

        var hasAnthropic = !string.IsNullOrWhiteSpace(snapshot.AnthropicApiKey);
        var hasOpenAi = !string.IsNullOrWhiteSpace(snapshot.OpenAIApiKey);
        var model = snapshot.DefaultModel;

        if (hasAnthropic && !hasOpenAi)
        {
            provider = ModelProviderKind.Anthropic;
            apiKeyEnvVar = "ANTHROPIC_API_KEY";
        }
        else if (hasOpenAi && !hasAnthropic)
        {
            provider = ModelProviderKind.OpenAI;
            apiKeyEnvVar = "OPENAI_API_KEY";
        }
        else if (hasAnthropic && hasOpenAi)
        {
            if (model.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
            {
                provider = ModelProviderKind.Anthropic;
                apiKeyEnvVar = "ANTHROPIC_API_KEY";
            }
            else
            {
                provider = ModelProviderKind.OpenAI;
                apiKeyEnvVar = "OPENAI_API_KEY";
            }
        }
        else
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var accountId = $"account-seed-{Guid.NewGuid():N}";
        var modelId = $"model-seed-{Guid.NewGuid():N}";
        var displayName = $"{model} (auto-seeded)";

        var account = new ProviderAccount(
            Id: accountId,
            DisplayName: displayName,
            ProviderKind: provider,
            BaseUrl: provider == ModelProviderKind.Anthropic
                ? snapshot.AnthropicBaseUrl
                : snapshot.OpenAIBaseUrl,
            ApiKeySecretRef: null,
            ApiKeyEnvironmentVariable: apiKeyEnvVar,
            AccessMode: "api",
            Enabled: true,
            CreatedAt: now,
            UpdatedAt: now);

        var accountModel = new AccountModel(
            Id: modelId,
            AccountId: accountId,
            DisplayName: displayName,
            ModelId: model,
            Capabilities: ModelCapabilitySet.Text,
            IsDefaultForAccount: true,
            IsGlobalDefault: false, // SetGlobalDefaultAsync called separately
            Enabled: true,
            CreatedAt: now,
            UpdatedAt: now,
            ContextWindowSize: 128_000);

        return (account, accountModel);
    }
}
