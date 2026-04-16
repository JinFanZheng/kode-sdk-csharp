using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class SecretMigrationReportIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Secret_migration_report_should_generate_and_persist_artifact()
    {
        using var workspace = new TempWorkspaceRoot();
        var fallbackEnvironmentVariable = $"KODACLAW_SECRET_MIGRATION_FALLBACK_{Guid.NewGuid():N}";

        var gatewayTokenSecretRef = new SecretRef("memory", "gateway", "report-token");
        var modelSecretRef = new SecretRef("memory", "models", "primary");
        var channelSecretRef = new SecretRef("memory", "channels", "telegram-main");
        var pluginSecretRef = new SecretRef("memory", "plugins", "fixture-token");
        var missingChannelSecretRef = new SecretRef("memory", "channels", "telegram-missing");
        var missingPluginHeaderSecretRef = new SecretRef("memory", "plugins", "fixture-header-missing");
        var secretStore = new FakeSecretStore(new Dictionary<string, string?>
        {
            [gatewayTokenSecretRef.ToReferenceString()] = "secret-report-token",
            [modelSecretRef.ToReferenceString()] = "model-secret",
            [channelSecretRef.ToReferenceString()] = "telegram-secret",
            [pluginSecretRef.ToReferenceString()] = "plugin-secret",
        });

        Environment.SetEnvironmentVariable(fallbackEnvironmentVariable, "model-fallback-secret");

        try
        {
            await using var hosted = await HostedGateway.StartAsync(
                gatewayToken: string.Empty,
                workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                    requiresBootstrap: false,
                    rootPath: workspace.Path),
                configureServices: services =>
                {
                    services.AddSingleton<ISecretStore>(secretStore);
                },
                configureConfiguration: configuration =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["KODACLAW_WORKSPACE_ROOT"] = workspace.Path,
                        ["KODACLAW_GATEWAY_TOKEN_SECRET_REF"] = gatewayTokenSecretRef.ToReferenceString(),
                        ["Runtime:OpenAIApiKey"] = "runtime-openai-legacy",
                    });
                },
                useTestWorkspaceService: false);

            hosted.Client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", "secret-report-token");

            await SeedModelEndpointsAsync(hosted.Services, modelSecretRef, fallbackEnvironmentVariable);
            await SeedChannelsAsync(hosted.Services, channelSecretRef, missingChannelSecretRef);
            await SeedPluginsAsync(hosted.Services, workspace.Path, pluginSecretRef, missingPluginHeaderSecretRef);

            var response = await hosted.Client.GetAsync("/api/system/secret-migration-report");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var payload = await response.Content.ReadFromJsonAsync<SecretMigrationReport>();
            payload.Should().NotBeNull();

            payload!.Summary.TotalCount.Should().Be(10);
            payload.Summary.MigratedCount.Should().Be(4);
            payload.Summary.LegacyFallbackCount.Should().Be(4);
            payload.Summary.MissingCount.Should().Be(2);
            payload.ArtifactPath.Should().Be("config/secret-migration-report.json");

            payload.Items.Should().ContainSingle(item =>
                item.Id == "gateway-token" &&
                item.State == SecretMigrationState.Migrated &&
                item.ConfiguredSecretRef == gatewayTokenSecretRef.ToReferenceString());
            payload.Items.Should().ContainSingle(item =>
                item.Id == "runtime-openai" &&
                item.State == SecretMigrationState.LegacyFallback &&
                item.LegacySource == "configurationValue:Runtime:OpenAIApiKey");
            payload.Items.Should().ContainSingle(item =>
                item.Id == "provider-account:account-primary" &&
                item.State == SecretMigrationState.Migrated &&
                item.ConfiguredSecretRef == modelSecretRef.ToReferenceString());
            payload.Items.Should().ContainSingle(item =>
                item.Id == "provider-account:account-fallback" &&
                item.State == SecretMigrationState.LegacyFallback &&
                item.LegacySource == $"environmentVariable:{fallbackEnvironmentVariable}");
            payload.Items.Should().ContainSingle(item =>
                item.Id == "channel-account:telegram-main" &&
                item.State == SecretMigrationState.Migrated &&
                item.ConfiguredSecretRef == channelSecretRef.ToReferenceString());
            payload.Items.Should().ContainSingle(item =>
                item.Id == "channel-account:webhook-main" &&
                item.State == SecretMigrationState.LegacyFallback &&
                item.LegacySource == "literalConfigValue");
            payload.Items.Should().ContainSingle(item =>
                item.Id == "channel-account:telegram-missing" &&
                item.State == SecretMigrationState.Missing &&
                item.ConfiguredSecretRef == missingChannelSecretRef.ToReferenceString());
            payload.Items.Should().ContainSingle(item =>
                item.Id == "plugin-environment:plugin.fixture.report:PLUGIN_API_TOKEN" &&
                item.State == SecretMigrationState.Migrated &&
                item.ConfiguredSecretRef == pluginSecretRef.ToReferenceString());
            payload.Items.Should().ContainSingle(item =>
                item.Id == "plugin-header:plugin.fixture.report:Authorization" &&
                item.State == SecretMigrationState.LegacyFallback &&
                item.LegacySource == "literalHeaderValue:Authorization");
            payload.Items.Should().ContainSingle(item =>
                item.Id == "plugin-header:plugin.fixture.report:X-API-Key" &&
                item.State == SecretMigrationState.Missing &&
                item.ConfiguredSecretRef == missingPluginHeaderSecretRef.ToReferenceString());

            var artifactPath = Path.Combine(
                workspace.Path,
                KodaClawWorkspaceLayout.ConfigDirectory,
                KodaClawWorkspaceLayout.SecretMigrationReportFile);
            File.Exists(artifactPath).Should().BeTrue();

            var persistedJson = await File.ReadAllTextAsync(artifactPath);
            var persisted = JsonSerializer.Deserialize<SecretMigrationReport>(persistedJson, JsonOptions);
            persisted.Should().NotBeNull();
            persisted!.Summary.Should().Be(payload.Summary);
            persisted.Items.Should().BeEquivalentTo(payload.Items);
        }
        finally
        {
            Environment.SetEnvironmentVariable(fallbackEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task Secret_migration_report_should_scan_all_channel_and_plugin_pages()
    {
        using var workspace = new TempWorkspaceRoot();
        var gatewayTokenSecretRef = new SecretRef("memory", "gateway", "paged-report-token");
        const int recordCount = 55;
        var secretValues = new Dictionary<string, string?>
        {
            [gatewayTokenSecretRef.ToReferenceString()] = "secret-report-token",
        };

        for (var index = 0; index < recordCount; index++)
        {
            var channelSecretRef = new SecretRef("memory", "channels", $"telegram-{index:D3}");
            var pluginSecretRef = new SecretRef("memory", "plugins", $"plugin-{index:D3}");
            secretValues[channelSecretRef.ToReferenceString()] = $"telegram-secret-{index:D3}";
            secretValues[pluginSecretRef.ToReferenceString()] = $"plugin-secret-{index:D3}";
        }

        var secretStore = new FakeSecretStore(secretValues);

        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: string.Empty,
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false,
                rootPath: workspace.Path),
            configureServices: services =>
            {
                services.AddSingleton<ISecretStore>(secretStore);
            },
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspace.Path,
                    ["KODACLAW_GATEWAY_TOKEN_SECRET_REF"] = gatewayTokenSecretRef.ToReferenceString(),
                });
            },
            useTestWorkspaceService: false);

        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "secret-report-token");

        await SeedPagedChannelsAsync(hosted.Services, recordCount);
        await SeedPagedPluginsAsync(hosted.Services, workspace.Path, recordCount);

        var response = await hosted.Client.GetAsync("/api/system/secret-migration-report");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<SecretMigrationReport>();
        payload.Should().NotBeNull();

        payload!.Summary.TotalCount.Should().Be(1 + recordCount + recordCount);
        payload.Summary.MigratedCount.Should().Be(1 + recordCount + recordCount);
        payload.Summary.LegacyFallbackCount.Should().Be(0);
        payload.Summary.MissingCount.Should().Be(0);
        payload.Items.Should().Contain(item =>
            item.Id == "channel-account:telegram-054" &&
            item.ConfiguredSecretRef == "memory:channels:telegram-054");
        payload.Items.Should().Contain(item =>
            item.Id == "plugin-environment:plugin.fixture.paged.054:PLUGIN_API_TOKEN" &&
            item.ConfiguredSecretRef == "memory:plugins:plugin-054");
    }

    private static async Task SeedModelEndpointsAsync(
        IServiceProvider services,
        SecretRef modelSecretRef,
        string fallbackEnvironmentVariable)
    {
        var repository = services.GetRequiredService<IProviderAccountRepository>();
        var now = new DateTimeOffset(2026, 3, 19, 10, 0, 0, TimeSpan.Zero);

        await repository.AddAccountAsync(new ProviderAccount(
            Id: "account-primary",
            DisplayName: "Primary account",
            ProviderKind: ModelProviderKind.OpenAICompatible,
            BaseUrl: "https://proxy.example.com",
            ApiKeySecretRef: modelSecretRef.ToReferenceString(),
            ApiKeyEnvironmentVariable: null,
            AccessMode: "api",
            Enabled: true,
            CreatedAt: now,
            UpdatedAt: now));
        await repository.AddModelAsync(new AccountModel(
            Id: "model-primary",
            AccountId: "account-primary",
            DisplayName: "Primary model",
            ModelId: "o3",
            Capabilities: ModelCapabilitySet.Text,
            IsDefaultForAccount: true,
            IsGlobalDefault: true,
            Enabled: true,
            CreatedAt: now,
            UpdatedAt: now));

        await repository.AddAccountAsync(new ProviderAccount(
            Id: "account-fallback",
            DisplayName: "Fallback account",
            ProviderKind: ModelProviderKind.OpenAICompatible,
            BaseUrl: "https://proxy.example.com",
            ApiKeySecretRef: null,
            ApiKeyEnvironmentVariable: fallbackEnvironmentVariable,
            AccessMode: "api",
            Enabled: true,
            CreatedAt: now.AddMinutes(1),
            UpdatedAt: now.AddMinutes(1)));
        await repository.AddModelAsync(new AccountModel(
            Id: "model-fallback",
            AccountId: "account-fallback",
            DisplayName: "Fallback model",
            ModelId: "o3-mini",
            Capabilities: ModelCapabilitySet.Text,
            IsDefaultForAccount: true,
            IsGlobalDefault: false,
            Enabled: true,
            CreatedAt: now.AddMinutes(1),
            UpdatedAt: now.AddMinutes(1)));
    }

    private static async Task SeedChannelsAsync(
        IServiceProvider services,
        SecretRef channelSecretRef,
        SecretRef missingChannelSecretRef)
    {
        var repository = services.GetRequiredService<IChannelAccountRepository>();
        var now = new DateTimeOffset(2026, 3, 19, 11, 0, 0, TimeSpan.Zero);

        await repository.UpsertAsync(new ChannelAccount(
            Id: "telegram-main",
            ConnectorKind: ChannelConnectorKind.Telegram,
            DisplayName: "Telegram Main",
            State: ChannelAccountState.Connected,
            CreatedAt: now,
            UpdatedAt: now,
            CredentialReference: channelSecretRef.ToReferenceString()));

        await repository.UpsertAsync(new ChannelAccount(
            Id: "webhook-main",
            ConnectorKind: ChannelConnectorKind.GenericWebhook,
            DisplayName: "Webhook Main",
            State: ChannelAccountState.Connected,
            CreatedAt: now.AddMinutes(1),
            UpdatedAt: now.AddMinutes(1),
            ConfigurationJson: """{"sharedSecret":"webhook-inline-secret","defaultThreadType":"Group"}"""));

        await repository.UpsertAsync(new ChannelAccount(
            Id: "telegram-missing",
            ConnectorKind: ChannelConnectorKind.Telegram,
            DisplayName: "Telegram Missing",
            State: ChannelAccountState.Disconnected,
            CreatedAt: now.AddMinutes(2),
            UpdatedAt: now.AddMinutes(2),
            CredentialReference: missingChannelSecretRef.ToReferenceString()));
    }

    private static async Task SeedPluginsAsync(
        IServiceProvider services,
        string workspaceRoot,
        SecretRef pluginSecretRef,
        SecretRef missingPluginHeaderSecretRef)
    {
        var repository = services.GetRequiredService<IPluginRegistryRepository>();
        var pluginRootPath = Path.Combine(workspaceRoot, "workspace", "plugins", "plugin.fixture.report");
        Directory.CreateDirectory(pluginRootPath);

        var now = new DateTimeOffset(2026, 3, 19, 12, 0, 0, TimeSpan.Zero);
        await repository.UpsertAsync(new PluginRecord(
            Id: "plugin.fixture.report",
            Manifest: new PluginManifest(
                Id: "plugin.fixture.report",
                Name: "Fixture Report Plugin",
                Version: "0.1.0",
                Types: [PluginType.Tool],
                Runtime: new PluginRuntimeSpec(
                    Transport: PluginTransportKind.Stdio,
                    Command: "dotnet",
                    Args: ["fixture.dll"],
                    Headers: new Dictionary<string, string>
                    {
                        ["Authorization"] = "Bearer legacy-plugin-header"
                    },
                    EnvironmentReferences: new Dictionary<string, string>
                    {
                        ["PLUGIN_API_TOKEN"] = pluginSecretRef.ToReferenceString()
                    },
                    HeaderReferences: new Dictionary<string, string>
                    {
                        ["X-API-Key"] = missingPluginHeaderSecretRef.ToReferenceString()
                    }),
                Permissions: new PluginPermissionSet(Network: true),
                Capabilities: new PluginCapabilitySet(Tools: ["echo"])),
            InstallSource: PluginInstallSource.LocalDirectory,
            RootPath: pluginRootPath,
            TrustState: PluginTrustState.Trusted,
            Enabled: true,
            RuntimeState: PluginRuntimeState.Running,
            DiscoveredAt: now,
            InstalledAt: now,
            UpdatedAt: now));
    }

    private static async Task SeedPagedChannelsAsync(IServiceProvider services, int recordCount)
    {
        var repository = services.GetRequiredService<IChannelAccountRepository>();
        var now = new DateTimeOffset(2026, 3, 19, 13, 0, 0, TimeSpan.Zero);

        for (var index = 0; index < recordCount; index++)
        {
            await repository.UpsertAsync(new ChannelAccount(
                Id: $"telegram-{index:D3}",
                ConnectorKind: ChannelConnectorKind.Telegram,
                DisplayName: $"Telegram {index:D3}",
                State: ChannelAccountState.Connected,
                CreatedAt: now.AddMinutes(index),
                UpdatedAt: now.AddMinutes(index),
                CredentialReference: $"memory:channels:telegram-{index:D3}"));
        }
    }

    private static async Task SeedPagedPluginsAsync(IServiceProvider services, string workspaceRoot, int recordCount)
    {
        var repository = services.GetRequiredService<IPluginRegistryRepository>();
        var now = new DateTimeOffset(2026, 3, 19, 14, 0, 0, TimeSpan.Zero);

        for (var index = 0; index < recordCount; index++)
        {
            var pluginId = $"plugin.fixture.paged.{index:D3}";
            var pluginRootPath = Path.Combine(workspaceRoot, "workspace", "plugins", pluginId);
            Directory.CreateDirectory(pluginRootPath);

            await repository.UpsertAsync(new PluginRecord(
                Id: pluginId,
                Manifest: new PluginManifest(
                    Id: pluginId,
                    Name: $"Paged Plugin {index:D3}",
                    Version: "0.1.0",
                    Types: [PluginType.Tool],
                    Runtime: new PluginRuntimeSpec(
                        Transport: PluginTransportKind.Stdio,
                        Command: "dotnet",
                        Args: ["fixture.dll"],
                        EnvironmentReferences: new Dictionary<string, string>
                        {
                            ["PLUGIN_API_TOKEN"] = $"memory:plugins:plugin-{index:D3}",
                        }),
                    Permissions: new PluginPermissionSet(Network: true),
                    Capabilities: new PluginCapabilitySet(Tools: ["echo"])),
                InstallSource: PluginInstallSource.LocalDirectory,
                RootPath: pluginRootPath,
                TrustState: PluginTrustState.Trusted,
                Enabled: true,
                RuntimeState: PluginRuntimeState.Running,
                DiscoveredAt: now.AddMinutes(index),
                InstalledAt: now.AddMinutes(index),
                UpdatedAt: now.AddMinutes(index)));
        }
    }

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-secret-migration-report",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
