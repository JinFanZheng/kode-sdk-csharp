using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Models;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

/// <summary>
/// Verifies the one-shot legacy-endpoint migration wired through
/// <c>ModelEndpointMigrationHostedService</c>: on startup, any
/// <c>config/models/*.json</c> files left over from the pre-Phase-5 flat
/// <c>ModelEndpoint</c> schema should be lifted into
/// <c>config/accounts/</c> + <c>config/account-models/</c> BEFORE
/// <c>ConfigBootstrapService</c> or <c>ModelRegistrySeedService</c> observes
/// the empty repository. Second startups must be no-ops.
/// </summary>
public sealed class ModelEndpointMigrationIntegrationTests
{
    private const string GatewayToken = "test-token";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [Fact]
    public async Task Migration_lifts_legacy_endpoints_into_provider_accounts_on_startup()
    {
        using var workspace = new TempWorkspaceRoot("migration-lift");
        var now = DateTimeOffset.UtcNow;

        // Seed two legacy endpoints that share the same (Provider, BaseUrl, SecretRef)
        // tuple — the migration must group them under one ProviderAccount.
        WriteLegacyEndpoint(workspace.Path, new LegacyEndpointPayload(
            Id:                        "legacy-claude-sonnet",
            DisplayName:               "Claude Sonnet 4",
            Provider:                  (int)ModelProviderKind.Anthropic,
            ModelId:                   "claude-sonnet-4-6",
            BaseUrl:                   null,
            ApiKeyEnvironmentVariable: null,
            ApiKeySecretRef:           "keychain:config-bootstrap:anthropic",
            Enabled:                   true,
            Capabilities:              (int)ModelCapabilitySet.Text,
            IsDefault:                 true,
            CreatedAt:                 now.AddDays(-7),
            UpdatedAt:                 now.AddDays(-1),
            ContextWindowSize:         200_000,
            MaxOutputTokens:           8192,
            IsReasoning:               false,
            SupportsToolCalling:       true));

        WriteLegacyEndpoint(workspace.Path, new LegacyEndpointPayload(
            Id:                        "legacy-claude-haiku",
            DisplayName:               "Claude Haiku 4",
            Provider:                  (int)ModelProviderKind.Anthropic,
            ModelId:                   "claude-haiku-4-5",
            BaseUrl:                   null,
            ApiKeyEnvironmentVariable: null,
            ApiKeySecretRef:           "keychain:config-bootstrap:anthropic",
            Enabled:                   true,
            Capabilities:              (int)ModelCapabilitySet.Text,
            IsDefault:                 false,
            CreatedAt:                 now.AddDays(-7),
            UpdatedAt:                 now.AddDays(-2),
            ContextWindowSize:         200_000,
            MaxOutputTokens:           8192,
            IsReasoning:               false,
            SupportsToolCalling:       true));

        // And one endpoint from a different provider — must land in a SECOND account.
        WriteLegacyEndpoint(workspace.Path, new LegacyEndpointPayload(
            Id:                        "legacy-gpt-4o",
            DisplayName:               "GPT-4o",
            Provider:                  (int)ModelProviderKind.OpenAI,
            ModelId:                   "gpt-4o",
            BaseUrl:                   null,
            ApiKeyEnvironmentVariable: null,
            ApiKeySecretRef:           "keychain:config-bootstrap:openai",
            Enabled:                   true,
            Capabilities:              (int)ModelCapabilitySet.Text,
            IsDefault:                 false,
            CreatedAt:                 now.AddDays(-5),
            UpdatedAt:                 now.AddDays(-1),
            ContextWindowSize:         128_000,
            MaxOutputTokens:           16_384,
            IsReasoning:               false,
            SupportsToolCalling:       true));

        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var response = await hosted.Client.GetAsync("/api/provider-accounts");
        response.EnsureSuccessStatusCode();

        var accounts = await response.Content.ReadFromJsonAsync<List<ProviderAccountResponse>>();
        accounts.Should().NotBeNull();
        accounts!.Should().HaveCount(2,
            because: "two legacy endpoints share one (provider, base-url, secret) tuple " +
                     "and should collapse into a single ProviderAccount, while the " +
                     "OpenAI endpoint lands in a second account.");

        var anthropic = accounts.Should().ContainSingle(a => a.ProviderKind == ModelProviderKind.Anthropic).Subject;
        anthropic.Models.Should().HaveCount(2);
        anthropic.Models.Should().Contain(m => m.ModelId == "claude-sonnet-4-6");
        anthropic.Models.Should().Contain(m => m.ModelId == "claude-haiku-4-5");
        anthropic.Models.Should().ContainSingle(m => m.IsGlobalDefault && m.ModelId == "claude-sonnet-4-6",
            because: "only the legacy endpoint with IsDefault=true should be marked global-default");
        anthropic.HasApiKey.Should().BeTrue(
            because: "the legacy secret ref was preserved by the migration");

        var openai = accounts.Should().ContainSingle(a => a.ProviderKind == ModelProviderKind.OpenAI).Subject;
        openai.Models.Should().ContainSingle();
        openai.Models[0].ModelId.Should().Be("gpt-4o");

        // The legacy directory should be moved aside, not deleted outright.
        Directory.Exists(Path.Combine(workspace.Path, "config", "models")).Should().BeFalse();
        Directory.Exists(Path.Combine(workspace.Path, "config", "models.migrated")).Should().BeTrue();
        Directory.GetFiles(Path.Combine(workspace.Path, "config", "models.migrated"), "*.json")
            .Should().HaveCount(3, because: "all three legacy files should be in the backup dir");
    }

    [Fact]
    public async Task Migration_is_idempotent_on_second_startup()
    {
        using var workspace = new TempWorkspaceRoot("migration-idempotent");
        var now = DateTimeOffset.UtcNow;

        WriteLegacyEndpoint(workspace.Path, new LegacyEndpointPayload(
            Id:                        "legacy-single",
            DisplayName:               "Claude",
            Provider:                  (int)ModelProviderKind.Anthropic,
            ModelId:                   "claude-sonnet-4-6",
            BaseUrl:                   null,
            ApiKeyEnvironmentVariable: null,
            ApiKeySecretRef:           "keychain:config-bootstrap:anthropic",
            Enabled:                   true,
            Capabilities:              (int)ModelCapabilitySet.Text,
            IsDefault:                 true,
            CreatedAt:                 now,
            UpdatedAt:                 now,
            ContextWindowSize:         200_000,
            MaxOutputTokens:           8192,
            IsReasoning:               false,
            SupportsToolCalling:       true));

        // First startup — runs the migration.
        await using (var first = await StartGatewayAsync(workspace.Path))
        {
            first.Client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", GatewayToken);

            var firstList = await first.Client.GetFromJsonAsync<List<ProviderAccountResponse>>(
                "/api/provider-accounts");
            firstList.Should().NotBeNull();
            firstList!.Should().HaveCount(1);
        }

        // Second startup — should be a no-op: accounts directory already exists,
        // and config/models/ no longer exists, so the migration service must
        // early-out and leave everything intact.
        await using var second = await StartGatewayAsync(workspace.Path);
        second.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var secondList = await second.Client.GetFromJsonAsync<List<ProviderAccountResponse>>(
            "/api/provider-accounts");
        secondList.Should().NotBeNull();
        secondList!.Should().HaveCount(1,
            because: "a second startup must not duplicate the migrated account");
        secondList[0].Models.Should().ContainSingle();
        secondList[0].Models[0].ModelId.Should().Be("claude-sonnet-4-6");
    }

    [Fact]
    public async Task Migration_is_noop_when_no_legacy_directory_exists()
    {
        using var workspace = new TempWorkspaceRoot("migration-noop");

        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var accounts = await hosted.Client.GetFromJsonAsync<List<ProviderAccountResponse>>(
            "/api/provider-accounts");
        accounts.Should().NotBeNull();
        accounts!.Should().BeEmpty(
            because: "without a legacy config/models/ directory there is nothing to migrate " +
                     "and no env-var bootstrap is configured in the test harness.");

        Directory.Exists(Path.Combine(workspace.Path, "config", "models.migrated"))
            .Should().BeFalse();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static Task<HostedGateway> StartGatewayAsync(string workspaceRoot)
    {
        return HostedGateway.StartAsync(
            gatewayToken: GatewayToken,
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false, rootPath: workspaceRoot),
            configureConfiguration: config =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                    // Keep env-var bootstrap OUT of the picture so the migration path
                    // is the only thing that can populate the provider-account repo.
                    ["KODACLAW_ANTHROPIC_API_KEY"] = null,
                    ["KODACLAW_OPENAI_API_KEY"] = null,
                    ["ANTHROPIC_API_KEY"] = null,
                    ["OPENAI_API_KEY"] = null,
                });
            },
            useTestWorkspaceService: false);
    }

    private static void WriteLegacyEndpoint(string workspaceRoot, LegacyEndpointPayload payload)
    {
        var dir = Path.Combine(workspaceRoot, "config", "models");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{payload.Id}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions));
    }

    /// <summary>
    /// Mirrors the (private) <c>ModelEndpointMigrationService.LegacyEndpoint</c>
    /// shape so the test can write realistic legacy JSON files on disk.
    /// Field order and names must stay in sync with the migration reader.
    /// </summary>
    private sealed record LegacyEndpointPayload(
        string Id,
        string DisplayName,
        int Provider,
        string ModelId,
        string? BaseUrl,
        string? ApiKeyEnvironmentVariable,
        string? ApiKeySecretRef,
        bool Enabled,
        int Capabilities,
        bool IsDefault,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        int ContextWindowSize,
        int MaxOutputTokens,
        bool IsReasoning,
        bool SupportsToolCalling);

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot(string tag)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"kodaclaw-migration-{tag}",
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
