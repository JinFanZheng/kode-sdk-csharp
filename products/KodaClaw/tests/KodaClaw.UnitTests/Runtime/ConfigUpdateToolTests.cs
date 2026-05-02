using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts.Models;
using KodaClaw.Contracts.Secrets;
using KodaClaw.Runtime.Tools;
using Kode.Agent.Sdk.Core.Abstractions;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.Runtime;

public sealed class ConfigUpdateToolTests : IDisposable
{
    private readonly Mock<IProviderAccountRepository> _repo;
    private readonly Mock<ISecretStore> _secretStore;
    private readonly ConfigUpdateTool _tool;

    public ConfigUpdateToolTests()
    {
        _repo = new Mock<IProviderAccountRepository>(MockBehavior.Strict);
        _secretStore = new Mock<ISecretStore>(MockBehavior.Strict);
        _tool = new ConfigUpdateTool(_repo.Object, _secretStore.Object);
    }

    // ── Tool metadata ────────────────────────────────────────────────────────

    [Fact]
    public void Tool_name_is_config_update()
        => _tool.Name.Should().Be("config_update");

    [Fact]
    public void Tool_does_not_require_approval()
        => _tool.Attributes.RequiresApproval.Should().BeFalse();

    [Fact]
    public void Tool_is_not_readonly()
        => _tool.Attributes.ReadOnly.Should().BeFalse();

    // ── add action: happy path ───────────────────────────────────────────────

    [Fact]
    public async Task Add_creates_account_and_model()
    {
        _secretStore.Setup(s => s.UpsertAsync(It.IsAny<SecretRef>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.AddAccountAsync(It.IsAny<ProviderAccount>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());
        _repo.Setup(r => r.AddModelAsync(It.IsAny<AccountModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "Anthropic",
            ModelId = "claude-sonnet-4-6",
            ApiKey = "sk-test-key",
        });

        result.Success.Should().BeTrue();

        _repo.Verify(r => r.AddAccountAsync(It.Is<ProviderAccount>(a =>
            a.ProviderKind == ModelProviderKind.Anthropic), It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(r => r.AddModelAsync(It.Is<AccountModel>(m =>
            m.ModelId == "claude-sonnet-4-6"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Add_passes_new_parameters_correctly()
    {
        _secretStore.Setup(s => s.UpsertAsync(It.IsAny<SecretRef>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.AddAccountAsync(It.IsAny<ProviderAccount>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());
        _repo.Setup(r => r.AddModelAsync(It.IsAny<AccountModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "OpenAI",
            ModelId = "gpt-4o",
            ApiKey = "sk-key",
            ContextWindowSize = "128k",
            MaxOutputTokens = "8k",
            Capabilities = "Text,Image",
            IsReasoning = true,
            SupportsToolCalling = false,
        });

        result.Success.Should().BeTrue();

        _repo.Verify(r => r.AddModelAsync(It.Is<AccountModel>(m =>
            m.ContextWindowSize == 128 * 1024 &&
            m.MaxOutputTokens == 8 * 1024 &&
            m.Capabilities.HasFlag(ModelCapabilitySet.Text) &&
            m.Capabilities.HasFlag(ModelCapabilitySet.Image) &&
            m.IsReasoning == true &&
            m.SupportsToolCalling == false), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Add_uses_defaults_when_optional_params_null()
    {
        _secretStore.Setup(s => s.UpsertAsync(It.IsAny<SecretRef>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.AddAccountAsync(It.IsAny<ProviderAccount>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());
        _repo.Setup(r => r.AddModelAsync(It.IsAny<AccountModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "Anthropic",
            ModelId = "claude-sonnet-4-6",
            ApiKey = "sk-test",
        });

        _repo.Verify(r => r.AddModelAsync(It.Is<AccountModel>(m =>
            m.ContextWindowSize == 128_000 &&
            m.MaxOutputTokens == 8192 &&
            m.Capabilities == ModelCapabilitySet.Text &&
            m.IsReasoning == false &&
            m.SupportsToolCalling == true), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── add action: validation errors ────────────────────────────────────────

    [Fact]
    public async Task Add_missing_modelId_returns_error()
    {
        var result = await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "Anthropic",
            ApiKey = "sk-test",
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("modelId");
    }

    [Fact]
    public async Task Add_missing_provider_returns_error()
    {
        var result = await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            ModelId = "claude-sonnet-4-6",
            ApiKey = "sk-test",
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("provider");
    }

    [Fact]
    public async Task Add_missing_apiKey_returns_error()
    {
        var result = await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "Anthropic",
            ModelId = "claude-sonnet-4-6",
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("apiKey");
    }

    [Fact]
    public async Task Add_unknown_provider_returns_error()
    {
        var result = await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "UnknownProvider",
            ModelId = "test",
            ApiKey = "sk-test",
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("UnknownProvider");
    }

    // ── add_model action ─────────────────────────────────────────────────────

    [Fact]
    public async Task AddModel_appends_model_to_existing_account()
    {
        var account = new ProviderAccount(
            "acct-1", "Test", ModelProviderKind.Anthropic, null, null, null, "api", true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        _repo.Setup(r => r.GetAccountByIdAsync("acct-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());
        _repo.Setup(r => r.AddModelAsync(It.IsAny<AccountModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add_model",
            EndpointId = "acct-1",
            ModelId = "claude-opus-4-7",
            Capabilities = "Text,Image",
            IsReasoning = true,
            SupportsToolCalling = false,
        });

        result.Success.Should().BeTrue();

        _repo.Verify(r => r.AddModelAsync(It.Is<AccountModel>(m =>
            m.AccountId == "acct-1" &&
            m.ModelId == "claude-opus-4-7" &&
            m.Capabilities.HasFlag(ModelCapabilitySet.Image) &&
            m.IsReasoning == true &&
            m.SupportsToolCalling == false), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddModel_missing_endpointId_returns_error()
    {
        var result = await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add_model",
            ModelId = "claude-opus-4-7",
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("endpointId");
    }

    [Fact]
    public async Task AddModel_missing_modelId_returns_error()
    {
        var result = await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add_model",
            EndpointId = "acct-1",
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("modelId");
    }

    [Fact]
    public async Task AddModel_account_not_found_returns_error()
    {
        _repo.Setup(r => r.GetAccountByIdAsync("acct-missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderAccount?)null);

        var result = await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add_model",
            EndpointId = "acct-missing",
            ModelId = "test-model",
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("acct-missing");
    }

    // ── list action ──────────────────────────────────────────────────────────

    [Fact]
    public async Task List_returns_accounts_with_model_fields()
    {
        var account = new ProviderAccount(
            "acct-1", "Test Acct", ModelProviderKind.OpenAI, "https://api.openai.com", null, null, "api", true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var model = new AccountModel(
            "model-1", "acct-1", "GPT-4o", "gpt-4o", ModelCapabilitySet.Text | ModelCapabilitySet.Image,
            true, true, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            ContextWindowSize: 128_000, MaxOutputTokens: 16384,
            IsReasoning: false, SupportsToolCalling: true);

        _repo.Setup(r => r.ListAccountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { account });
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { model });

        var result = await ExecuteAsync(new ConfigUpdateArgs { Action = "list" });

        result.Success.Should().BeTrue();
        var json = JsonSerializer.Serialize(result.Value);
        json.Should().Contain("maxOutputTokens");
        json.Should().Contain("isReasoning");
        json.Should().Contain("supportsToolCalling");
    }

    [Fact]
    public async Task List_empty_returns_zero_count()
    {
        _repo.Setup(r => r.ListAccountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderAccount>());
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());

        var result = await ExecuteAsync(new ConfigUpdateArgs { Action = "list" });

        result.Success.Should().BeTrue();
        var json = JsonSerializer.Serialize(result.Value);
        json.Should().Contain("\"count\":0");
    }

    // ── token format parsing (via add action) ────────────────────────────────

    [Fact]
    public async Task Add_parses_128k_context_window()
    {
        _secretStore.Setup(s => s.UpsertAsync(It.IsAny<SecretRef>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.AddAccountAsync(It.IsAny<ProviderAccount>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());
        _repo.Setup(r => r.AddModelAsync(It.IsAny<AccountModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "Anthropic",
            ModelId = "claude-sonnet-4-6",
            ApiKey = "sk-test",
            ContextWindowSize = "128k",
        });

        _repo.Verify(r => r.AddModelAsync(It.Is<AccountModel>(m =>
            m.ContextWindowSize == 131_072), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Add_parses_1m_context_window()
    {
        _secretStore.Setup(s => s.UpsertAsync(It.IsAny<SecretRef>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.AddAccountAsync(It.IsAny<ProviderAccount>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());
        _repo.Setup(r => r.AddModelAsync(It.IsAny<AccountModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "Anthropic",
            ModelId = "claude-sonnet-4-6",
            ApiKey = "sk-test",
            ContextWindowSize = "1m",
        });

        _repo.Verify(r => r.AddModelAsync(It.Is<AccountModel>(m =>
            m.ContextWindowSize == 1_000_000), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Add_parses_200k_context_window()
    {
        _secretStore.Setup(s => s.UpsertAsync(It.IsAny<SecretRef>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.AddAccountAsync(It.IsAny<ProviderAccount>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());
        _repo.Setup(r => r.AddModelAsync(It.IsAny<AccountModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "Anthropic",
            ModelId = "claude-sonnet-4-6",
            ApiKey = "sk-test",
            ContextWindowSize = "200k",
        });

        _repo.Verify(r => r.AddModelAsync(It.Is<AccountModel>(m =>
            m.ContextWindowSize == 200 * 1024), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Add_parses_plain_integer_context_window()
    {
        _secretStore.Setup(s => s.UpsertAsync(It.IsAny<SecretRef>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.AddAccountAsync(It.IsAny<ProviderAccount>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());
        _repo.Setup(r => r.AddModelAsync(It.IsAny<AccountModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "Anthropic",
            ModelId = "claude-sonnet-4-6",
            ApiKey = "sk-test",
            ContextWindowSize = "262144",
        });

        _repo.Verify(r => r.AddModelAsync(It.Is<AccountModel>(m =>
            m.ContextWindowSize == 262_144), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Add_invalid_token_format_returns_error()
    {
        var result = await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "Anthropic",
            ModelId = "claude-sonnet-4-6",
            ApiKey = "sk-test",
            ContextWindowSize = "abc",
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Cannot parse");
    }

    // ── unknown action ───────────────────────────────────────────────────────

    [Fact]
    public async Task Unknown_action_returns_error()
    {
        var result = await ExecuteAsync(new ConfigUpdateArgs { Action = "foobar" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("foobar");
    }

    // ── ParseTokenCount 边界 ─────────────────────────────────────────────

    [Fact]
    public async Task Add_null_contextWindowSize_returns_default_128000()
    {
        _secretStore.Setup(s => s.UpsertAsync(It.IsAny<SecretRef>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.AddAccountAsync(It.IsAny<ProviderAccount>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());
        _repo.Setup(r => r.AddModelAsync(It.IsAny<AccountModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "Anthropic",
            ModelId = "test-model",
            ApiKey = "sk-test",
            ContextWindowSize = null,
        });

        _repo.Verify(r => r.AddModelAsync(It.Is<AccountModel>(m =>
            m.ContextWindowSize == 128_000), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Add_empty_contextWindowSize_returns_default_128000()
    {
        _secretStore.Setup(s => s.UpsertAsync(It.IsAny<SecretRef>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.AddAccountAsync(It.IsAny<ProviderAccount>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());
        _repo.Setup(r => r.AddModelAsync(It.IsAny<AccountModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "Anthropic",
            ModelId = "test-model",
            ApiKey = "sk-test",
            ContextWindowSize = "",
        });

        _repo.Verify(r => r.AddModelAsync(It.Is<AccountModel>(m =>
            m.ContextWindowSize == 128_000), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Add_parses_2m_context_window()
    {
        _secretStore.Setup(s => s.UpsertAsync(It.IsAny<SecretRef>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.AddAccountAsync(It.IsAny<ProviderAccount>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());
        _repo.Setup(r => r.AddModelAsync(It.IsAny<AccountModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "Anthropic",
            ModelId = "test-model",
            ApiKey = "sk-test",
            ContextWindowSize = "2m",
        });

        _repo.Verify(r => r.AddModelAsync(It.Is<AccountModel>(m =>
            m.ContextWindowSize == 2_000_000), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Add_parses_131072_context_window()
    {
        _secretStore.Setup(s => s.UpsertAsync(It.IsAny<SecretRef>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.AddAccountAsync(It.IsAny<ProviderAccount>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());
        _repo.Setup(r => r.AddModelAsync(It.IsAny<AccountModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "Anthropic",
            ModelId = "test-model",
            ApiKey = "sk-test",
            ContextWindowSize = "131072",
        });

        _repo.Verify(r => r.AddModelAsync(It.Is<AccountModel>(m =>
            m.ContextWindowSize == 131_072), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Add_parses_zero_context_window()
    {
        _secretStore.Setup(s => s.UpsertAsync(It.IsAny<SecretRef>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.AddAccountAsync(It.IsAny<ProviderAccount>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());
        _repo.Setup(r => r.AddModelAsync(It.IsAny<AccountModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "Anthropic",
            ModelId = "test-model",
            ApiKey = "sk-test",
            ContextWindowSize = "0",
        });

        _repo.Verify(r => r.AddModelAsync(It.Is<AccountModel>(m =>
            m.ContextWindowSize == 0), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── ParseCapabilities 边界 ────────────────────────────────────────────

    [Fact]
    public async Task Add_null_capabilities_defaults_to_Text()
    {
        _secretStore.Setup(s => s.UpsertAsync(It.IsAny<SecretRef>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.AddAccountAsync(It.IsAny<ProviderAccount>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());
        _repo.Setup(r => r.AddModelAsync(It.IsAny<AccountModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "Anthropic",
            ModelId = "test-model",
            ApiKey = "sk-test",
            Capabilities = null,
        });

        _repo.Verify(r => r.AddModelAsync(It.Is<AccountModel>(m =>
            m.Capabilities == ModelCapabilitySet.Text), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Add_empty_capabilities_defaults_to_Text()
    {
        _secretStore.Setup(s => s.UpsertAsync(It.IsAny<SecretRef>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.AddAccountAsync(It.IsAny<ProviderAccount>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());
        _repo.Setup(r => r.AddModelAsync(It.IsAny<AccountModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "Anthropic",
            ModelId = "test-model",
            ApiKey = "sk-test",
            Capabilities = "",
        });

        _repo.Verify(r => r.AddModelAsync(It.Is<AccountModel>(m =>
            m.Capabilities == ModelCapabilitySet.Text), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Add_multiple_capabilities_Text_Image_Video_parsed()
    {
        _secretStore.Setup(s => s.UpsertAsync(It.IsAny<SecretRef>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.AddAccountAsync(It.IsAny<ProviderAccount>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());
        _repo.Setup(r => r.AddModelAsync(It.IsAny<AccountModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "Anthropic",
            ModelId = "test-model",
            ApiKey = "sk-test",
            Capabilities = "Text,Image,Video",
        });

        _repo.Verify(r => r.AddModelAsync(It.Is<AccountModel>(m =>
            m.Capabilities.HasFlag(ModelCapabilitySet.Text) &&
            m.Capabilities.HasFlag(ModelCapabilitySet.Image) &&
            m.Capabilities.HasFlag(ModelCapabilitySet.Video)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Add_unknown_capability_returns_fail()
    {
        var result = await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "Anthropic",
            ModelId = "test-model",
            ApiKey = "sk-test",
            Capabilities = "Foo",
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Foo");
    }

    // ── add action: additional token/capability validation ──────────────

    [Fact]
    public async Task Add_invalid_contextWindowSize_symbols_returns_error()
    {
        var result = await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "Anthropic",
            ModelId = "test-model",
            ApiKey = "sk-test",
            ContextWindowSize = "!!!",
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Cannot parse");
    }

    [Fact]
    public async Task Add_invalid_maxOutputTokens_format_returns_error()
    {
        var result = await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add",
            Provider = "Anthropic",
            ModelId = "test-model",
            ApiKey = "sk-test",
            MaxOutputTokens = "xyz",
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Cannot parse");
    }

    // ── add_model action: parameter passing ───────────────────────────────

    [Fact]
    public async Task AddModel_parses_2m_context_window()
    {
        var account = new ProviderAccount(
            "acct-1", "Test", ModelProviderKind.Anthropic, null, null, null, "api", true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        _repo.Setup(r => r.GetAccountByIdAsync("acct-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());
        _repo.Setup(r => r.AddModelAsync(It.IsAny<AccountModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add_model",
            EndpointId = "acct-1",
            ModelId = "test-model",
            ContextWindowSize = "2m",
        });

        _repo.Verify(r => r.AddModelAsync(It.Is<AccountModel>(m =>
            m.ContextWindowSize == 2_000_000), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddModel_parses_32k_maxOutputTokens()
    {
        var account = new ProviderAccount(
            "acct-1", "Test", ModelProviderKind.Anthropic, null, null, null, "api", true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        _repo.Setup(r => r.GetAccountByIdAsync("acct-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());
        _repo.Setup(r => r.AddModelAsync(It.IsAny<AccountModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add_model",
            EndpointId = "acct-1",
            ModelId = "test-model",
            MaxOutputTokens = "32k",
        });

        _repo.Verify(r => r.AddModelAsync(It.Is<AccountModel>(m =>
            m.MaxOutputTokens == 32 * 1024), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddModel_parses_Text_Image_capabilities()
    {
        var account = new ProviderAccount(
            "acct-1", "Test", ModelProviderKind.Anthropic, null, null, null, "api", true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        _repo.Setup(r => r.GetAccountByIdAsync("acct-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());
        _repo.Setup(r => r.AddModelAsync(It.IsAny<AccountModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add_model",
            EndpointId = "acct-1",
            ModelId = "test-model",
            Capabilities = "Text,Image",
        });

        _repo.Verify(r => r.AddModelAsync(It.Is<AccountModel>(m =>
            m.Capabilities.HasFlag(ModelCapabilitySet.Text) &&
            m.Capabilities.HasFlag(ModelCapabilitySet.Image)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddModel_isReasoning_true_supportsToolCalling_false()
    {
        var account = new ProviderAccount(
            "acct-1", "Test", ModelProviderKind.OpenAI, null, null, null, "api", true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        _repo.Setup(r => r.GetAccountByIdAsync("acct-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());
        _repo.Setup(r => r.AddModelAsync(It.IsAny<AccountModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add_model",
            EndpointId = "acct-1",
            ModelId = "o1-preview",
            IsReasoning = true,
            SupportsToolCalling = false,
        });

        _repo.Verify(r => r.AddModelAsync(It.Is<AccountModel>(m =>
            m.IsReasoning == true &&
            m.SupportsToolCalling == false), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddModel_first_model_becomes_global_default()
    {
        var account = new ProviderAccount(
            "acct-1", "Test", ModelProviderKind.Anthropic, null, null, null, "api", true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        _repo.Setup(r => r.GetAccountByIdAsync("acct-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccountModel>());
        _repo.Setup(r => r.AddModelAsync(It.IsAny<AccountModel>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new ConfigUpdateArgs
        {
            Action = "add_model",
            EndpointId = "acct-1",
            ModelId = "first-model",
        });

        _repo.Verify(r => r.AddModelAsync(It.Is<AccountModel>(m =>
            m.IsGlobalDefault == true), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── list action: multi-account multi-model ────────────────────────────

    [Fact]
    public async Task List_multi_account_multi_model_output_structure()
    {
        var acct1 = new ProviderAccount(
            "acct-1", "Anthropic Acct", ModelProviderKind.Anthropic, null, null, null, "api", true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var acct2 = new ProviderAccount(
            "acct-2", "OpenAI Acct", ModelProviderKind.OpenAI, "https://api.openai.com", null, null, "api", true,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var model1 = new AccountModel(
            "m1", "acct-1", "Claude Sonnet", "claude-sonnet-4-6", ModelCapabilitySet.Text,
            true, true, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            ContextWindowSize: 200_000, MaxOutputTokens: 8192,
            IsReasoning: false, SupportsToolCalling: true);
        var model2 = new AccountModel(
            "m2", "acct-2", "GPT-4o", "gpt-4o", ModelCapabilitySet.Text | ModelCapabilitySet.Image,
            false, false, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            ContextWindowSize: 128_000, MaxOutputTokens: 16384,
            IsReasoning: false, SupportsToolCalling: true);
        var model3 = new AccountModel(
            "m3", "acct-2", "o1-preview", "o1-preview", ModelCapabilitySet.Text,
            false, false, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            ContextWindowSize: 128_000, MaxOutputTokens: 32768,
            IsReasoning: true, SupportsToolCalling: false);

        _repo.Setup(r => r.ListAccountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { acct1, acct2 });
        _repo.Setup(r => r.ListAllModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { model1, model2, model3 });

        var result = await ExecuteAsync(new ConfigUpdateArgs { Action = "list" });

        result.Success.Should().BeTrue();
        var json = JsonSerializer.Serialize(result.Value);
        json.Should().Contain("acct-1");
        json.Should().Contain("acct-2");
        json.Should().Contain("claude-sonnet-4-6");
        json.Should().Contain("gpt-4o");
        json.Should().Contain("o1-preview");
        json.Should().Contain("\"count\":2");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _repo.VerifyAll();
        _secretStore.VerifyAll();
    }

    private Task<ToolResult> ExecuteAsync(ConfigUpdateArgs args)
    {
        var context = new ToolContext
        {
            AgentId = "test-agent",
            CallId = "test-call",
            Sandbox = new Mock<ISandbox>().Object,
        };
        return _tool.ExecuteAsync((object)args, context, CancellationToken.None);
    }
}
