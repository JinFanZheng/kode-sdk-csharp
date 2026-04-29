using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Models;
using KodaClaw.Contracts.Secrets;
using KodaClaw.Storage.Json.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

/// <summary>
/// KC-DOCKER-005: Integration tests for the Docker onboarding path:
///   GET  /healthz          — unauthenticated health probe
///   GET  /setup            — Setup Wizard HTML page
///   POST /setup/complete   — writes model registry from provided provider config
///   ConfigBootstrapService — ENV-var path seeds registry at startup
/// </summary>
public sealed class SetupWizardIntegrationTests
{
    // ── /healthz ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Healthz_returns_200_without_authentication()
    {
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(requiresBootstrap: false));

        var response = await hosted.Client.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<HealthzResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("ok");
    }

    [Fact]
    public async Task Healthz_does_not_require_gateway_token()
    {
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "secret-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(requiresBootstrap: false));

        // No Authorization header sent
        var response = await hosted.Client.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "/healthz must bypass auth for Docker health checks");
    }

    // ── GET /setup ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Setup_get_returns_html_content_type()
    {
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(requiresBootstrap: false));

        var response = await hosted.Client.GetAsync("/setup");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
    }

    [Fact]
    public async Task Setup_get_html_contains_setup_complete_endpoint_reference()
    {
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(requiresBootstrap: false));

        var html = await hosted.Client.GetStringAsync("/setup");

        html.Should().Contain("/setup/complete",
            because: "the setup page must post to /setup/complete");
        html.Should().Contain("modelId",
            because: "the setup form must include the modelId field in the JSON body");
        html.Should().Contain("apiKey",
            because: "the setup form must include the apiKey field in the JSON body");
    }

    [Fact]
    public async Task Setup_get_html_contains_all_major_providers()
    {
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(requiresBootstrap: false));

        var html = await hosted.Client.GetStringAsync("/setup");

        html.Should().Contain("Anthropic");
        html.Should().Contain("OpenAI");
        html.Should().Contain("DeepSeek");
        html.Should().Contain("GLM");
        html.Should().Contain("Kimi");
        html.Should().Contain("Ollama");
    }

    [Fact]
    public async Task Setup_get_html_contains_coding_plan_mode()
    {
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(requiresBootstrap: false));

        var html = await hosted.Client.GetStringAsync("/setup");

        html.Should().Contain("coding-plan",
            because: "Coding Plan mode toggle must be present in setup HTML");
        html.Should().Contain("AnthropicCompatible",
            because: "Coding Plan providers use AnthropicCompatible kind");
        html.Should().Contain("open.bigmodel.cn/api/anthropic",
            because: "GLM Coding Plan base URL must be present");
        html.Should().Contain("api.minimaxi.com/anthropic",
            because: "MiniMax Coding Plan base URL must be present");
        html.Should().Contain("token-plan-cn.xiaomimimo.com/anthropic",
            because: "Xiaomi Coding Plan base URL must be present");
    }

    // ── POST /setup/complete ───────────────────────────────────────────────────

    [Fact]
    public async Task Setup_complete_returns_400_when_provider_missing()
    {
        using var workspace = new TempWorkspaceRoot("setup-no-provider");
        var fakeSecret = new SetupFakeSecretStore(new Dictionary<string, string?>());

        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false, rootPath: workspace.Path),
            configureServices: services =>
                services.AddSingleton<ISecretStore>(fakeSecret),
            configureConfiguration: config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspace.Path,
                }),
            useTestWorkspaceService: false);

        var response = await hosted.Client.PostAsJsonAsync("/setup/complete", new
        {
            provider = (string?)null,
            modelId  = (string?)null,
            apiKey   = (string?)null,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Setup_complete_returns_400_when_model_missing()
    {
        using var workspace = new TempWorkspaceRoot("setup-no-model");
        var fakeSecret = new SetupFakeSecretStore(new Dictionary<string, string?>());

        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false, rootPath: workspace.Path),
            configureServices: services =>
                services.AddSingleton<ISecretStore>(fakeSecret),
            configureConfiguration: config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspace.Path,
                }),
            useTestWorkspaceService: false);

        var response = await hosted.Client.PostAsJsonAsync("/setup/complete", new
        {
            provider = "Anthropic",
            modelId  = (string?)null,
            apiKey   = "sk-ant-test",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Setup_complete_returns_400_when_key_missing_for_hosted_provider()
    {
        using var workspace = new TempWorkspaceRoot("setup-no-key");
        var fakeSecret = new SetupFakeSecretStore(new Dictionary<string, string?>());

        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false, rootPath: workspace.Path),
            configureServices: services =>
                services.AddSingleton<ISecretStore>(fakeSecret),
            configureConfiguration: config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspace.Path,
                }),
            useTestWorkspaceService: false);

        var response = await hosted.Client.PostAsJsonAsync("/setup/complete", new
        {
            provider = "Anthropic",
            modelId  = "claude-sonnet-4-20250514",
            apiKey   = (string?)null,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Setup_complete_with_anthropic_creates_registry_endpoint()
    {
        using var workspace = new TempWorkspaceRoot("setup-anthropic");
        var fakeSecret = new SetupFakeSecretStore(new Dictionary<string, string?>());

        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false, rootPath: workspace.Path),
            configureServices: services =>
                services.AddSingleton<ISecretStore>(fakeSecret),
            configureConfiguration: config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspace.Path,
                }),
            useTestWorkspaceService: false);

        var response = await hosted.Client.PostAsJsonAsync("/setup/complete", new
        {
            provider    = "Anthropic",
            modelId     = "claude-sonnet-4-20250514",
            apiKey      = "sk-ant-integration-test",
            displayName = "Claude Sonnet 4.5",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var registry = new JsonProviderAccountRepository(workspace.Path);
        var accounts = await registry.ListAccountsAsync();
        var models   = await registry.ListAllModelsAsync();
        accounts.Should().HaveCount(1);
        accounts[0].ProviderKind.Should().Be(ModelProviderKind.Anthropic);
        models.Should().HaveCount(1);
        models[0].IsGlobalDefault.Should().BeTrue();
        models[0].ModelId.Should().Be("claude-sonnet-4-20250514");
        fakeSecret.StoredValues.Should().ContainValue("sk-ant-integration-test");
    }

    [Fact]
    public async Task Setup_complete_with_openai_creates_registry_endpoint()
    {
        using var workspace = new TempWorkspaceRoot("setup-openai");
        var fakeSecret = new SetupFakeSecretStore(new Dictionary<string, string?>());

        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false, rootPath: workspace.Path),
            configureServices: services =>
                services.AddSingleton<ISecretStore>(fakeSecret),
            configureConfiguration: config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspace.Path,
                }),
            useTestWorkspaceService: false);

        var response = await hosted.Client.PostAsJsonAsync("/setup/complete", new
        {
            provider = "OpenAI",
            modelId  = "gpt-4o",
            apiKey   = "sk-openai-integration-test",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var registry = new JsonProviderAccountRepository(workspace.Path);
        var accounts = await registry.ListAccountsAsync();
        accounts.Should().HaveCount(1);
        accounts[0].ProviderKind.Should().Be(ModelProviderKind.OpenAI);
        fakeSecret.StoredValues.Should().ContainValue("sk-openai-integration-test");
    }

    [Fact]
    public async Task Setup_complete_with_openai_compatible_creates_registry_endpoint()
    {
        using var workspace = new TempWorkspaceRoot("setup-glm");
        var fakeSecret = new SetupFakeSecretStore(new Dictionary<string, string?>());

        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false, rootPath: workspace.Path),
            configureServices: services =>
                services.AddSingleton<ISecretStore>(fakeSecret),
            configureConfiguration: config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspace.Path,
                }),
            useTestWorkspaceService: false);

        var response = await hosted.Client.PostAsJsonAsync("/setup/complete", new
        {
            provider    = "OpenAICompatible",
            modelId     = "glm-4-flash",
            apiKey      = "glm-api-key-test",
            baseUrl     = "https://open.bigmodel.cn/api/paas/v4",
            displayName = "GLM-4 Flash",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var registry = new JsonProviderAccountRepository(workspace.Path);
        var accounts = await registry.ListAccountsAsync();
        var models   = await registry.ListAllModelsAsync();
        accounts.Should().HaveCount(1);
        accounts[0].ProviderKind.Should().Be(ModelProviderKind.OpenAICompatible);
        accounts[0].BaseUrl.Should().Be("https://open.bigmodel.cn/api/paas/v4");
        models.Should().HaveCount(1);
        models[0].ModelId.Should().Be("glm-4-flash");
        fakeSecret.StoredValues.Should().ContainValue("glm-api-key-test");
    }

    [Fact]
    public async Task Setup_complete_with_ollama_accepts_empty_api_key()
    {
        using var workspace = new TempWorkspaceRoot("setup-ollama");
        var fakeSecret = new SetupFakeSecretStore(new Dictionary<string, string?>());

        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false, rootPath: workspace.Path),
            configureServices: services =>
                services.AddSingleton<ISecretStore>(fakeSecret),
            configureConfiguration: config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspace.Path,
                }),
            useTestWorkspaceService: false);

        var response = await hosted.Client.PostAsJsonAsync("/setup/complete", new
        {
            provider = "OpenAICompatible",
            modelId  = "llama3.2",
            apiKey   = (string?)null,
            baseUrl  = "http://localhost:11434/v1",
        });

        // Local providers (localhost base URL) don't need an API key
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var registry = new JsonProviderAccountRepository(workspace.Path);
        var accounts = await registry.ListAccountsAsync();
        var models   = await registry.ListAllModelsAsync();
        accounts.Should().HaveCount(1);
        accounts[0].ProviderKind.Should().Be(ModelProviderKind.OpenAICompatible);
        models.Should().HaveCount(1);
        models[0].ModelId.Should().Be("llama3.2");
        fakeSecret.StoredValues.Should().BeEmpty(because: "Ollama requires no API key");
    }

    [Fact]
    public async Task Setup_complete_is_idempotent_does_not_duplicate_when_registry_nonempty()
    {
        using var workspace = new TempWorkspaceRoot("setup-idempotent");
        var fakeSecret = new SetupFakeSecretStore(new Dictionary<string, string?>());

        // Pre-populate registry
        var registry = new JsonProviderAccountRepository(workspace.Path);
        var now      = DateTimeOffset.UtcNow;
        await registry.AddAccountAsync(new ProviderAccount(
            Id:                        "existing-account",
            DisplayName:               "Existing",
            ProviderKind:              ModelProviderKind.Anthropic,
            BaseUrl:                   null,
            ApiKeySecretRef:           null,
            ApiKeyEnvironmentVariable: "ANTHROPIC_API_KEY",
            AccessMode:                "api",
            Enabled:                   true,
            CreatedAt:                 now,
            UpdatedAt:                 now));
        await registry.AddModelAsync(new AccountModel(
            Id:                  "existing-model",
            AccountId:           "existing-account",
            DisplayName:         "Existing",
            ModelId:             "existing-model",
            Capabilities:        ModelCapabilitySet.Text,
            IsDefaultForAccount: true,
            IsGlobalDefault:     true,
            Enabled:             true,
            CreatedAt:           now,
            UpdatedAt:           now));

        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false, rootPath: workspace.Path),
            configureServices: services =>
                services.AddSingleton<ISecretStore>(fakeSecret),
            configureConfiguration: config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspace.Path,
                }),
            useTestWorkspaceService: false);

        var response = await hosted.Client.PostAsJsonAsync("/setup/complete", new
        {
            provider = "Anthropic",
            modelId  = "claude-sonnet-4-20250514",
            apiKey   = "sk-ant-NEW",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var accounts = await registry.ListAccountsAsync();
        accounts.Should().HaveCount(1,
            because: "WriteIfAbsent must not add a second account when registry is already populated");
        accounts[0].Id.Should().Be("existing-account");
    }

    [Fact]
    public async Task Setup_complete_marks_onboarding_as_completed()
    {
        using var workspace = new TempWorkspaceRoot("setup-onboarding-complete");
        var fakeSecret = new SetupFakeSecretStore(new Dictionary<string, string?>());

        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false, rootPath: workspace.Path),
            configureServices: services =>
                services.AddSingleton<ISecretStore>(fakeSecret),
            configureConfiguration: config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspace.Path,
                }),
            useTestWorkspaceService: false);

        var response = await hosted.Client.PostAsJsonAsync("/setup/complete", new
        {
            provider = "Anthropic",
            modelId  = "claude-sonnet-4-20250514",
            apiKey   = "sk-ant-integration-test",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Onboarding state file must now exist and isCompleted == true
        var onboardingPath = Path.Combine(workspace.Path, "config", "onboarding.json");
        File.Exists(onboardingPath).Should().BeTrue(
            because: "POST /setup/complete must write the onboarding state file");

        var json    = await File.ReadAllTextAsync(onboardingPath);
        var stateEl = System.Text.Json.JsonDocument.Parse(json).RootElement;
        stateEl.GetProperty("IsCompleted").GetBoolean().Should().BeTrue(
            because: "React app checks isCompleted to skip its own onboarding wizard");
    }

    // ── ENV-var bootstrap (ConfigBootstrapService) ──────────────────────────────

    [Fact]
    public async Task ConfigBootstrapService_zero_env_does_not_write_registry()
    {
        using var workspace = new TempWorkspaceRoot("bootstrap-zero-env");
        var fakeSecret = new SetupFakeSecretStore(new Dictionary<string, string?>());

        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false, rootPath: workspace.Path),
            configureServices: services =>
                services.AddSingleton<ISecretStore>(fakeSecret),
            configureConfiguration: config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspace.Path,
                }),
            useTestWorkspaceService: false);

        var registry = new JsonProviderAccountRepository(workspace.Path);
        var accounts = await registry.ListAccountsAsync();
        accounts.Should().BeEmpty(
            because: "registry must remain empty when no KODACLAW_*_API_KEY is set");
    }

    [Fact]
    public async Task ConfigBootstrapService_env_anthropic_key_seeds_registry()
    {
        using var workspace = new TempWorkspaceRoot("bootstrap-env-anthropic");
        var fakeSecret = new SetupFakeSecretStore(new Dictionary<string, string?>());

        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false, rootPath: workspace.Path),
            configureServices: services =>
                services.AddSingleton<ISecretStore>(fakeSecret),
            configureConfiguration: config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"]   = workspace.Path,
                    ["KODACLAW_ANTHROPIC_API_KEY"] = "sk-ant-env-test",
                }),
            useTestWorkspaceService: false);

        var registry = new JsonProviderAccountRepository(workspace.Path);
        var accounts = await registry.ListAccountsAsync();
        var models   = await registry.ListAllModelsAsync();
        accounts.Should().HaveCount(1,
            because: "ConfigBootstrapService should seed one account from KODACLAW_ANTHROPIC_API_KEY");
        accounts[0].ProviderKind.Should().Be(ModelProviderKind.Anthropic);
        models.Should().HaveCount(1);
        models[0].IsGlobalDefault.Should().BeTrue();
        fakeSecret.StoredValues.Should().ContainValue("sk-ant-env-test");
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private sealed record HealthzResponse(string Status);
}

/// <summary>
/// Spy ISecretStore that captures written secrets for assertions.
/// </summary>
internal sealed class SetupFakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, string?> _initial;
    public Dictionary<string, string?> StoredValues { get; } = new();

    public SetupFakeSecretStore(Dictionary<string, string?> initialValues)
    {
        _initial = initialValues;
        foreach (var kv in initialValues) StoredValues[kv.Key] = kv.Value;
    }

    public Task<string?> GetAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
    {
        StoredValues.TryGetValue(secretRef.ToReferenceString(), out var v);
        return Task.FromResult(v);
    }

    public Task UpsertAsync(SecretRef secretRef, string secretValue, CancellationToken cancellationToken = default)
    {
        StoredValues[secretRef.ToReferenceString()] = secretValue;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
    {
        StoredValues.Remove(secretRef.ToReferenceString());
        return Task.CompletedTask;
    }

    public Task<SecretDescriptor> DescribeAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
        => Task.FromResult(new SecretDescriptor(secretRef, Exists: StoredValues.ContainsKey(secretRef.ToReferenceString()), IsReadOnly: false));
}

internal sealed class TempWorkspaceRoot : IDisposable
{
    public TempWorkspaceRoot(string tag)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kodaclaw-setup-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
