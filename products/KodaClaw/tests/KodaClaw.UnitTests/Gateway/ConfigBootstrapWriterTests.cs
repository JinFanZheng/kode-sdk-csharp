using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Models;
using KodaClaw.Contracts.Secrets;
using KodaClaw.Gateway;
using KodaClaw.Storage.Json.Repositories;
using Xunit;

namespace KodaClaw.UnitTests.Gateway;

/// <summary>
/// KC-DOCKER-005: Unit tests for ConfigBootstrapWriter — the shared writer used by
/// both the ENV-var bootstrap path and the Setup Wizard POST /setup/complete.
/// </summary>
public sealed class ConfigBootstrapWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly FakeSecretStoreForBootstrap _secretStore = new();

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private ConfigBootstrapWriter CreateWriter()
    {
        var registry = new JsonProviderAccountRepository(_tempDir);
        return new ConfigBootstrapWriter(registry, _secretStore);
    }

    private JsonProviderAccountRepository CreateRegistry() => new(_tempDir);

    private static async Task SeedAccountAsync(
        JsonProviderAccountRepository repo,
        string id,
        ModelProviderKind provider,
        bool isDefault = false)
    {
        var now = DateTimeOffset.UtcNow;
        var account = new ProviderAccount(
            Id: id,
            DisplayName: "test",
            ProviderKind: provider,
            BaseUrl: null,
            ApiKeySecretRef: null,
            ApiKeyEnvironmentVariable: null,
            AccessMode: "api",
            Enabled: true,
            CreatedAt: now,
            UpdatedAt: now);
        var model = new AccountModel(
            Id: $"{id}-model",
            AccountId: id,
            DisplayName: "test",
            ModelId: "test-model",
            Capabilities: ModelCapabilitySet.Text,
            IsDefaultForAccount: true,
            IsGlobalDefault: isDefault,
            Enabled: true,
            CreatedAt: now,
            UpdatedAt: now);
        await repo.AddAccountAsync(account);
        await repo.AddModelAsync(model);
    }

    // ── ENV-var path: no-op guards ───────────────────────────────────────────

    [Fact]
    public async Task WriteIfAbsent_skips_when_registry_already_has_endpoints()
    {
        var registry = CreateRegistry();
        await SeedAccountAsync(registry, "already-exists", ModelProviderKind.Anthropic, isDefault: true);

        var writer = new ConfigBootstrapWriter(registry, _secretStore);
        await writer.WriteIfAbsentAsync("sk-ant-NEW", null);

        var all = await registry.ListAccountsAsync();
        all.Should().HaveCount(1, because: "WriteIfAbsent must not add when registry is nonempty");
        all[0].Id.Should().Be("already-exists");
    }

    [Fact]
    public async Task WriteIfAbsent_is_noop_when_both_keys_are_null()
    {
        var writer = CreateWriter();
        await writer.WriteIfAbsentAsync(null, null);

        var all = await CreateRegistry().ListAccountsAsync();
        all.Should().BeEmpty();
        _secretStore.StoredSecrets.Should().BeEmpty();
    }

    [Fact]
    public async Task WriteIfAbsent_is_noop_when_both_keys_are_whitespace()
    {
        var writer = CreateWriter();
        await writer.WriteIfAbsentAsync("   ", "  ");

        var all = await CreateRegistry().ListAccountsAsync();
        all.Should().BeEmpty();
    }

    // ── ENV-var path: Anthropic key ──────────────────────────────────────────

    [Fact]
    public async Task WriteIfAbsent_creates_anthropic_account_and_marks_default()
    {
        var writer = CreateWriter();
        await writer.WriteIfAbsentAsync("sk-ant-test", null);

        var registry = CreateRegistry();
        var accounts = await registry.ListAccountsAsync();
        accounts.Should().HaveCount(1);

        var account = accounts[0];
        account.ProviderKind.Should().Be(ModelProviderKind.Anthropic);
        account.Enabled.Should().BeTrue();
        account.ApiKeySecretRef.Should().NotBeNullOrWhiteSpace();
        account.ApiKeyEnvironmentVariable.Should().BeNull();

        var models = await registry.ListAllModelsAsync();
        models.Should().HaveCount(1);
        models[0].IsGlobalDefault.Should().BeTrue();
    }

    [Fact]
    public async Task WriteIfAbsent_stores_anthropic_key_in_secret_store()
    {
        var writer = CreateWriter();
        await writer.WriteIfAbsentAsync("sk-ant-test", null);

        _secretStore.StoredSecrets.Should().ContainValue("sk-ant-test");
    }

    // ── ENV-var path: OpenAI key ─────────────────────────────────────────────

    [Fact]
    public async Task WriteIfAbsent_creates_openai_account_when_only_openai_key_provided()
    {
        var writer = CreateWriter();
        await writer.WriteIfAbsentAsync(null, "sk-openai-test");

        var registry = CreateRegistry();
        var accounts = await registry.ListAccountsAsync();
        accounts.Should().HaveCount(1);
        accounts[0].ProviderKind.Should().Be(ModelProviderKind.OpenAI);

        var models = await registry.ListAllModelsAsync();
        models.Should().HaveCount(1);
        models[0].IsGlobalDefault.Should().BeTrue();
    }

    [Fact]
    public async Task WriteIfAbsent_stores_openai_key_in_secret_store()
    {
        var writer = CreateWriter();
        await writer.WriteIfAbsentAsync(null, "sk-openai-test");

        _secretStore.StoredSecrets.Should().ContainValue("sk-openai-test");
    }

    // ── ENV-var path: precedence ─────────────────────────────────────────────

    [Fact]
    public async Task WriteIfAbsent_prefers_anthropic_when_both_keys_provided()
    {
        var writer = CreateWriter();
        await writer.WriteIfAbsentAsync("sk-ant-test", "sk-openai-test");

        var accounts = await CreateRegistry().ListAccountsAsync();
        accounts.Should().HaveCount(1);
        accounts[0].ProviderKind.Should().Be(ModelProviderKind.Anthropic,
            because: "Anthropic takes precedence over OpenAI when both keys are provided");

        _secretStore.StoredSecrets.Values
            .Should().ContainSingle().Which.Should().Be("sk-ant-test");
    }

    // ── ENV-var path: key trimming ───────────────────────────────────────────

    [Fact]
    public async Task WriteIfAbsent_trims_api_key_before_storing()
    {
        var writer = CreateWriter();
        await writer.WriteIfAbsentAsync("  sk-ant-padded  ", null);

        _secretStore.StoredSecrets.Values.Should().ContainSingle().Which.Should().Be("sk-ant-padded");
    }

    // ── Setup Wizard path (provider overload) ────────────────────────────────

    [Fact]
    public async Task WriteIfAbsent_provider_creates_account_and_marks_default()
    {
        var writer = CreateWriter();
        await writer.WriteIfAbsentAsync(
            provider:    ModelProviderKind.OpenAICompatible,
            modelId:     "glm-4-flash",
            apiKey:      "glm-key-test",
            baseUrl:     "https://open.bigmodel.cn/api/paas/v4",
            displayName: "GLM-4 Flash");

        var registry = CreateRegistry();
        var accounts = await registry.ListAccountsAsync();
        accounts.Should().HaveCount(1);

        var account = accounts[0];
        account.ProviderKind.Should().Be(ModelProviderKind.OpenAICompatible);
        account.BaseUrl.Should().Be("https://open.bigmodel.cn/api/paas/v4");
        account.DisplayName.Should().Be("GLM-4 Flash");

        var models = await registry.ListAllModelsAsync();
        models.Should().HaveCount(1);
        models[0].ModelId.Should().Be("glm-4-flash");
        models[0].IsGlobalDefault.Should().BeTrue();
        _secretStore.StoredSecrets.Should().ContainValue("glm-key-test");
    }

    [Fact]
    public async Task WriteIfAbsent_provider_skips_when_registry_nonempty()
    {
        var registry = CreateRegistry();
        await SeedAccountAsync(registry, "existing", ModelProviderKind.Anthropic, isDefault: true);

        var writer = new ConfigBootstrapWriter(registry, _secretStore);
        await writer.WriteIfAbsentAsync(
            provider: ModelProviderKind.OpenAI,
            modelId:  "gpt-4o",
            apiKey:   "sk-openai-test",
            baseUrl:  null,
            displayName: null);

        var accounts = await registry.ListAccountsAsync();
        accounts.Should().HaveCount(1, because: "WriteIfAbsent must not overwrite existing registry");
        accounts[0].Id.Should().Be("existing");
    }

    [Fact]
    public async Task WriteIfAbsent_provider_accepts_null_api_key_for_local_providers()
    {
        var writer = CreateWriter();
        await writer.WriteIfAbsentAsync(
            provider:    ModelProviderKind.OpenAICompatible,
            modelId:     "llama3.2",
            apiKey:      null,           // Ollama: no key needed
            baseUrl:     "http://localhost:11434/v1",
            displayName: null);

        var registry = CreateRegistry();
        var accounts = await registry.ListAccountsAsync();
        accounts.Should().HaveCount(1);
        accounts[0].ApiKeySecretRef.Should().BeNull(because: "no key was provided");

        var models = await registry.ListAllModelsAsync();
        models.Should().HaveCount(1);
        models[0].ModelId.Should().Be("llama3.2");
        _secretStore.StoredSecrets.Should().BeEmpty();
    }

    [Fact]
    public async Task WriteIfAbsent_provider_trims_api_key_and_baseurl()
    {
        var writer = CreateWriter();
        await writer.WriteIfAbsentAsync(
            provider:    ModelProviderKind.OpenAICompatible,
            modelId:     "deepseek-chat",
            apiKey:      "  sk-ds-padded  ",
            baseUrl:     "  https://api.deepseek.com/v1  ",
            displayName: null);

        _secretStore.StoredSecrets.Values.Should().ContainSingle().Which.Should().Be("sk-ds-padded");
        var accounts = await CreateRegistry().ListAccountsAsync();
        accounts[0].BaseUrl.Should().Be("https://api.deepseek.com/v1");
    }

    [Fact]
    public async Task WriteIfAbsent_provider_uses_provider_model_as_displayname_when_none_given()
    {
        var writer = CreateWriter();
        await writer.WriteIfAbsentAsync(
            provider:    ModelProviderKind.OpenAI,
            modelId:     "gpt-4o",
            apiKey:      "sk-openai-test",
            baseUrl:     null,
            displayName: null);

        var accounts = await CreateRegistry().ListAccountsAsync();
        accounts[0].DisplayName.Should().Contain("OpenAI").And.Contain("gpt-4o");
    }
}

/// <summary>In-memory ISecretStore for tests that captures written secrets.</summary>
internal sealed class FakeSecretStoreForBootstrap : ISecretStore
{
    public Dictionary<string, string> StoredSecrets { get; } = new();

    public Task<string?> GetAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
    {
        StoredSecrets.TryGetValue(secretRef.ToReferenceString(), out var v);
        return Task.FromResult<string?>(v);
    }

    public Task UpsertAsync(SecretRef secretRef, string secretValue, CancellationToken cancellationToken = default)
    {
        StoredSecrets[secretRef.ToReferenceString()] = secretValue;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
    {
        StoredSecrets.Remove(secretRef.ToReferenceString());
        return Task.CompletedTask;
    }

    public Task<SecretDescriptor> DescribeAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
        => Task.FromResult(new SecretDescriptor(secretRef, Exists: StoredSecrets.ContainsKey(secretRef.ToReferenceString()), IsReadOnly: false));
}
