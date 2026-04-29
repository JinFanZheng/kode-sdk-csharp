using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Models;
using KodaClaw.Storage.Json.Repositories;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

/// <summary>
/// KC-3503: ModelRegistrySeedService integration tests.
/// Verifies that the seed service populates provider accounts from env-var
/// config when the registry is empty at startup, and skips seeding when the
/// registry is already populated.
/// </summary>
public sealed class ModelRegistrySeedServiceIntegrationTests
{
    private const string GatewayToken = "test-token";

    // ── Seeds from env vars when registry is empty ────────────────────────────

    [Fact]
    public async Task Seed_creates_anthropic_account_when_registry_is_empty_and_anthropic_env_var_set()
    {
        using var workspace = new TempWorkspaceRoot("seed-anthropic");

        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: GatewayToken,
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false, rootPath: workspace.Path),
            configureConfiguration: config =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspace.Path,
                    ["KODACLAW_DEFAULT_MODEL"] = "claude-sonnet-4-6",
                    ["ANTHROPIC_API_KEY"] = "sk-ant-test",
                });
            },
            useTestWorkspaceService: false);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var response = await hosted.Client.GetAsync("/api/models");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<List<AccountModelResponse>>();
        body.Should().NotBeNull();
        body!.Should().HaveCount(1,
            because: "seed service should have created one model from env-var config");

        var seeded = body[0];
        seeded.ModelId.Should().Be("claude-sonnet-4-6");
        seeded.IsGlobalDefault.Should().BeTrue(because: "seed service marks the seeded model as global default");
    }

    [Fact]
    public async Task Seed_creates_openai_account_when_registry_is_empty_and_openai_env_var_set()
    {
        using var workspace = new TempWorkspaceRoot("seed-openai");

        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: GatewayToken,
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false, rootPath: workspace.Path),
            configureConfiguration: config =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspace.Path,
                    ["KODACLAW_DEFAULT_MODEL"] = "gpt-4o-mini",
                    ["OPENAI_API_KEY"] = "sk-openai-test",
                });
            },
            useTestWorkspaceService: false);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var response = await hosted.Client.GetAsync("/api/models");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<List<AccountModelResponse>>();
        body.Should().NotBeNull();
        body!.Should().HaveCount(1,
            because: "seed service should have created one model from env-var config");

        var seeded = body[0];
        seeded.ModelId.Should().Be("gpt-4o-mini");
        seeded.IsGlobalDefault.Should().BeTrue();
    }

    // ── Skips seeding when registry already has accounts ──────────────────────

    [Fact]
    public async Task Seed_skips_when_registry_already_has_accounts()
    {
        using var workspace = new TempWorkspaceRoot("seed-skip");

        // Pre-populate the registry BEFORE starting the gateway
        var repo = new JsonProviderAccountRepository(workspace.Path);
        var now = DateTimeOffset.UtcNow;
        await repo.AddAccountAsync(new ProviderAccount(
            Id:                        "account-existing",
            DisplayName:               "Pre-existing",
            ProviderKind:              ModelProviderKind.OpenAI,
            BaseUrl:                   null,
            ApiKeySecretRef:           null,
            ApiKeyEnvironmentVariable: "OPENAI_API_KEY",
            AccessMode:                "api",
            Enabled:                   true,
            CreatedAt:                 now,
            UpdatedAt:                 now));
        await repo.AddModelAsync(new AccountModel(
            Id:                  "model-existing",
            AccountId:           "account-existing",
            DisplayName:         "Pre-existing",
            ModelId:             "gpt-4o",
            Capabilities:        ModelCapabilitySet.Text,
            IsDefaultForAccount: true,
            IsGlobalDefault:     true,
            Enabled:             true,
            CreatedAt:           now,
            UpdatedAt:           now));

        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: GatewayToken,
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false, rootPath: workspace.Path),
            configureConfiguration: config =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspace.Path,
                    ["KODACLAW_DEFAULT_MODEL"] = "claude-sonnet-4-6",
                    ["ANTHROPIC_API_KEY"] = "sk-ant-skip",
                });
            },
            useTestWorkspaceService: false);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var response = await hosted.Client.GetAsync("/api/models");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<List<AccountModelResponse>>();
        body.Should().NotBeNull();
        body!.Should().HaveCount(1,
            because: "seed service should not add entries when registry already has accounts");
        body[0].Id.Should().Be("model-existing");
    }

    // ── No env vars = no seed ─────────────────────────────────────────────────

    [Fact]
    public async Task Seed_does_nothing_when_no_env_vars_configured()
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);

        using var workspace = new TempWorkspaceRoot("seed-empty");

        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: GatewayToken,
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false, rootPath: workspace.Path),
            configureConfiguration: config =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspace.Path,
                });
            },
            useTestWorkspaceService: false);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var response = await hosted.Client.GetAsync("/api/models");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<List<AccountModelResponse>>();
        body.Should().NotBeNull();
        body!.Should().BeEmpty(because: "no env-var config means no seed");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot(string tag)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"kodaclaw-seed-{tag}",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
