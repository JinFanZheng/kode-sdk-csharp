using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KodaClaw.Automation;
using KodaClaw.ChannelHub;
using KodaClaw.Contracts;
using KodaClaw.ControlPlane;
using KodaClaw.ModelHub;
using KodaClaw.Storage.Json.Repositories;
using KodaClaw.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class BackupApiIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Backup_export_preflight_and_import_should_restore_sanitized_workspace()
    {
        using var sourceWorkspace = new TempWorkspaceRoot("kodaclaw-backup-source");
        var modelSecretRef = new SecretRef("memory", "models", "primary");
        var channelSecretRef = new SecretRef("memory", "channels", "telegram-main");
        var webhookSecretRef = new SecretRef("memory", "channels", "webhook-main");
        var pluginEnvironmentSecretRef = new SecretRef("memory", "plugins", "fixture-env");
        var pluginHeaderSecretRef = new SecretRef("memory", "plugins", "fixture-header");

        await using var sourceHosted = await StartHostedGatewayAsync(sourceWorkspace.Path, secretStore: null);
        sourceHosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "backup-token");
        await SeedSourceWorkspaceAsync(
            sourceHosted.Services,
            sourceWorkspace.Path,
            modelSecretRef,
            channelSecretRef,
            webhookSecretRef,
            pluginEnvironmentSecretRef,
            pluginHeaderSecretRef);

        var exportResponse = await sourceHosted.Client.PostAsJsonAsync("/api/system/backup-export", new BackupExportRequest());
        exportResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var exportPayload = await exportResponse.Content.ReadFromJsonAsync<BackupExportResponse>();
        exportPayload.Should().NotBeNull();
        File.Exists(exportPayload!.ArchivePath).Should().BeTrue();
        exportPayload.Manifest.Entries.Should().NotBeEmpty();

        using var extractedArchive = new TempWorkspaceRoot("kodaclaw-backup-extract");
        ZipFile.ExtractToDirectory(exportPayload.ArchivePath, extractedArchive.Path);
        File.Exists(Path.Combine(extractedArchive.Path, KodaClawWorkspaceLayout.BackupManifestFile)).Should().BeTrue();
        File.Exists(Path.Combine(extractedArchive.Path, KodaClawWorkspaceLayout.SessionsDirectory, "main-001", "meta.json")).Should().BeTrue();
        File.Exists(Path.Combine(extractedArchive.Path, KodaClawWorkspaceLayout.SessionsDirectory, "main-001", "messages.json")).Should().BeFalse();

        var extractedChannels = await new JsonChannelAccountRepository(extractedArchive.Path)
            .ListAsync(new ChannelAccountQuery(Limit: 10));
        var extractedBindings = await new JsonThreadBindingRepository(extractedArchive.Path)
            .ListAsync(new ChannelQuery(Limit: 10));
        var extractedPlugins = await new JsonPluginRegistryRepository(extractedArchive.Path)
            .ListAsync(new PluginQuery(Limit: 10));
        var extractedAutomations = await new JsonAutomationDefinitionRepository(extractedArchive.Path)
            .ListAsync(new AutomationDefinitionQuery(Limit: 10));

        extractedChannels.Should().ContainSingle(account =>
            account.Id == "webhook-main" &&
            account.ConfigurationJson != null &&
            !account.ConfigurationJson.Contains("sharedSecret", StringComparison.Ordinal));
        extractedBindings.Should().ContainSingle(binding => binding.LastMessagePreview == null);
        extractedPlugins.Should().ContainSingle(plugin =>
            plugin.Manifest.Runtime.Environment == null &&
            plugin.Manifest.Runtime.Headers == null &&
            plugin.RootPath.Contains("__KODACLAW_WORKSPACE_ROOT__", StringComparison.Ordinal));
        extractedAutomations.Should().ContainSingle(definition =>
            definition.SourcePath != null &&
            definition.SourcePath.Contains("__KODACLAW_WORKSPACE_ROOT__", StringComparison.Ordinal));

        using var targetWorkspace = new TempWorkspaceRoot("kodaclaw-backup-target");
        var targetSecretStore = new FakeSecretStore(new Dictionary<string, string?>
        {
            [modelSecretRef.ToReferenceString()] = "model-secret",
        });
        await using var targetHosted = await StartHostedGatewayAsync(targetWorkspace.Path, targetSecretStore);
        targetHosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "backup-token");

        var preflightResponse = await targetHosted.Client.PostAsJsonAsync(
            "/api/system/backup-import/preflight",
            new BackupImportPreflightRequest(exportPayload.ArchivePath));
        preflightResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preflightPayload = await preflightResponse.Content.ReadFromJsonAsync<BackupImportPreflightResponse>();
        preflightPayload.Should().NotBeNull();
        preflightPayload!.ChecksumVerified.Should().BeTrue();
        preflightPayload.CanImport.Should().BeTrue();
        preflightPayload.Checklist.Items.Should().Contain(item => item.Id == "active-main-session-reset");
        preflightPayload.Checklist.Items.Should().Contain(item => item.Id == "secret-ref-missing:channel:telegram-main");
        preflightPayload.Checklist.Items.Should().Contain(item => item.Id == "plugin-root-missing:plugin.fixture.backup");
        preflightPayload.Checklist.Items.Should().NotContain(item => item.Id == "workspace-pristine-required");

        var importResponse = await targetHosted.Client.PostAsJsonAsync(
            "/api/system/backup-import",
            new BackupImportRequest(exportPayload.ArchivePath));
        importResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var importPayload = await importResponse.Content.ReadFromJsonAsync<BackupImportResponse>();
        importPayload.Should().NotBeNull();
        File.Exists(importPayload!.RepairReportPath).Should().BeTrue();
        importPayload.RestoredPaths.Should().Contain("config/app.json");
        importPayload.SkippedPaths.Should().Contain("identity/device.json");

        var targetWorkspaceService = targetHosted.Services.GetRequiredService<IWorkspaceService>();
        var importedAppConfig = await targetWorkspaceService.LoadAppConfigAsync();
        importedAppConfig.BootstrapCompleted.Should().BeTrue();
        importedAppConfig.ActiveMainSessionId.Should().BeNull();

        var targetChannels = await targetHosted.Services.GetRequiredService<IChannelAccountRepository>()
            .ListAsync(new ChannelAccountQuery(Limit: 10));
        var targetBindings = await targetHosted.Services.GetRequiredService<IThreadBindingRepository>()
            .ListAsync(new ChannelQuery(Limit: 10));
        var targetPlugins = await targetHosted.Services.GetRequiredService<IPluginRegistryRepository>()
            .ListAsync(new PluginQuery(Limit: 10));
        var targetAutomations = await targetHosted.Services.GetRequiredService<IAutomationDefinitionRepository>()
            .ListAsync(new AutomationDefinitionQuery(Limit: 10));

        targetChannels.Should().ContainSingle(account =>
            account.Id == "webhook-main" &&
            account.ConfigurationJson != null &&
            !account.ConfigurationJson.Contains("sharedSecret", StringComparison.Ordinal));
        targetBindings.Should().ContainSingle(binding => binding.LastMessagePreview == null);
        targetPlugins.Should().ContainSingle(plugin =>
            plugin.Id == "plugin.fixture.backup" &&
            !plugin.Enabled &&
            plugin.RootPath == Path.Combine(targetWorkspace.Path, "workspace", "plugins", "plugin.fixture.backup") &&
            plugin.LastError != null &&
            plugin.LastError.Contains("not bundled", StringComparison.Ordinal));
        targetAutomations.Should().ContainSingle(definition =>
            definition.Id == "heartbeat-daily" &&
            definition.SourcePath == Path.Combine(targetWorkspace.Path, "workspace", KodaClawWorkspaceLayout.HeartbeatFile));

        File.Exists(Path.Combine(targetWorkspace.Path, KodaClawWorkspaceLayout.SessionsDirectory, "main-001", "meta.json")).Should().BeTrue();
        var repairReportJson = await File.ReadAllTextAsync(importPayload.RepairReportPath);
        var repairReportResponse = JsonSerializer.Deserialize<ImportRepairReportResponse>(repairReportJson, JsonOptions);
        repairReportResponse.Should().NotBeNull();
        repairReportResponse!.Checklist.Summary.ActionRequiredCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Backup_import_should_block_when_target_workspace_is_not_pristine()
    {
        using var sourceWorkspace = new TempWorkspaceRoot("kodaclaw-backup-conflict-source");
        var modelSecretRef = new SecretRef("memory", "models", "primary");
        var channelSecretRef = new SecretRef("memory", "channels", "telegram-main");
        var webhookSecretRef = new SecretRef("memory", "channels", "webhook-main");
        var pluginEnvironmentSecretRef = new SecretRef("memory", "plugins", "fixture-env");
        var pluginHeaderSecretRef = new SecretRef("memory", "plugins", "fixture-header");

        await using var sourceHosted = await StartHostedGatewayAsync(sourceWorkspace.Path, secretStore: null);
        sourceHosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "backup-token");
        await SeedSourceWorkspaceAsync(
            sourceHosted.Services,
            sourceWorkspace.Path,
            modelSecretRef,
            channelSecretRef,
            webhookSecretRef,
            pluginEnvironmentSecretRef,
            pluginHeaderSecretRef);

        var exportResponse = await sourceHosted.Client.PostAsJsonAsync("/api/system/backup-export", new BackupExportRequest());
        var exportPayload = await exportResponse.Content.ReadFromJsonAsync<BackupExportResponse>();
        exportPayload.Should().NotBeNull();

        using var targetWorkspace = new TempWorkspaceRoot("kodaclaw-backup-conflict-target");
        await using var targetHosted = await StartHostedGatewayAsync(targetWorkspace.Path, new FakeSecretStore(new Dictionary<string, string?>()));
        targetHosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "backup-token");

        var targetModelRepository = targetHosted.Services.GetRequiredService<IProviderAccountRepository>();
        var now = new DateTimeOffset(2026, 3, 19, 15, 0, 0, TimeSpan.Zero);
        await targetModelRepository.AddAccountAsync(new ProviderAccount(
            Id:                        "existing-account",
            DisplayName:               "Existing account",
            ProviderKind:              ModelProviderKind.OpenAICompatible,
            BaseUrl:                   "https://target.example.com",
            ApiKeySecretRef:           null,
            ApiKeyEnvironmentVariable: null,
            AccessMode:                "api",
            Enabled:                   true,
            CreatedAt:                 now,
            UpdatedAt:                 now));
        await targetModelRepository.AddModelAsync(new AccountModel(
            Id:                  "existing-model",
            AccountId:           "existing-account",
            DisplayName:         "Existing model",
            ModelId:             "o4-mini",
            Capabilities:        ModelCapabilitySet.Text,
            IsDefaultForAccount: true,
            IsGlobalDefault:     true,
            Enabled:             true,
            CreatedAt:           now,
            UpdatedAt:           now));

        var preflightResponse = await targetHosted.Client.PostAsJsonAsync(
            "/api/system/backup-import/preflight",
            new BackupImportPreflightRequest(exportPayload!.ArchivePath));
        preflightResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preflightPayload = await preflightResponse.Content.ReadFromJsonAsync<BackupImportPreflightResponse>();
        preflightPayload.Should().NotBeNull();
        preflightPayload!.CanImport.Should().BeFalse();
        preflightPayload.Checklist.Items.Should().Contain(item => item.Id == "workspace-pristine-required");

        var importResponse = await targetHosted.Client.PostAsJsonAsync(
            "/api/system/backup-import",
            new BackupImportRequest(exportPayload.ArchivePath));
        importResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await importResponse.Content.ReadFromJsonAsync<ErrorResponse>();
        error.Should().NotBeNull();
        error!.Code.Should().Be("backup.import.preflight_failed");
    }

    [Fact]
    public async Task Backup_import_should_resolve_relative_archive_path_against_target_workspace()
    {
        using var sourceWorkspace = new TempWorkspaceRoot("kodaclaw-backup-relative-source");
        var modelSecretRef = new SecretRef("memory", "models", "primary");
        var channelSecretRef = new SecretRef("memory", "channels", "telegram-main");
        var webhookSecretRef = new SecretRef("memory", "channels", "webhook-main");
        var pluginEnvironmentSecretRef = new SecretRef("memory", "plugins", "fixture-env");
        var pluginHeaderSecretRef = new SecretRef("memory", "plugins", "fixture-header");

        await using var sourceHosted = await StartHostedGatewayAsync(sourceWorkspace.Path, secretStore: null);
        sourceHosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "backup-token");
        await SeedSourceWorkspaceAsync(
            sourceHosted.Services,
            sourceWorkspace.Path,
            modelSecretRef,
            channelSecretRef,
            webhookSecretRef,
            pluginEnvironmentSecretRef,
            pluginHeaderSecretRef);

        var exportResponse = await sourceHosted.Client.PostAsJsonAsync("/api/system/backup-export", new BackupExportRequest());
        exportResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var exportPayload = await exportResponse.Content.ReadFromJsonAsync<BackupExportResponse>();
        exportPayload.Should().NotBeNull();

        using var targetWorkspace = new TempWorkspaceRoot("kodaclaw-backup-relative-target");
        var relativeArchivePath = Path.Combine(KodaClawWorkspaceLayout.ConfigDirectory, "backups", "manual-smoke.zip");
        var expectedArchivePath = Path.Combine(targetWorkspace.Path, relativeArchivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(expectedArchivePath)!);
        File.Copy(exportPayload!.ArchivePath, expectedArchivePath);

        var targetSecretStore = new FakeSecretStore(new Dictionary<string, string?>
        {
            [modelSecretRef.ToReferenceString()] = "model-secret",
        });
        await using var targetHosted = await StartHostedGatewayAsync(targetWorkspace.Path, targetSecretStore);
        targetHosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "backup-token");

        var preflightResponse = await targetHosted.Client.PostAsJsonAsync(
            "/api/system/backup-import/preflight",
            new BackupImportPreflightRequest(relativeArchivePath));
        preflightResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preflightPayload = await preflightResponse.Content.ReadFromJsonAsync<BackupImportPreflightResponse>();
        preflightPayload.Should().NotBeNull();
        preflightPayload!.ArchivePath.Should().Be(expectedArchivePath);
        preflightPayload.CanImport.Should().BeTrue();

        var importResponse = await targetHosted.Client.PostAsJsonAsync(
            "/api/system/backup-import",
            new BackupImportRequest(relativeArchivePath));
        importResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var importPayload = await importResponse.Content.ReadFromJsonAsync<BackupImportResponse>();
        importPayload.Should().NotBeNull();
        importPayload!.ArchivePath.Should().Be(expectedArchivePath);
        File.Exists(importPayload.RepairReportPath).Should().BeTrue();
    }

    [Fact]
    public async Task Backup_export_should_reject_archive_path_outside_workspace()
    {
        using var workspace = new TempWorkspaceRoot("kodaclaw-backup-export-path");
        await using var hosted = await StartHostedGatewayAsync(workspace.Path, secretStore: null);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "backup-token");

        var outsideArchivePath = Path.Combine(Path.GetTempPath(), $"kodaclaw-outside-{Guid.NewGuid():N}.zip");
        var response = await hosted.Client.PostAsJsonAsync(
            "/api/system/backup-export",
            new BackupExportRequest(outsideArchivePath));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        payload.Should().NotBeNull();
        payload!.Code.Should().Be("validation.archive_path_invalid");
    }

    [Fact]
    public async Task Backup_import_preflight_should_reject_archive_without_manifest()
    {
        using var workspace = new TempWorkspaceRoot("kodaclaw-backup-missing-manifest");
        using var archiveRoot = new TempWorkspaceRoot("kodaclaw-backup-archive");
        var archivePath = Path.Combine(archiveRoot.Path, "missing-manifest.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var appConfig = archive.CreateEntry("config/app.json");
            await using var stream = appConfig.Open();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync("{}");
        }

        await using var hosted = await StartHostedGatewayAsync(workspace.Path, new FakeSecretStore(new Dictionary<string, string?>()));
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "backup-token");

        var response = await hosted.Client.PostAsJsonAsync(
            "/api/system/backup-import/preflight",
            new BackupImportPreflightRequest(archivePath));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        payload.Should().NotBeNull();
        payload!.Code.Should().Be("backup.import.invalid_archive");
    }

    [Fact]
    public async Task Backup_import_preflight_should_reject_archive_with_suspicious_entry()
    {
        using var workspace = new TempWorkspaceRoot("kodaclaw-backup-unsafe-entry");
        using var archiveRoot = new TempWorkspaceRoot("kodaclaw-backup-archive");
        var archivePath = Path.Combine(archiveRoot.Path, "unsafe-entry.zip");
        var manifest = new BackupManifest(
            Product: "KodaClaw",
            FormatVersion: 1,
            WorkspaceVersion: KodaClawWorkspaceLayout.CurrentWorkspaceVersion,
            GeneratedAt: new DateTimeOffset(2026, 3, 19, 16, 0, 0, TimeSpan.Zero),
            ArchiveName: Path.GetFileName(archivePath),
            SourceWorkspaceRoot: archiveRoot.Path,
            SourceDevice: null,
            Entries:
            [
                new BackupManifestEntry(
                    Path: "config/app.json",
                    Sha256: "deadbeef",
                    SizeBytes: 2,
                    Category: "config"),
            ],
            Includes: [],
            Excludes: []);
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var manifestEntry = archive.CreateEntry(KodaClawWorkspaceLayout.BackupManifestFile);
            await using (var stream = manifestEntry.Open())
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions);
            }

            var safeEntry = archive.CreateEntry("config/app.json");
            await using (var stream = safeEntry.Open())
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync("{}");
            }

            var unsafeEntry = archive.CreateEntry("../outside.txt");
            await using (var stream = unsafeEntry.Open())
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync("unsafe");
            }
        }

        await using var hosted = await StartHostedGatewayAsync(workspace.Path, new FakeSecretStore(new Dictionary<string, string?>()));
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "backup-token");

        var response = await hosted.Client.PostAsJsonAsync(
            "/api/system/backup-import/preflight",
            new BackupImportPreflightRequest(archivePath));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        payload.Should().NotBeNull();
        payload!.Code.Should().Be("backup.import.invalid_archive");
    }

    [Fact]
    public async Task Backup_import_preflight_should_reject_archive_with_case_colliding_entries()
    {
        using var workspace = new TempWorkspaceRoot("kodaclaw-backup-case-collision");
        using var archiveRoot = new TempWorkspaceRoot("kodaclaw-backup-archive");
        var archivePath = Path.Combine(archiveRoot.Path, "case-collision.zip");
        var manifest = new BackupManifest(
            Product: "KodaClaw",
            FormatVersion: 1,
            WorkspaceVersion: KodaClawWorkspaceLayout.CurrentWorkspaceVersion,
            GeneratedAt: new DateTimeOffset(2026, 3, 19, 16, 30, 0, TimeSpan.Zero),
            ArchiveName: Path.GetFileName(archivePath),
            SourceWorkspaceRoot: archiveRoot.Path,
            SourceDevice: null,
            Entries:
            [
                new BackupManifestEntry(
                    Path: "config/App.json",
                    Sha256: "deadbeef",
                    SizeBytes: 2,
                    Category: "config"),
                new BackupManifestEntry(
                    Path: "config/app.json",
                    Sha256: "feedface",
                    SizeBytes: 2,
                    Category: "config"),
            ],
            Includes: [],
            Excludes: []);
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var manifestEntry = archive.CreateEntry(KodaClawWorkspaceLayout.BackupManifestFile);
            await using (var stream = manifestEntry.Open())
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions);
            }

            var upperEntry = archive.CreateEntry("config/App.json");
            await using (var stream = upperEntry.Open())
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync("{}");
            }

            var lowerEntry = archive.CreateEntry("config/app.json");
            await using (var stream = lowerEntry.Open())
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync("[]");
            }
        }

        await using var hosted = await StartHostedGatewayAsync(workspace.Path, new FakeSecretStore(new Dictionary<string, string?>()));
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "backup-token");

        var response = await hosted.Client.PostAsJsonAsync(
            "/api/system/backup-import/preflight",
            new BackupImportPreflightRequest(archivePath));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        payload.Should().NotBeNull();
        payload!.Code.Should().Be("backup.import.invalid_archive");
    }

    private static async Task<HostedGateway> StartHostedGatewayAsync(string workspaceRoot, ISecretStore? secretStore)
    {
        return await HostedGateway.StartAsync(
            gatewayToken: "backup-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false,
                rootPath: workspaceRoot),
            configureServices: services =>
            {
                if (secretStore is not null)
                {
                    services.AddSingleton<ISecretStore>(secretStore);
                }
                // Replace with no-op to prevent HeartbeatFileWatcherHostedService from
                // creating automations that make the workspace non-pristine for import checks.
                services.Replace(ServiceDescriptor.Singleton<IHeartbeatSyncService>(
                    _ => new NoOpHeartbeatSyncService()));
            },
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                });
            },
            useTestWorkspaceService: false);
    }

    private sealed class NoOpHeartbeatSyncService : IHeartbeatSyncService
    {
        public Task<HeartbeatSyncResult> SyncAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new HeartbeatSyncResult(0, 0, false));
    }

    private static async Task SeedSourceWorkspaceAsync(
        IServiceProvider services,
        string workspaceRoot,
        SecretRef modelSecretRef,
        SecretRef channelSecretRef,
        SecretRef webhookSecretRef,
        SecretRef pluginEnvironmentSecretRef,
        SecretRef pluginHeaderSecretRef)
    {
        var workspaceService = services.GetRequiredService<IWorkspaceService>();
        await workspaceService.EnsureInitializedAsync();
        await workspaceService.SaveAppConfigAsync(new WorkspaceAppConfig
        {
            WorkspaceVersion = KodaClawWorkspaceLayout.CurrentWorkspaceVersion,
            BootstrapCompleted = true,
            ActiveMainSessionId = "main-001",
        });

        var settingsRepository = services.GetRequiredService<ISettingsRepository>();
        var settingsUpdatedAt = new DateTimeOffset(2026, 3, 19, 10, 0, 0, TimeSpan.Zero);
        await settingsRepository.SaveAsync(new KodaClawSettings(
            DefaultLandingRoute: "/inbox",
            Theme: ThemeMode.Dark,
            RequireApprovalForExternalActions: true,
            NotificationsEnabled: true,
            QuietHoursEnabled: true,
            QuietHoursStartLocalTime: "22:00",
            QuietHoursEndLocalTime: "07:00",
            UpdatedAt: settingsUpdatedAt));

        var modelRepository = services.GetRequiredService<IProviderAccountRepository>();
        var channelAccountRepository = services.GetRequiredService<IChannelAccountRepository>();
        var threadBindingRepository = services.GetRequiredService<IThreadBindingRepository>();
        var pluginRegistryRepository = services.GetRequiredService<IPluginRegistryRepository>();
        var automationDefinitionRepository = services.GetRequiredService<IAutomationDefinitionRepository>();

        var now = new DateTimeOffset(2026, 3, 19, 11, 0, 0, TimeSpan.Zero);
        await modelRepository.AddAccountAsync(new ProviderAccount(
            Id:                        "account-primary",
            DisplayName:               "Primary account",
            ProviderKind:              ModelProviderKind.OpenAICompatible,
            BaseUrl:                   "https://model.example.com",
            ApiKeySecretRef:           modelSecretRef.ToReferenceString(),
            ApiKeyEnvironmentVariable: null,
            AccessMode:                "api",
            Enabled:                   true,
            CreatedAt:                 now,
            UpdatedAt:                 now));
        await modelRepository.AddModelAsync(new AccountModel(
            Id:                  "model-primary",
            AccountId:           "account-primary",
            DisplayName:         "Primary model",
            ModelId:             "o3",
            Capabilities:        ModelCapabilitySet.Text,
            IsDefaultForAccount: true,
            IsGlobalDefault:     true,
            Enabled:             true,
            CreatedAt:           now,
            UpdatedAt:           now));

        await channelAccountRepository.UpsertAsync(new ChannelAccount(
            Id: "telegram-main",
            ConnectorKind: ChannelConnectorKind.Telegram,
            DisplayName: "Telegram Main",
            State: ChannelAccountState.Connected,
            CreatedAt: now,
            UpdatedAt: now,
            CredentialReference: channelSecretRef.ToReferenceString()));
        await channelAccountRepository.UpsertAsync(new ChannelAccount(
            Id: "webhook-main",
            ConnectorKind: ChannelConnectorKind.GenericWebhook,
            DisplayName: "Webhook Main",
            State: ChannelAccountState.Connected,
            CreatedAt: now.AddMinutes(1),
            UpdatedAt: now.AddMinutes(1),
            ConfigurationJson: $"{{\"sharedSecret\":\"inline-webhook-secret\",\"credentialReference\":\"{webhookSecretRef}\",\"defaultThreadType\":\"Group\"}}"));

        await threadBindingRepository.UpsertAsync(new ThreadBinding(
            Id: "binding-group-001",
            ConnectorKind: ChannelConnectorKind.GenericWebhook,
            AccountId: "webhook-main",
            ExternalThreadId: "thread-001",
            ThreadType: ChannelThreadType.Group,
            SessionId: "channel-group-001",
            SessionKind: SessionKind.ChannelGroup,
            ChannelIdentity: new ChannelIdentity(
                Id: "user-001",
                Username: "ops",
                DisplayName: "Ops Team"),
            PolicyId: "policy-default",
            DeliveryRuleId: "delivery-default",
            CreatedAt: now,
            UpdatedAt: now,
            LastInboundAt: now,
            LastOutboundAt: now,
            LastMessagePreview: "sensitive preview should be removed"));

        var pluginRoot = Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.WorkspaceDirectory, "plugins", "plugin.fixture.backup");
        Directory.CreateDirectory(pluginRoot);
        await pluginRegistryRepository.UpsertAsync(new PluginRecord(
            Id: "plugin.fixture.backup",
            Manifest: new PluginManifest(
                Id: "plugin.fixture.backup",
                Name: "Fixture Backup Plugin",
                Version: "0.1.0",
                Types: [PluginType.Tool],
                Runtime: new PluginRuntimeSpec(
                    Transport: PluginTransportKind.Stdio,
                    Command: "dotnet",
                    Args: ["fixture.dll"],
                    Environment: new Dictionary<string, string>
                    {
                        ["PLUGIN_TOKEN"] = "inline-plugin-secret",
                    },
                    Headers: new Dictionary<string, string>
                    {
                        ["Authorization"] = "Bearer inline-plugin-header",
                    },
                    EnvironmentReferences: new Dictionary<string, string>
                    {
                        ["PLUGIN_TOKEN"] = pluginEnvironmentSecretRef.ToReferenceString(),
                    },
                    HeaderReferences: new Dictionary<string, string>
                    {
                        ["X-Plugin-Key"] = pluginHeaderSecretRef.ToReferenceString(),
                    }),
                Permissions: new PluginPermissionSet(Network: true),
                Capabilities: new PluginCapabilitySet(Tools: ["echo"])),
            InstallSource: PluginInstallSource.LocalDirectory,
            RootPath: pluginRoot,
            TrustState: PluginTrustState.Trusted,
            Enabled: true,
            RuntimeState: PluginRuntimeState.Running,
            DiscoveredAt: now,
            InstalledAt: now,
            UpdatedAt: now));

        var heartbeatPath = Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.HeartbeatFile);
        await automationDefinitionRepository.UpsertAsync(new AutomationDefinition(
            Id: "heartbeat-daily",
            Title: "Daily heartbeat",
            Prompt: "Summarize operator heartbeat.",
            Source: AutomationDefinitionSource.Manual,
            SourcePath: heartbeatPath,
            CronExpression: "0 * * * *",
            Enabled: true,
            InputPaths: [heartbeatPath],
            ModelId: null,
            NotificationChannels: null,
            NotifyMode: AutomationNotifyMode.None,
            CreatedAt: now,
            UpdatedAt: now,
            LastRunAt: null,
            NextRunAt: now.AddDays(1),
            LastRunStatus: null,
            LastError: null));

        var sessionDirectory = workspaceService.GetSessionDirectory("main-001");
        Directory.CreateDirectory(sessionDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(sessionDirectory, "meta.json"),
            "{\"agentId\":\"main-001\",\"metadata\":{\"toolIds\":[\"mcp__plugin.fixture.backup__echo\"]}}");
    }

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot(string prefix)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                prefix,
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
