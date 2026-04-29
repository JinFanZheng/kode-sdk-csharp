using System.Text;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Models;
using KodaClaw.Contracts.Secrets;
using Microsoft.Extensions.Logging;

namespace KodaClaw.Gateway;

/// <summary>
/// Shared writer used by both the ENV-var bootstrap path (ConfigBootstrapService)
/// and the Setup Wizard path (POST /setup/complete).
///
/// Writes a default provider account + model into the repository if and only if
/// the repository is currently empty. Keys are stored in the OS Keychain via
/// ISecretStore rather than on disk in plaintext.
/// </summary>
internal sealed class ConfigBootstrapWriter
{
    private const string AnthropicDefaultAccountId = "anthropic-default";
    private const string AnthropicDefaultModelId = "anthropic-default-model";
    private const string OpenAIDefaultAccountId = "openai-default";
    private const string OpenAIDefaultModelId = "openai-default-model";

    private readonly IProviderAccountRepository _repo;
    private readonly ISecretStore _secretStore;
    private readonly ILogger<ConfigBootstrapWriter>? _logger;

    public ConfigBootstrapWriter(
        IProviderAccountRepository repo,
        ISecretStore secretStore,
        ILogger<ConfigBootstrapWriter>? logger = null)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _logger = logger;
    }

    // ── ENV-var bootstrap path ─────────────────────────────────────────────────

    /// <summary>
    /// If the provider account repository is empty, writes a default account + model
    /// for the first non-null API key provided and marks the model as the global default.
    /// Anthropic key takes precedence over OpenAI when both are supplied.
    /// No-op if the repository already contains at least one account.
    /// Used by <see cref="ConfigBootstrapService"/> for ENV-var seeding.
    /// </summary>
    public async Task WriteIfAbsentAsync(
        string? anthropicKey,
        string? openaiKey,
        CancellationToken cancellationToken = default)
    {
        var existing = await _repo.ListAccountsAsync(cancellationToken);
        if (existing.Count > 0)
        {
            _logger?.LogDebug(
                "ConfigBootstrapWriter: repository has {Count} account(s), skipping.", existing.Count);
            return;
        }

        (ProviderAccount Account, AccountModel Model)? pair = null;

        if (!string.IsNullOrWhiteSpace(anthropicKey))
        {
            var secretRef = new SecretRef("keychain", "config-bootstrap", "anthropic");
            await _secretStore.UpsertAsync(secretRef, anthropicKey.Trim(), cancellationToken);
            pair = BuildAccountAndModel(
                accountId: AnthropicDefaultAccountId,
                modelId: AnthropicDefaultModelId,
                displayName: "Claude Sonnet (default)",
                provider: ModelProviderKind.Anthropic,
                modelIdValue: "claude-sonnet-4-20250514",
                secretRef: secretRef.ToReferenceString());

            _logger?.LogDebug("ConfigBootstrapWriter: stored Anthropic key in secret store.");
        }
        else if (!string.IsNullOrWhiteSpace(openaiKey))
        {
            var secretRef = new SecretRef("keychain", "config-bootstrap", "openai");
            await _secretStore.UpsertAsync(secretRef, openaiKey.Trim(), cancellationToken);
            pair = BuildAccountAndModel(
                accountId: OpenAIDefaultAccountId,
                modelId: OpenAIDefaultModelId,
                displayName: "GPT-4o (default)",
                provider: ModelProviderKind.OpenAI,
                modelIdValue: "gpt-4o",
                secretRef: secretRef.ToReferenceString());

            _logger?.LogDebug("ConfigBootstrapWriter: stored OpenAI key in secret store.");
        }

        if (pair is null)
        {
            _logger?.LogDebug("ConfigBootstrapWriter: no API key provided, nothing to write.");
            return;
        }

        var (account, model) = pair.Value;

        await _repo.AddAccountAsync(account, cancellationToken);
        await _repo.AddModelAsync(model, cancellationToken);
        await _repo.SetGlobalDefaultAsync(model.Id, DateTimeOffset.UtcNow, cancellationToken);

        _logger?.LogInformation(
            "ConfigBootstrapWriter: wrote account '{DisplayName}' " +
            "(provider={Provider}, modelId={ModelId}) and set model as global default.",
            account.DisplayName, account.ProviderKind, model.ModelId);
    }

    // ── Setup Wizard path ──────────────────────────────────────────────────────

    /// <summary>
    /// If the provider account repository is empty, writes a single account + model
    /// for the specified provider / model combination and marks the model as the
    /// global default.
    /// No-op if the repository already contains at least one account.
    /// Used by the Setup Wizard POST /setup/complete path.
    /// </summary>
    public async Task WriteIfAbsentAsync(
        ModelProviderKind provider,
        string modelId,
        string? apiKey,
        string? baseUrl,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        var existing = await _repo.ListAccountsAsync(cancellationToken);
        if (existing.Count > 0)
        {
            _logger?.LogDebug(
                "ConfigBootstrapWriter: repository has {Count} account(s), skipping.", existing.Count);
            return;
        }

        var accountId = Slugify($"{provider}-{modelId}");
        var accountModelId = Slugify($"{provider}-{modelId}-model");
        var name = !string.IsNullOrWhiteSpace(displayName)
            ? displayName.Trim()
            : $"{provider} / {modelId}";

        string? secretRef = null;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var secret = new SecretRef("keychain", "config-bootstrap", accountId);
            await _secretStore.UpsertAsync(secret, apiKey.Trim(), cancellationToken);
            secretRef = secret.ToReferenceString();
            _logger?.LogDebug("ConfigBootstrapWriter: stored API key for '{Id}' in secret store.", accountId);
        }

        var (account, model) = BuildAccountAndModel(
            accountId: accountId,
            modelId: accountModelId,
            displayName: name,
            provider: provider,
            modelIdValue: modelId,
            secretRef: secretRef,
            baseUrl: string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim());

        await _repo.AddAccountAsync(account, cancellationToken);
        await _repo.AddModelAsync(model, cancellationToken);
        await _repo.SetGlobalDefaultAsync(model.Id, DateTimeOffset.UtcNow, cancellationToken);

        _logger?.LogInformation(
            "ConfigBootstrapWriter: wrote account '{DisplayName}' " +
            "(provider={Provider}, modelId={ModelId}) and set model as global default.",
            name, provider, modelId);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static (ProviderAccount Account, AccountModel Model) BuildAccountAndModel(
        string accountId,
        string modelId,
        string displayName,
        ModelProviderKind provider,
        string modelIdValue,
        string? secretRef,
        string? baseUrl = null)
    {
        var now = DateTimeOffset.UtcNow;
        var account = new ProviderAccount(
            Id: accountId,
            DisplayName: displayName,
            ProviderKind: provider,
            BaseUrl: baseUrl,
            ApiKeySecretRef: secretRef,
            ApiKeyEnvironmentVariable: null,
            AccessMode: "api",
            Enabled: true,
            CreatedAt: now,
            UpdatedAt: now);
        var model = new AccountModel(
            Id: modelId,
            AccountId: accountId,
            DisplayName: displayName,
            ModelId: modelIdValue,
            Capabilities: ModelCapabilitySet.Text,
            IsDefaultForAccount: true,
            IsGlobalDefault: false, // SetGlobalDefaultAsync is called right after AddModelAsync
            Enabled: true,
            CreatedAt: now,
            UpdatedAt: now);
        return (account, model);
    }

    private static string Slugify(string input)
    {
        var sb = new StringBuilder();
        var prevDash = true;
        foreach (var c in input.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(c); prevDash = false; }
            else if (!prevDash) { sb.Append('-'); prevDash = true; }
        }
        var slug = sb.ToString().TrimEnd('-');
        return slug.Length == 0 ? "endpoint" : (slug.Length > 64 ? slug[..64] : slug);
    }
}
