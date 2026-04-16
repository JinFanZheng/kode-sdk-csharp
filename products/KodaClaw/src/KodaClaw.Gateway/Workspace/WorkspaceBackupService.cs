using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using KodaClaw.Automation;
using KodaClaw.ChannelHub;
using KodaClaw.Contracts;
using KodaClaw.ControlPlane;
using KodaClaw.ModelHub;
using KodaClaw.Storage.Json.Repositories;

namespace KodaClaw.Gateway;

internal sealed class WorkspaceBackupService
{
    private const string ProductName = "KodaClaw";
    private const int BackupFormatVersion = 1;
    private const int RepositorySafetyLimit = 200;
    private const int MaxImportArchiveEntries = 4096;
    private const long MaxImportArchiveBytes = 256L * 1024 * 1024;
    private const string WorkspaceRootPlaceholder = "__KODACLAW_WORKSPACE_ROOT__";
    private const string BackupArchivePrefix = "kodaclaw-backup-";
    private const string BackupScope = "backupImport";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly string[] WorkspaceProtocolFiles =
    [
        Path.Combine(KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.AgentsFile),
        Path.Combine(KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.IdentityFile),
        Path.Combine(KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.SoulFile),
        Path.Combine(KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.UserFile),
        Path.Combine(KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.MemoryFile),
        Path.Combine(KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.HeartbeatFile),
        Path.Combine(KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.BootstrapFile),
        Path.Combine(KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.ToolsFile),
        Path.Combine(KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.McpConfigFile),
        Path.Combine(KodaClawWorkspaceLayout.WorkspaceDirectory, "canvas", "index.html"),
        Path.Combine(KodaClawWorkspaceLayout.WorkspaceDirectory, "canvas", "state.json"),
    ];
    private static readonly string[] IncludeLabels =
    [
        "config/app.json",
        "config/control-plane.db (sanitized)",
        "config/secret-migration-report.json (optional)",
        "config/startup-repair-report.json (optional)",
        "config/update-state.json (optional)",
        "identity/device.json",
        "identity/profile.json",
        "workspace protocol files",
        "sessions/*/meta.json",
    ];
    private static readonly string[] ExcludeLabels =
    [
        "raw secrets and secret values",
        "cache/ and logs/ directories",
        "messages.json and tool-calls.json session payloads",
        "plugin binaries and workspace/plugins runtime directories",
        "approval/inbox payload bodies and automation run summaries",
        "temporary build or download artifacts",
    ];

    private readonly IWorkspaceService _workspaceService;
    private readonly ISecretStore _secretStore;
    private readonly ISettingsRepository _settingsRepository;
    private readonly IProviderAccountRepository _providerAccountRepository;
    private readonly IChannelAccountRepository _channelAccountRepository;
    private readonly IThreadBindingRepository _threadBindingRepository;
    private readonly IPluginRegistryRepository _pluginRegistryRepository;
    private readonly IAutomationDefinitionRepository _automationDefinitionRepository;

    public WorkspaceBackupService(
        IWorkspaceService workspaceService,
        ISecretStore secretStore,
        ISettingsRepository settingsRepository,
        IProviderAccountRepository providerAccountRepository,
        IChannelAccountRepository channelAccountRepository,
        IThreadBindingRepository threadBindingRepository,
        IPluginRegistryRepository pluginRegistryRepository,
        IAutomationDefinitionRepository automationDefinitionRepository)
    {
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _settingsRepository = settingsRepository ?? throw new ArgumentNullException(nameof(settingsRepository));
        _providerAccountRepository = providerAccountRepository ?? throw new ArgumentNullException(nameof(providerAccountRepository));
        _channelAccountRepository = channelAccountRepository ?? throw new ArgumentNullException(nameof(channelAccountRepository));
        _threadBindingRepository = threadBindingRepository ?? throw new ArgumentNullException(nameof(threadBindingRepository));
        _pluginRegistryRepository = pluginRegistryRepository ?? throw new ArgumentNullException(nameof(pluginRegistryRepository));
        _automationDefinitionRepository = automationDefinitionRepository ?? throw new ArgumentNullException(nameof(automationDefinitionRepository));
    }

    public async Task<BackupExportResponse> ExportAsync(
        BackupExportRequest? request,
        CancellationToken cancellationToken = default)
    {
        request ??= new BackupExportRequest();

        var snapshot = await _workspaceService.EnsureInitializedAsync(cancellationToken);
        var appConfig = await _workspaceService.LoadAppConfigAsync(cancellationToken);
        var stagingRoot = CreateTemporaryDirectory("kodaclaw-backup-stage");

        try
        {
            CreateWorkspaceDirectories(stagingRoot);

            await WriteJsonAsync(
                Path.Combine(stagingRoot, KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.AppConfigFile),
                appConfig,
                cancellationToken);
            await CopyOptionalRelativeFileAsync(snapshot.RootPath, stagingRoot, Path.Combine(KodaClawWorkspaceLayout.IdentityDirectory, KodaClawWorkspaceLayout.DeviceIdentityFile), cancellationToken);
            await CopyOptionalRelativeFileAsync(snapshot.RootPath, stagingRoot, Path.Combine(KodaClawWorkspaceLayout.IdentityDirectory, KodaClawWorkspaceLayout.ProfileFile), cancellationToken);
            await CopyOptionalRelativeFileAsync(snapshot.RootPath, stagingRoot, Path.Combine(KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.SecretMigrationReportFile), cancellationToken);
            await CopyOptionalRelativeFileAsync(snapshot.RootPath, stagingRoot, Path.Combine(KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.StartupRepairReportFile), cancellationToken);
            await CopyOptionalRelativeFileAsync(snapshot.RootPath, stagingRoot, Path.Combine(KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.UpdateStateFile), cancellationToken);
            await CopyOptionalRelativeFileAsync(snapshot.RootPath, stagingRoot, Path.Combine(KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.ImportRepairReportFile), cancellationToken);
            await CopyWorkspaceProtocolFilesAsync(snapshot.RootPath, stagingRoot, cancellationToken);
            await CopySessionMetadataAsync(snapshot.RootPath, stagingRoot, cancellationToken);
            await BuildSanitizedControlPlaneAsync(snapshot.RootPath, stagingRoot, cancellationToken);

            var archivePath = ResolveArchivePath(request.ArchivePath, snapshot.RootPath);
            var manifest = await BuildManifestAsync(stagingRoot, archivePath, snapshot, cancellationToken);
            await WriteJsonAsync(
                Path.Combine(stagingRoot, KodaClawWorkspaceLayout.BackupManifestFile),
                manifest,
                cancellationToken);

            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            ZipFile.CreateFromDirectory(stagingRoot, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);

            return new BackupExportResponse(
                GeneratedAt: manifest.GeneratedAt,
                WorkspaceRootPath: snapshot.RootPath,
                ArchivePath: archivePath,
                Manifest: manifest);
        }
        finally
        {
            DeleteDirectoryIfExists(stagingRoot);
        }
    }

    public async Task<BackupImportPreflightResponse> PreflightImportAsync(
        BackupImportPreflightRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = await _workspaceService.EnsureInitializedAsync(cancellationToken);
        var extractedRoot = CreateTemporaryDirectory("kodaclaw-backup-import");

        try
        {
            var archiveInspection = await InspectArchiveAsync(request.ArchivePath, snapshot.RootPath, cancellationToken);
            ExtractArchiveAsync(archiveInspection.ArchivePath, extractedRoot);
            var evaluation = await EvaluateExtractedBackupAsync(extractedRoot, archiveInspection.Manifest, snapshot, cancellationToken);
            return new BackupImportPreflightResponse(
                EvaluatedAt: evaluation.Checklist.GeneratedAt,
                WorkspaceRootPath: snapshot.RootPath,
                ArchivePath: archiveInspection.ArchivePath,
                Manifest: archiveInspection.Manifest,
                ChecksumVerified: evaluation.ChecksumVerified,
                CanImport: evaluation.CanImport,
                Checklist: evaluation.Checklist);
        }
        finally
        {
            DeleteDirectoryIfExists(extractedRoot);
        }
    }

    public async Task<BackupImportResponse> ImportAsync(
        BackupImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = await _workspaceService.EnsureInitializedAsync(cancellationToken);
        var extractedRoot = CreateTemporaryDirectory("kodaclaw-backup-restore");

        try
        {
            var archiveInspection = await InspectArchiveAsync(request.ArchivePath, snapshot.RootPath, cancellationToken);
            ExtractArchiveAsync(archiveInspection.ArchivePath, extractedRoot);
            var evaluation = await EvaluateExtractedBackupAsync(extractedRoot, archiveInspection.Manifest, snapshot, cancellationToken);
            if (!evaluation.CanImport)
            {
                throw new InvalidOperationException("Backup import preflight failed. Resolve blocking repair items before retrying the restore.");
            }

            var importTimestamp = DateTimeOffset.UtcNow;
            await PrepareExtractedControlPlaneForImportAsync(extractedRoot, snapshot.RootPath, importTimestamp, cancellationToken);

            var restoredPaths = new List<string>();
            var skippedPaths = new List<string>();
            await RestoreWorkspaceFilesAsync(extractedRoot, snapshot.RootPath, restoredPaths, skippedPaths, cancellationToken);
            await RestoreControlPlaneJsonStoreAsync(extractedRoot, snapshot.RootPath, restoredPaths, cancellationToken);

            var importedAppConfigPath = Path.Combine(extractedRoot, KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.AppConfigFile);
            if (File.Exists(importedAppConfigPath))
            {
                var importedAppConfig = await ReadJsonAsync<WorkspaceAppConfig>(importedAppConfigPath, cancellationToken) ?? new WorkspaceAppConfig();
                await WriteJsonAsync(
                    Path.Combine(snapshot.RootPath, KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.AppConfigFile),
                    importedAppConfig with { ActiveMainSessionId = null },
                    cancellationToken);
                restoredPaths.Add(ToDisplayPath(snapshot.RootPath, Path.Combine(KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.AppConfigFile)));
            }

            var repairReportPath = Path.Combine(
                snapshot.RootPath,
                KodaClawWorkspaceLayout.ConfigDirectory,
                KodaClawWorkspaceLayout.ImportRepairReportFile);
            var importRepairReport = new ImportRepairReportResponse(
                GeneratedAt: importTimestamp,
                WorkspaceRootPath: snapshot.RootPath,
                ReportPath: repairReportPath,
                Checklist: evaluation.Checklist);
            await WriteJsonAsync(repairReportPath, importRepairReport, cancellationToken);
            restoredPaths.Add(ToDisplayPath(snapshot.RootPath, Path.Combine(KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.ImportRepairReportFile)));

            return new BackupImportResponse(
                ImportedAt: importTimestamp,
                WorkspaceRootPath: snapshot.RootPath,
                ArchivePath: archiveInspection.ArchivePath,
                RepairReportPath: repairReportPath,
                Manifest: archiveInspection.Manifest,
                Checklist: evaluation.Checklist,
                RestoredPaths: restoredPaths.Distinct(StringComparer.Ordinal).OrderBy(static path => path, StringComparer.Ordinal).ToArray(),
                SkippedPaths: skippedPaths.Distinct(StringComparer.Ordinal).OrderBy(static path => path, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            DeleteDirectoryIfExists(extractedRoot);
        }
    }

    private async Task BuildSanitizedControlPlaneAsync(
        string sourceWorkspaceRoot,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        await EnsureTableWithinLimitAsync(sourceWorkspaceRoot, "channel_accounts", "channel accounts", cancellationToken);
        await EnsureTableWithinLimitAsync(sourceWorkspaceRoot, "thread_bindings", "channel thread bindings", cancellationToken);
        await EnsureTableWithinLimitAsync(sourceWorkspaceRoot, "plugins", "plugins", cancellationToken);
        await EnsureTableWithinLimitAsync(sourceWorkspaceRoot, "automation_definitions", "automation definitions", cancellationToken);

        var stagingSettingsRepository = new JsonSettingsRepository(stagingRoot);
        var stagingModelRepository = new JsonProviderAccountRepository(stagingRoot);
        var stagingChannelAccountRepository = new JsonChannelAccountRepository(stagingRoot);
        var stagingThreadBindingRepository = new JsonThreadBindingRepository(stagingRoot);
        var stagingPluginRepository = new JsonPluginRegistryRepository(stagingRoot);
        var stagingAutomationDefinitionRepository = new JsonAutomationDefinitionRepository(stagingRoot);

        var settings = await _settingsRepository.GetAsync(cancellationToken);
        await stagingSettingsRepository.SaveAsync(settings, cancellationToken);

        var providerAccounts = await _providerAccountRepository.ListAccountsAsync(cancellationToken);
        foreach (var account in providerAccounts)
        {
            await stagingModelRepository.AddAccountAsync(account, cancellationToken);
        }
        var accountModels = await _providerAccountRepository.ListAllModelsAsync(cancellationToken);
        foreach (var model in accountModels)
        {
            await stagingModelRepository.AddModelAsync(model, cancellationToken);
        }

        var accounts = await _channelAccountRepository.ListAsync(
            new ChannelAccountQuery(Limit: RepositorySafetyLimit),
            cancellationToken);
        foreach (var account in accounts)
        {
            await stagingChannelAccountRepository.UpsertAsync(SanitizeChannelAccount(account), cancellationToken);
        }

        var bindings = await _threadBindingRepository.ListAsync(
            new ChannelQuery(Limit: RepositorySafetyLimit),
            cancellationToken);
        foreach (var binding in bindings)
        {
            await stagingThreadBindingRepository.UpsertAsync(binding with { LastMessagePreview = null }, cancellationToken);
        }

        var plugins = await _pluginRegistryRepository.ListAsync(
            new PluginQuery(Limit: RepositorySafetyLimit),
            cancellationToken);
        foreach (var plugin in plugins)
        {
            await stagingPluginRepository.UpsertAsync(SanitizePluginRecordForBackup(plugin), cancellationToken);
        }

        var definitions = await _automationDefinitionRepository.ListAsync(
            new AutomationDefinitionQuery(Limit: RepositorySafetyLimit),
            cancellationToken);
        foreach (var definition in definitions)
        {
            await stagingAutomationDefinitionRepository.UpsertAsync(
                SanitizeAutomationDefinitionForBackup(definition, sourceWorkspaceRoot),
                cancellationToken);
        }
    }

    private static ChannelAccount SanitizeChannelAccount(ChannelAccount account)
    {
        return account with
        {
            ConfigurationJson = SanitizeChannelConfiguration(account.ConfigurationJson),
        };
    }

    private static string? SanitizeChannelConfiguration(string? configurationJson)
    {
        if (string.IsNullOrWhiteSpace(configurationJson))
        {
            return configurationJson;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(configurationJson);
        }
        catch (JsonException)
        {
            return configurationJson;
        }

        if (node is not JsonObject root)
        {
            return configurationJson;
        }

        root.Remove("botToken");
        root.Remove("token");
        root.Remove("sharedSecret");
        root.Remove("secret");
        return root.ToJsonString(JsonOptions);
    }

    private static PluginRecord SanitizePluginRecordForBackup(PluginRecord record)
    {
        var runtime = record.Manifest.Runtime with
        {
            Environment = null,
            Headers = null,
        };

        return record with
        {
            Manifest = record.Manifest with { Runtime = runtime },
            RootPath = BuildPluginRestoreRoot(WorkspaceRootPlaceholder, record.Id),
        };
    }

    private static AutomationDefinition SanitizeAutomationDefinitionForBackup(
        AutomationDefinition definition,
        string sourceWorkspaceRoot)
    {
        return definition with
        {
            SourcePath = NormalizeWorkspaceBoundPath(definition.SourcePath, sourceWorkspaceRoot),
            InputPaths = definition.InputPaths is null
                ? null
                : definition.InputPaths.Select(path => NormalizeWorkspaceBoundPath(path, sourceWorkspaceRoot) ?? path).ToArray(),
        };
    }

    private static string BuildPluginRestoreRoot(string workspaceRoot, string pluginId)
    {
        return Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.WorkspaceDirectory, "plugins", pluginId);
    }

    private static string? NormalizeWorkspaceBoundPath(string? candidatePath, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return candidatePath;
        }

        if (!Path.IsPathRooted(candidatePath))
        {
            return candidatePath;
        }

        var fullWorkspaceRoot = NormalizePath(workspaceRoot);
        var fullCandidate = NormalizePath(candidatePath);
        if (!IsPathInsideRoot(fullWorkspaceRoot, fullCandidate))
        {
            return candidatePath;
        }

        var relativePath = Path.GetRelativePath(fullWorkspaceRoot, fullCandidate);
        return Path.Combine(WorkspaceRootPlaceholder, relativePath);
    }

    private async Task<BackupManifest> BuildManifestAsync(
        string stagingRoot,
        string archivePath,
        WorkspaceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var entries = new List<BackupManifestEntry>();

        foreach (var filePath in Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            var relativePath = ToArchivePath(stagingRoot, filePath);
            if (string.Equals(relativePath, KodaClawWorkspaceLayout.BackupManifestFile, StringComparison.Ordinal))
            {
                continue;
            }

            entries.Add(new BackupManifestEntry(
                Path: relativePath,
                Sha256: await ComputeSha256Async(filePath, cancellationToken),
                SizeBytes: new FileInfo(filePath).Length,
                Category: CategorizeArchivePath(relativePath)));
        }

        return new BackupManifest(
            Product: ProductName,
            FormatVersion: BackupFormatVersion,
            WorkspaceVersion: snapshot.WorkspaceVersion,
            GeneratedAt: generatedAt,
            ArchiveName: Path.GetFileName(archivePath),
            SourceWorkspaceRoot: snapshot.RootPath,
            SourceDevice: new BackupDeviceIdentitySnapshot(
                snapshot.DeviceId,
                snapshot.DeviceFingerprintHash,
                snapshot.DeviceAppVersion,
                snapshot.DeviceLastSeenAtUtc),
            Entries: entries,
            Includes: IncludeLabels,
            Excludes: ExcludeLabels);
    }

    private static string ResolveArchivePath(string? requestedArchivePath, string workspaceRoot)
    {
        var fullWorkspaceRoot = NormalizePath(workspaceRoot);
        if (!string.IsNullOrWhiteSpace(requestedArchivePath))
        {
            var candidatePath = requestedArchivePath.Trim();
            var resolvedPath = Path.IsPathRooted(candidatePath)
                ? NormalizePath(candidatePath)
                : NormalizePath(Path.Combine(fullWorkspaceRoot, candidatePath));
            if (!IsPathInsideRoot(fullWorkspaceRoot, resolvedPath))
            {
                throw new ArgumentException(
                    "ArchivePath must resolve inside the workspace root.",
                    nameof(requestedArchivePath));
            }

            return resolvedPath;
        }

        var fileName = BackupArchivePrefix + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + ".zip";
        return Path.Combine(fullWorkspaceRoot, KodaClawWorkspaceLayout.ConfigDirectory, "backups", fileName);
    }

    private static void CreateWorkspaceDirectories(string rootPath)
    {
        Directory.CreateDirectory(Path.Combine(rootPath, KodaClawWorkspaceLayout.ConfigDirectory));
        Directory.CreateDirectory(Path.Combine(rootPath, KodaClawWorkspaceLayout.IdentityDirectory));
        Directory.CreateDirectory(Path.Combine(rootPath, KodaClawWorkspaceLayout.WorkspaceDirectory));
        Directory.CreateDirectory(Path.Combine(rootPath, KodaClawWorkspaceLayout.SessionsDirectory));
    }

    private static async Task CopyOptionalRelativeFileAsync(
        string sourceRoot,
        string destinationRoot,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.Combine(sourceRoot, relativePath);
        if (!File.Exists(sourcePath))
        {
            return;
        }

        var destinationPath = Path.Combine(destinationRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var sourceStream = File.OpenRead(sourcePath);
        await using var destinationStream = File.Create(destinationPath);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken);
    }

    private static async Task CopyWorkspaceProtocolFilesAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        foreach (var relativePath in WorkspaceProtocolFiles)
        {
            await CopyOptionalRelativeFileAsync(sourceRoot, destinationRoot, relativePath, cancellationToken);
        }
    }

    private static async Task CopySessionMetadataAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        var sessionsRoot = Path.Combine(sourceRoot, KodaClawWorkspaceLayout.SessionsDirectory);
        if (!Directory.Exists(sessionsRoot))
        {
            return;
        }

        foreach (var sessionDirectory in Directory.EnumerateDirectories(sessionsRoot).OrderBy(static path => path, StringComparer.Ordinal))
        {
            var metaPath = Path.Combine(sessionDirectory, "meta.json");
            if (!File.Exists(metaPath))
            {
                continue;
            }

            var destinationSessionDirectory = Path.Combine(destinationRoot, KodaClawWorkspaceLayout.SessionsDirectory, Path.GetFileName(sessionDirectory));
            Directory.CreateDirectory(destinationSessionDirectory);
            await using var sourceStream = File.OpenRead(metaPath);
            await using var destinationStream = File.Create(Path.Combine(destinationSessionDirectory, "meta.json"));
            await sourceStream.CopyToAsync(destinationStream, cancellationToken);
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ToArchivePath(string rootPath, string filePath)
    {
        return Path.GetRelativePath(rootPath, filePath).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string CategorizeArchivePath(string relativePath)
    {
        if (relativePath.StartsWith(KodaClawWorkspaceLayout.ConfigDirectory + "/", StringComparison.Ordinal))
        {
            return "config";
        }

        if (relativePath.StartsWith(KodaClawWorkspaceLayout.IdentityDirectory + "/", StringComparison.Ordinal))
        {
            return "identity";
        }

        if (relativePath.StartsWith(KodaClawWorkspaceLayout.WorkspaceDirectory + "/", StringComparison.Ordinal))
        {
            return "workspace";
        }

        if (relativePath.StartsWith(KodaClawWorkspaceLayout.SessionsDirectory + "/", StringComparison.Ordinal))
        {
            return "sessionMeta";
        }

        return "other";
    }

    private static async Task WriteJsonAsync<T>(string filePath, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
    }

    private static async Task<T?> ReadJsonAsync<T>(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return default;
        }

        await using var stream = File.OpenRead(filePath);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };
    }

    private async Task<ArchiveInspectionResult> InspectArchiveAsync(
        string archivePath,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fullArchivePath = ResolveImportArchivePath(archivePath, workspaceRoot);
        if (!File.Exists(fullArchivePath))
        {
            throw new FileNotFoundException("Backup archive was not found.", fullArchivePath);
        }

        using var archive = ZipFile.OpenRead(fullArchivePath);
        var archiveEntries = ValidateArchiveEntries(archive);
        if (!archiveEntries.TryGetValue(KodaClawWorkspaceLayout.BackupManifestFile, out var manifestEntry))
        {
            throw new InvalidDataException("Backup archive is missing backup-manifest.json.");
        }

        await using var manifestStream = manifestEntry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(manifestStream, JsonOptions, cancellationToken);
        if (manifest is null)
        {
            throw new InvalidDataException("Backup archive is missing backup-manifest.json.");
        }

        var manifestEntries = ValidateManifestEntries(manifest);
        var archiveEntryPaths = archiveEntries.Keys
            .Where(static path => !string.Equals(path, KodaClawWorkspaceLayout.BackupManifestFile, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var undeclaredEntryPath = archiveEntryPaths
            .Except(manifestEntries, StringComparer.Ordinal)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .FirstOrDefault();
        if (undeclaredEntryPath is not null)
        {
            throw new InvalidDataException($"Backup archive contains undeclared entry '{undeclaredEntryPath}'.");
        }

        var missingEntryPath = manifestEntries
            .Except(archiveEntryPaths, StringComparer.Ordinal)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .FirstOrDefault();
        if (missingEntryPath is not null)
        {
            throw new InvalidDataException($"Backup manifest declares missing entry '{missingEntryPath}'.");
        }

        return new ArchiveInspectionResult(fullArchivePath, manifest);
    }

    private static string ResolveImportArchivePath(string archivePath, string workspaceRoot)
    {
        var candidatePath = archivePath.Trim();
        if (Path.IsPathRooted(candidatePath))
        {
            return NormalizePath(candidatePath);
        }

        return NormalizePath(Path.Combine(workspaceRoot, candidatePath));
    }

    private static void ExtractArchiveAsync(string archivePath, string destinationRoot)
    {
        ZipFile.ExtractToDirectory(archivePath, destinationRoot, overwriteFiles: true);
    }

    private static Dictionary<string, ZipArchiveEntry> ValidateArchiveEntries(ZipArchive archive)
    {
        if (archive.Entries.Count == 0)
        {
            throw new InvalidDataException("Backup archive is empty.");
        }

        if (archive.Entries.Count > MaxImportArchiveEntries)
        {
            throw new InvalidDataException(
                $"Backup archive contains {archive.Entries.Count} entries, which exceeds the safety limit of {MaxImportArchiveEntries}.");
        }

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        var caseInsensitivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalUncompressedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var normalizedPath = NormalizeArchiveEntryPath(entry.FullName);
            if (string.IsNullOrEmpty(normalizedPath) || entry.Name.Length == 0)
            {
                continue;
            }

            totalUncompressedBytes += entry.Length;
            if (totalUncompressedBytes > MaxImportArchiveBytes)
            {
                throw new InvalidDataException(
                    $"Backup archive expands to {totalUncompressedBytes} bytes, which exceeds the safety limit of {MaxImportArchiveBytes} bytes.");
            }

            if (!entries.TryAdd(normalizedPath, entry))
            {
                throw new InvalidDataException($"Backup archive contains duplicate entry '{normalizedPath}'.");
            }

            if (!caseInsensitivePaths.Add(normalizedPath))
            {
                throw new InvalidDataException(
                    $"Backup archive contains case-colliding entry '{normalizedPath}', which is unsafe on case-insensitive filesystems.");
            }
        }

        return entries;
    }

    private static HashSet<string> ValidateManifestEntries(BackupManifest manifest)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var caseInsensitivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifestEntry in manifest.Entries)
        {
            var normalizedPath = NormalizeArchiveEntryPath(manifestEntry.Path);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                throw new InvalidDataException("Backup manifest contains an empty entry path.");
            }

            if (string.Equals(normalizedPath, KodaClawWorkspaceLayout.BackupManifestFile, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Backup manifest cannot declare backup-manifest.json as a data entry.");
            }

            if (!paths.Add(normalizedPath))
            {
                throw new InvalidDataException($"Backup manifest contains duplicate entry '{normalizedPath}'.");
            }

            if (!caseInsensitivePaths.Add(normalizedPath))
            {
                throw new InvalidDataException(
                    $"Backup manifest contains case-colliding entry '{normalizedPath}', which is unsafe on case-insensitive filesystems.");
            }
        }

        return paths;
    }

    private static string NormalizeArchiveEntryPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return string.Empty;
        }

        var normalized = rawPath.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        if (string.IsNullOrEmpty(normalized))
        {
            return string.Empty;
        }

        if (normalized.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Backup archive contains unsafe entry path '{rawPath}'.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return string.Empty;
        }

        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.Contains(':', StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Backup archive contains unsafe entry path '{rawPath}'.");
            }
        }

        return string.Join('/', segments);
    }

    private async Task<BackupEvaluationResult> EvaluateExtractedBackupAsync(
        string extractedRoot,
        BackupManifest manifest,
        WorkspaceSnapshot currentSnapshot,
        CancellationToken cancellationToken)
    {
        var items = new List<RepairChecklistItem>();
        var checksumVerified = await VerifyChecksumsAsync(extractedRoot, manifest, items, cancellationToken);

        if (!string.Equals(manifest.Product, ProductName, StringComparison.Ordinal))
        {
            items.Add(CreateRepairItem(
                id: "backup-product-mismatch",
                severity: RepairChecklistSeverity.Blocking,
                category: "archive",
                title: "Backup artifact does not belong to KodaClaw",
                summary: $"Expected product '{ProductName}' but found '{manifest.Product}'.",
                action: "Export a KodaClaw backup archive and retry the import."));
        }

        if (manifest.FormatVersion != BackupFormatVersion)
        {
            items.Add(CreateRepairItem(
                id: "backup-format-version",
                severity: RepairChecklistSeverity.Blocking,
                category: "archive",
                title: "Backup format version is unsupported",
                summary: $"This build supports backup format {BackupFormatVersion}, but the archive uses {manifest.FormatVersion}.",
                action: "Use a matching KodaClaw build or regenerate the backup archive."));
        }

        if (manifest.WorkspaceVersion > KodaClawWorkspaceLayout.CurrentWorkspaceVersion)
        {
            items.Add(CreateRepairItem(
                id: "workspace-version-too-new",
                severity: RepairChecklistSeverity.Blocking,
                category: "workspace",
                title: "Backup workspace version is newer than this build",
                summary: $"Archive workspace version {manifest.WorkspaceVersion} is newer than supported version {KodaClawWorkspaceLayout.CurrentWorkspaceVersion}.",
                action: "Upgrade KodaClaw before restoring this archive."));
        }
        else if (manifest.WorkspaceVersion < KodaClawWorkspaceLayout.CurrentWorkspaceVersion)
        {
            items.Add(CreateRepairItem(
                id: "workspace-version-older",
                severity: RepairChecklistSeverity.Warning,
                category: "workspace",
                title: "Backup workspace version is older than the current schema",
                summary: $"Archive workspace version {manifest.WorkspaceVersion} will be restored into workspace version {KodaClawWorkspaceLayout.CurrentWorkspaceVersion}.",
                action: "Review the imported workspace after restore and re-run diagnostics if needed."));
        }

        var state = await LoadBackupStateAsync(extractedRoot, cancellationToken);
        items.AddRange(await BuildPristineWorkspaceItemsAsync(currentSnapshot.RootPath, cancellationToken));
        items.AddRange(BuildDeviceIdentityItems(manifest, currentSnapshot));
        items.AddRange(await BuildMissingSecretItemsAsync(state, cancellationToken));
        items.AddRange(BuildPluginRepairItems(state, currentSnapshot.RootPath));
        items.AddRange(BuildSessionRepairItems(state));
        items.AddRange(BuildAutomationPathItems(state, currentSnapshot.RootPath));

        var checklist = BuildChecklist(items);
        return new BackupEvaluationResult(state, checksumVerified, !items.Any(static item => item.Severity == RepairChecklistSeverity.Blocking), checklist);
    }

    private static RepairChecklist BuildChecklist(IReadOnlyList<RepairChecklistItem> items)
    {
        var blockingCount = items.Count(static item => item.Severity == RepairChecklistSeverity.Blocking);
        var actionRequiredCount = items.Count(static item => item.Severity == RepairChecklistSeverity.ActionRequired);
        var warningCount = items.Count(static item => item.Severity == RepairChecklistSeverity.Warning);
        var infoCount = items.Count(static item => item.Severity == RepairChecklistSeverity.Info);

        return new RepairChecklist(
            GeneratedAt: DateTimeOffset.UtcNow,
            Scope: BackupScope,
            Summary: new RepairChecklistSummary(
                TotalCount: items.Count,
                BlockingCount: blockingCount,
                ActionRequiredCount: actionRequiredCount,
                WarningCount: warningCount,
                InfoCount: infoCount),
            Items: items.OrderByDescending(static item => item.Severity)
                .ThenBy(static item => item.Id, StringComparer.Ordinal)
                .ToArray());
    }

    private static RepairChecklistItem CreateRepairItem(
        string id,
        RepairChecklistSeverity severity,
        string category,
        string title,
        string summary,
        string? resource = null,
        string? action = null,
        string? evidence = null)
    {
        return new RepairChecklistItem(
            Id: id,
            Severity: severity,
            State: RepairChecklistState.Pending,
            Category: category,
            Title: title,
            Summary: summary,
            Resource: resource,
            Action: action,
            Evidence: evidence);
    }

    private static async Task<bool> VerifyChecksumsAsync(
        string extractedRoot,
        BackupManifest manifest,
        ICollection<RepairChecklistItem> items,
        CancellationToken cancellationToken)
    {
        var verified = true;
        foreach (var entry in manifest.Entries)
        {
            var absolutePath = Path.Combine(extractedRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolutePath))
            {
                verified = false;
                items.Add(CreateRepairItem(
                    id: $"checksum-missing:{entry.Path}",
                    severity: RepairChecklistSeverity.Blocking,
                    category: "archive",
                    title: "Backup entry is missing from archive",
                    summary: $"Archive entry '{entry.Path}' is listed in the manifest but was not found after extraction.",
                    resource: entry.Path,
                    action: "Regenerate the backup archive before importing it."));
                continue;
            }

            var actualSha256 = await ComputeSha256Async(absolutePath, cancellationToken);
            if (!string.Equals(actualSha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                verified = false;
                items.Add(CreateRepairItem(
                    id: $"checksum-mismatch:{entry.Path}",
                    severity: RepairChecklistSeverity.Blocking,
                    category: "archive",
                    title: "Backup entry checksum mismatch",
                    summary: $"Archive entry '{entry.Path}' does not match the checksum recorded in the manifest.",
                    resource: entry.Path,
                    action: "Discard the archive and export a fresh backup before retrying.",
                    evidence: $"expected={entry.Sha256}; actual={actualSha256}"));
            }
        }

        return verified;
    }

    private async Task<BackupState> LoadBackupStateAsync(string extractedRoot, CancellationToken cancellationToken)
    {
        await EnsureTableWithinLimitAsync(extractedRoot, "channel_accounts", "channel accounts", cancellationToken);
        await EnsureTableWithinLimitAsync(extractedRoot, "thread_bindings", "channel thread bindings", cancellationToken);
        await EnsureTableWithinLimitAsync(extractedRoot, "plugins", "plugins", cancellationToken);
        await EnsureTableWithinLimitAsync(extractedRoot, "automation_definitions", "automation definitions", cancellationToken);

        var workspace = new StaticWorkspaceService(extractedRoot);
        var appConfig = await workspace.LoadAppConfigAsync(cancellationToken);
        var modelRepository = new JsonProviderAccountRepository(extractedRoot);
        var channelAccountRepository = new JsonChannelAccountRepository(extractedRoot);
        var threadBindingRepository = new JsonThreadBindingRepository(extractedRoot);
        var pluginRepository = new JsonPluginRegistryRepository(extractedRoot);
        var automationDefinitionRepository = new JsonAutomationDefinitionRepository(extractedRoot);

        var sessionIds = Directory.Exists(Path.Combine(extractedRoot, KodaClawWorkspaceLayout.SessionsDirectory))
            ? Directory.EnumerateDirectories(Path.Combine(extractedRoot, KodaClawWorkspaceLayout.SessionsDirectory))
                .Where(static path => File.Exists(Path.Combine(path, "meta.json")))
                .Select(Path.GetFileName)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();

        return new BackupState(
            AppConfig: appConfig,
            ProviderAccounts: await modelRepository.ListAccountsAsync(cancellationToken),
            ChannelAccounts: await channelAccountRepository.ListAsync(new ChannelAccountQuery(Limit: RepositorySafetyLimit), cancellationToken),
            ThreadBindings: await threadBindingRepository.ListAsync(new ChannelQuery(Limit: RepositorySafetyLimit), cancellationToken),
            Plugins: await pluginRepository.ListAsync(new PluginQuery(Limit: RepositorySafetyLimit), cancellationToken),
            AutomationDefinitions: await automationDefinitionRepository.ListAsync(new AutomationDefinitionQuery(Limit: RepositorySafetyLimit), cancellationToken),
            SessionIds: sessionIds);
    }

    private async Task<IReadOnlyList<RepairChecklistItem>> BuildPristineWorkspaceItemsAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var items = new List<RepairChecklistItem>();
        var appConfig = await _workspaceService.LoadAppConfigAsync(cancellationToken);
        var hasSessions = Directory.Exists(Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.SessionsDirectory)) &&
            Directory.EnumerateDirectories(Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.SessionsDirectory))
                .Any(static path => File.Exists(Path.Combine(path, "meta.json")));
        var hasChannelAccounts = (await _channelAccountRepository.ListAsync(new ChannelAccountQuery(Limit: 1), cancellationToken)).Count > 0;
        var hasThreadBindings = (await _threadBindingRepository.ListAsync(new ChannelQuery(Limit: 1), cancellationToken)).Count > 0;
        var hasPlugins = (await _pluginRegistryRepository.ListAsync(new PluginQuery(Limit: 1), cancellationToken)).Count > 0;
        var hasAutomations = (await _automationDefinitionRepository.ListAsync(new AutomationDefinitionQuery(Limit: 1), cancellationToken)).Count > 0;
        var hasModels = (await _providerAccountRepository.ListAccountsAsync(cancellationToken)).Count > 0;
        var hasImportReport = File.Exists(Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.ImportRepairReportFile));
        var hasSecretMigrationReport = File.Exists(Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.SecretMigrationReportFile));

        if (appConfig.BootstrapCompleted ||
            !string.IsNullOrWhiteSpace(appConfig.ActiveMainSessionId) ||
            hasSessions ||
            hasChannelAccounts ||
            hasThreadBindings ||
            hasPlugins ||
            hasAutomations ||
            hasModels ||
            hasImportReport ||
            hasSecretMigrationReport)
        {
            var evidence = string.Join(
                "; ",
                new[]
                {
                    appConfig.BootstrapCompleted ? "bootstrapCompleted=true" : null,
                    !string.IsNullOrWhiteSpace(appConfig.ActiveMainSessionId) ? $"activeMainSessionId={appConfig.ActiveMainSessionId}" : null,
                    hasSessions ? "sessionMetaPresent=true" : null,
                    hasModels ? "modelsPresent=true" : null,
                    hasChannelAccounts ? "channelAccountsPresent=true" : null,
                    hasThreadBindings ? "threadBindingsPresent=true" : null,
                    hasPlugins ? "pluginsPresent=true" : null,
                    hasAutomations ? "automationsPresent=true" : null,
                    hasImportReport ? "importRepairReportPresent=true" : null,
                    hasSecretMigrationReport ? "secretMigrationReportPresent=true" : null,
                }.Where(static value => value is not null)!);

            items.Add(CreateRepairItem(
                id: "workspace-pristine-required",
                severity: RepairChecklistSeverity.Blocking,
                category: "workspace",
                title: "Restore target must be a pristine KodaClaw workspace",
                summary: "Import v1 only restores into a freshly initialized workspace to avoid mixing sanitized backup state with existing operator data.",
                action: "Create a new empty KodaClaw workspace and run the import there.",
                evidence: evidence));
        }

        return items;
    }

    private static IReadOnlyList<RepairChecklistItem> BuildDeviceIdentityItems(
        BackupManifest manifest,
        WorkspaceSnapshot currentSnapshot)
    {
        var items = new List<RepairChecklistItem>();
        if (manifest.SourceDevice is null)
        {
            return items;
        }

        if (!string.IsNullOrWhiteSpace(manifest.SourceDevice.FingerprintHash) &&
            !string.IsNullOrWhiteSpace(currentSnapshot.DeviceFingerprintHash) &&
            !string.Equals(manifest.SourceDevice.FingerprintHash, currentSnapshot.DeviceFingerprintHash, StringComparison.Ordinal))
        {
            items.Add(CreateRepairItem(
                id: "device-fingerprint-mismatch",
                severity: RepairChecklistSeverity.ActionRequired,
                category: "device",
                title: "Backup was captured on a different device fingerprint",
                summary: "The backup archive will restore workspace state, but the current device identity remains local and must not be silently replaced.",
                action: "Revalidate device-scoped secrets and operator trust settings after import.",
                evidence: $"backupFingerprint={manifest.SourceDevice.FingerprintHash}; currentFingerprint={currentSnapshot.DeviceFingerprintHash}"));
        }

        if (!string.IsNullOrWhiteSpace(manifest.SourceDevice.DeviceId) &&
            !string.IsNullOrWhiteSpace(currentSnapshot.DeviceId) &&
            !string.Equals(manifest.SourceDevice.DeviceId, currentSnapshot.DeviceId, StringComparison.Ordinal))
        {
            items.Add(CreateRepairItem(
                id: "device-id-mismatch",
                severity: RepairChecklistSeverity.Warning,
                category: "device",
                title: "Backup device id differs from the current workspace device id",
                summary: "KodaClaw preserves the current workspace device identity during import and records the mismatch for operator review.",
                action: "Confirm the restored workspace should run on this device before resuming channel or plugin activity.",
                evidence: $"backupDeviceId={manifest.SourceDevice.DeviceId}; currentDeviceId={currentSnapshot.DeviceId}"));
        }

        return items;
    }

    private async Task<IReadOnlyList<RepairChecklistItem>> BuildMissingSecretItemsAsync(
        BackupState state,
        CancellationToken cancellationToken)
    {
        var items = new List<RepairChecklistItem>();
        foreach (var candidate in EnumerateSecretReferences(state))
        {
            if (!SecretRef.TryParse(candidate.SecretReference, out var secretRef))
            {
                items.Add(CreateRepairItem(
                    id: $"secret-ref-invalid:{candidate.Id}",
                    severity: RepairChecklistSeverity.ActionRequired,
                    category: "secrets",
                    title: "Imported secret reference is invalid",
                    summary: $"'{candidate.SecretReference}' is not a valid secret reference for {candidate.DisplayName}.",
                    resource: candidate.Resource,
                    action: "Repair the configuration to point at a valid secret reference before resuming runtime activity."));
                continue;
            }

            var value = await _secretStore.GetAsync(secretRef, cancellationToken);
            if (value is null)
            {
                items.Add(CreateRepairItem(
                    id: $"secret-ref-missing:{candidate.Id}",
                    severity: RepairChecklistSeverity.ActionRequired,
                    category: "secrets",
                    title: "Imported secret reference is not available on this device",
                    summary: $"{candidate.DisplayName} expects secret ref '{candidate.SecretReference}', but the secret store does not currently resolve it.",
                    resource: candidate.Resource,
                    action: "Recreate the secret in the local secret store or update the restored descriptor to point at an available secret ref."));
            }
        }

        return items;
    }

    private static IEnumerable<SecretReferenceCandidate> EnumerateSecretReferences(BackupState state)
    {
        foreach (var account in state.ProviderAccounts)
        {
            if (!string.IsNullOrWhiteSpace(account.ApiKeySecretRef))
            {
                yield return new SecretReferenceCandidate(
                    Id: $"account:{account.Id}",
                    DisplayName: $"provider account '{account.DisplayName}'",
                    Resource: $"accounts/{account.Id}",
                    SecretReference: account.ApiKeySecretRef!);
            }
        }

        foreach (var account in state.ChannelAccounts)
        {
            if (!string.IsNullOrWhiteSpace(account.CredentialReference))
            {
                yield return new SecretReferenceCandidate(
                    Id: $"channel:{account.Id}",
                    DisplayName: $"channel account '{account.DisplayName}'",
                    Resource: $"channel_accounts/{account.Id}",
                    SecretReference: account.CredentialReference!);
            }

            if (string.IsNullOrWhiteSpace(account.ConfigurationJson))
            {
                continue;
            }

            JsonNode? configuration;
            try
            {
                configuration = JsonNode.Parse(account.ConfigurationJson);
            }
            catch (JsonException)
            {
                continue;
            }

            var credentialReference = configuration?["credentialReference"]?.GetValue<string?>();
            if (!string.IsNullOrWhiteSpace(credentialReference))
            {
                yield return new SecretReferenceCandidate(
                    Id: $"channel-config:{account.Id}",
                    DisplayName: $"channel account '{account.DisplayName}' configuration",
                    Resource: $"channel_accounts/{account.Id}",
                    SecretReference: credentialReference!);
            }
        }

        foreach (var plugin in state.Plugins)
        {
            var environmentReferences = plugin.Manifest.Runtime.EnvironmentReferences;
            if (environmentReferences is not null)
            {
                foreach (var pair in environmentReferences.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(pair.Value))
                    {
                        yield return new SecretReferenceCandidate(
                            Id: $"plugin-env:{plugin.Id}:{pair.Key}",
                            DisplayName: $"plugin '{plugin.Manifest.Name}' environment '{pair.Key}'",
                            Resource: $"plugins/{plugin.Id}",
                            SecretReference: pair.Value);
                    }
                }
            }

            var headerReferences = plugin.Manifest.Runtime.HeaderReferences;
            if (headerReferences is not null)
            {
                foreach (var pair in headerReferences.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(pair.Value))
                    {
                        yield return new SecretReferenceCandidate(
                            Id: $"plugin-header:{plugin.Id}:{pair.Key}",
                            DisplayName: $"plugin '{plugin.Manifest.Name}' header '{pair.Key}'",
                            Resource: $"plugins/{plugin.Id}",
                            SecretReference: pair.Value);
                    }
                }
            }
        }
    }

    private static IReadOnlyList<RepairChecklistItem> BuildPluginRepairItems(BackupState state, string currentWorkspaceRoot)
    {
        var items = new List<RepairChecklistItem>();
        foreach (var plugin in state.Plugins)
        {
            var restoredRoot = ExpandWorkspaceBoundPath(plugin.RootPath, currentWorkspaceRoot);
            if (Directory.Exists(restoredRoot))
            {
                continue;
            }

            items.Add(CreateRepairItem(
                id: $"plugin-root-missing:{plugin.Id}",
                severity: RepairChecklistSeverity.ActionRequired,
                category: "plugins",
                title: "Plugin runtime root is not bundled in the backup archive",
                summary: $"Plugin '{plugin.Manifest.Name}' needs its runtime files restored or reinstalled before it can be enabled again.",
                resource: $"plugins/{plugin.Id}",
                action: "Reinstall the plugin into workspace/plugins and verify trust before re-enabling it.",
                evidence: $"expectedRoot={restoredRoot}"));
        }

        return items;
    }

    private static IReadOnlyList<RepairChecklistItem> BuildSessionRepairItems(BackupState state)
    {
        var items = new List<RepairChecklistItem>();
        if (state.SessionIds.Count > 0)
        {
            items.Add(CreateRepairItem(
                id: "session-metadata-only",
                severity: RepairChecklistSeverity.Info,
                category: "sessions",
                title: "Backup contains session metadata only",
                summary: "Session meta.json files are included for diagnostics and repair evidence, but message and tool-call payloads stay excluded by default.",
                action: "Expect restored sessions to require fresh runtime bootstrap if you need live history continuity.",
                evidence: $"sessionCount={state.SessionIds.Count}"));
        }

        if (!string.IsNullOrWhiteSpace(state.AppConfig.ActiveMainSessionId))
        {
            items.Add(CreateRepairItem(
                id: "active-main-session-reset",
                severity: RepairChecklistSeverity.ActionRequired,
                category: "sessions",
                title: "Active main session pointer will be cleared on import",
                summary: "KodaClaw resets ActiveMainSessionId during restore because full runtime payloads are not bundled in the backup archive.",
                action: "Start a fresh main session after restore and reattach any required context manually.",
                evidence: $"archivedActiveMainSessionId={state.AppConfig.ActiveMainSessionId}"));
        }

        return items;
    }

    private static IReadOnlyList<RepairChecklistItem> BuildAutomationPathItems(BackupState state, string currentWorkspaceRoot)
    {
        var items = new List<RepairChecklistItem>();
        foreach (var definition in state.AutomationDefinitions)
        {
            if (!string.IsNullOrWhiteSpace(definition.SourcePath) &&
                Path.IsPathRooted(ExpandWorkspaceBoundPath(definition.SourcePath!, currentWorkspaceRoot)) &&
                !File.Exists(ExpandWorkspaceBoundPath(definition.SourcePath!, currentWorkspaceRoot)))
            {
                items.Add(CreateRepairItem(
                    id: $"automation-source-missing:{definition.Id}",
                    severity: RepairChecklistSeverity.Warning,
                    category: "automations",
                    title: "Automation source path is not present on this machine",
                    summary: $"Automation '{definition.Title}' references a source file that does not currently exist after restore.",
                    resource: $"automation_definitions/{definition.Id}",
                    action: "Review the automation source path before enabling or editing the automation.",
                    evidence: $"sourcePath={ExpandWorkspaceBoundPath(definition.SourcePath!, currentWorkspaceRoot)}"));
            }

            if (definition.InputPaths is null)
            {
                continue;
            }

            foreach (var inputPath in definition.InputPaths)
            {
                var resolvedPath = ExpandWorkspaceBoundPath(inputPath, currentWorkspaceRoot);
                if (Path.IsPathRooted(resolvedPath) && !File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
                {
                    items.Add(CreateRepairItem(
                        id: $"automation-input-missing:{definition.Id}:{resolvedPath}",
                        severity: RepairChecklistSeverity.Warning,
                        category: "automations",
                        title: "Automation input path is not present after restore",
                        summary: $"Automation '{definition.Title}' references input path '{resolvedPath}' which is not available in the restored workspace.",
                        resource: $"automation_definitions/{definition.Id}",
                        action: "Repoint the automation input path or recreate the source material before enabling the automation."));
                }
            }
        }

        return items;
    }

    private async Task PrepareExtractedControlPlaneForImportAsync(
        string extractedRoot,
        string targetWorkspaceRoot,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var pluginRepository = new JsonPluginRegistryRepository(extractedRoot);
        var automationDefinitionRepository = new JsonAutomationDefinitionRepository(extractedRoot);

        var plugins = await pluginRepository.ListAsync(new PluginQuery(Limit: RepositorySafetyLimit), cancellationToken);
        foreach (var plugin in plugins)
        {
            var restoredRoot = ExpandWorkspaceBoundPath(plugin.RootPath, targetWorkspaceRoot);
            var rootExists = Directory.Exists(restoredRoot);
            var updated = plugin with
            {
                RootPath = restoredRoot,
                Enabled = plugin.Enabled && rootExists,
                RuntimeState = rootExists ? PluginRuntimeState.Stopped : PluginRuntimeState.Stopped,
                UpdatedAt = updatedAt,
                LastError = rootExists
                    ? plugin.LastError
                    : "Plugin runtime root is not bundled in the backup archive. Reinstall before enabling.",
            };
            await pluginRepository.UpsertAsync(updated, cancellationToken);
        }

        var definitions = await automationDefinitionRepository.ListAsync(new AutomationDefinitionQuery(Limit: RepositorySafetyLimit), cancellationToken);
        foreach (var definition in definitions)
        {
            var updated = definition with
            {
                SourcePath = ExpandWorkspaceBoundPathOrNull(definition.SourcePath, targetWorkspaceRoot),
                InputPaths = definition.InputPaths is null
                    ? null
                    : definition.InputPaths.Select(path => ExpandWorkspaceBoundPath(path, targetWorkspaceRoot)).ToArray(),
            };
            await automationDefinitionRepository.UpsertAsync(updated, cancellationToken);
        }
    }

    private static string? ExpandWorkspaceBoundPathOrNull(string? candidatePath, string workspaceRoot)
    {
        if (candidatePath is null)
        {
            return null;
        }

        return ExpandWorkspaceBoundPath(candidatePath, workspaceRoot);
    }

    private static string ExpandWorkspaceBoundPath(string candidatePath, string workspaceRoot)
    {
        if (!candidatePath.StartsWith(WorkspaceRootPlaceholder, StringComparison.Ordinal))
        {
            return candidatePath;
        }

        var suffix = candidatePath[WorkspaceRootPlaceholder.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.Combine(workspaceRoot, suffix);
    }

    private static async Task RestoreWorkspaceFilesAsync(
        string extractedRoot,
        string targetWorkspaceRoot,
        ICollection<string> restoredPaths,
        ICollection<string> skippedPaths,
        CancellationToken cancellationToken)
    {
        foreach (var relativePath in WorkspaceProtocolFiles)
        {
            await RestoreOptionalRelativeFileAsync(extractedRoot, targetWorkspaceRoot, relativePath, restoredPaths, cancellationToken);
        }

        await RestoreOptionalRelativeFileAsync(
            extractedRoot,
            targetWorkspaceRoot,
            Path.Combine(KodaClawWorkspaceLayout.IdentityDirectory, KodaClawWorkspaceLayout.ProfileFile),
            restoredPaths,
            cancellationToken);
        await RestoreOptionalRelativeFileAsync(
            extractedRoot,
            targetWorkspaceRoot,
            Path.Combine(KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.SecretMigrationReportFile),
            restoredPaths,
            cancellationToken);
        await RestoreOptionalRelativeFileAsync(
            extractedRoot,
            targetWorkspaceRoot,
            Path.Combine(KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.ControlPlaneDatabaseFile),
            restoredPaths,
            cancellationToken);

        var extractedSessionsRoot = Path.Combine(extractedRoot, KodaClawWorkspaceLayout.SessionsDirectory);
        if (!Directory.Exists(extractedSessionsRoot))
        {
            return;
        }

        foreach (var sessionDirectory in Directory.EnumerateDirectories(extractedSessionsRoot).OrderBy(static path => path, StringComparer.Ordinal))
        {
            var metaPath = Path.Combine(sessionDirectory, "meta.json");
            if (!File.Exists(metaPath))
            {
                continue;
            }

            var sessionId = Path.GetFileName(sessionDirectory);
            var targetPath = Path.Combine(targetWorkspaceRoot, KodaClawWorkspaceLayout.SessionsDirectory, sessionId, "meta.json");
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var sourceStream = File.OpenRead(metaPath);
            await using var destinationStream = File.Create(targetPath);
            await sourceStream.CopyToAsync(destinationStream, cancellationToken);
            restoredPaths.Add(ToDisplayPath(targetWorkspaceRoot, Path.Combine(KodaClawWorkspaceLayout.SessionsDirectory, sessionId, "meta.json")));
            skippedPaths.Remove(ToDisplayPath(targetWorkspaceRoot, Path.Combine(KodaClawWorkspaceLayout.SessionsDirectory, sessionId, "messages.json")));
        }

        skippedPaths.Add(Path.Combine(KodaClawWorkspaceLayout.IdentityDirectory, KodaClawWorkspaceLayout.DeviceIdentityFile).Replace(Path.DirectorySeparatorChar, '/'));
    }

    private static async Task RestoreOptionalRelativeFileAsync(
        string extractedRoot,
        string targetWorkspaceRoot,
        string relativePath,
        ICollection<string> restoredPaths,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.Combine(extractedRoot, relativePath);
        if (!File.Exists(sourcePath))
        {
            return;
        }

        var targetPath = Path.Combine(targetWorkspaceRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await using var sourceStream = File.OpenRead(sourcePath);
        await using var destinationStream = File.Create(targetPath);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken);
        restoredPaths.Add(ToDisplayPath(targetWorkspaceRoot, relativePath));
    }

    private static async Task RestoreControlPlaneJsonStoreAsync(
        string extractedRoot,
        string targetWorkspaceRoot,
        ICollection<string> restoredPaths,
        CancellationToken cancellationToken)
    {
        // Single-file stores
        await RestoreOptionalRelativeFileAsync(
            extractedRoot, targetWorkspaceRoot,
            Path.Combine(KodaClawWorkspaceLayout.ConfigDirectory, "settings.json"),
            restoredPaths, cancellationToken);

        // Directory stores
        await RestoreOptionalRelativeDirectoryAsync(
            extractedRoot, targetWorkspaceRoot,
            Path.Combine(KodaClawWorkspaceLayout.ConfigDirectory, "models"),
            restoredPaths, cancellationToken);
        await RestoreOptionalRelativeDirectoryAsync(
            extractedRoot, targetWorkspaceRoot,
            Path.Combine(KodaClawWorkspaceLayout.ConfigDirectory, "plugins"),
            restoredPaths, cancellationToken);
        await RestoreOptionalRelativeDirectoryAsync(
            extractedRoot, targetWorkspaceRoot,
            Path.Combine(".koda", "store", "channels", "accounts"),
            restoredPaths, cancellationToken);
        await RestoreOptionalRelativeDirectoryAsync(
            extractedRoot, targetWorkspaceRoot,
            Path.Combine(".koda", "store", "channels", "bindings"),
            restoredPaths, cancellationToken);
        await RestoreOptionalRelativeDirectoryAsync(
            extractedRoot, targetWorkspaceRoot,
            Path.Combine(".koda", "store", "automations"),
            restoredPaths, cancellationToken);
    }

    private static async Task RestoreOptionalRelativeDirectoryAsync(
        string extractedRoot,
        string targetWorkspaceRoot,
        string relativeDir,
        ICollection<string> restoredPaths,
        CancellationToken cancellationToken)
    {
        var sourceDir = Path.Combine(extractedRoot, relativeDir);
        if (!Directory.Exists(sourceDir))
        {
            return;
        }

        var targetDir = Path.Combine(targetWorkspaceRoot, relativeDir);
        Directory.CreateDirectory(targetDir);

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, sourceFile);
            var targetFile = Path.Combine(targetDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            await using var sourceStream = File.OpenRead(sourceFile);
            await using var destinationStream = File.Create(targetFile);
            await sourceStream.CopyToAsync(destinationStream, cancellationToken);
            restoredPaths.Add(ToDisplayPath(targetWorkspaceRoot, Path.Combine(relativeDir, relativePath)));
        }
    }

    private static string ToDisplayPath(string workspaceRoot, string relativeOrAbsolutePath)
    {
        var absolutePath = Path.IsPathRooted(relativeOrAbsolutePath)
            ? relativeOrAbsolutePath
            : Path.Combine(workspaceRoot, relativeOrAbsolutePath);
        return Path.GetRelativePath(workspaceRoot, absolutePath).Replace(Path.DirectorySeparatorChar, '/');
    }

    private async Task EnsureTableWithinLimitAsync(
        string workspaceRoot,
        string tableName,
        string displayName,
        CancellationToken cancellationToken)
    {
        var count = await GetTableCountAsync(workspaceRoot, tableName, cancellationToken);
        if (count <= RepositorySafetyLimit)
        {
            return;
        }

        throw new InvalidOperationException($"Backup export/import currently supports at most {RepositorySafetyLimit} {displayName}, but found {count}.");
    }

    private static Task<int> GetTableCountAsync(
        string workspaceRoot,
        string tableName,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var dir = tableName switch
        {
            "channel_accounts" => Path.Combine(workspaceRoot, ".koda", "store", "channels", "accounts"),
            "thread_bindings" => Path.Combine(workspaceRoot, ".koda", "store", "channels", "bindings"),
            "plugins" => Path.Combine(workspaceRoot, "config", "plugins"),
            "automation_definitions" => Path.Combine(workspaceRoot, ".koda", "store", "automations"),
            _ => null,
        };

        if (dir is null || !Directory.Exists(dir))
        {
            return Task.FromResult(0);
        }

        var count = Directory.EnumerateFiles(dir, "*.json").Count();
        return Task.FromResult(count);
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsPathInsideRoot(string rootPath, string candidatePath)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(rootPath, candidatePath, comparison))
        {
            return true;
        }

        return candidatePath.StartsWith(rootPath + Path.DirectorySeparatorChar, comparison);
    }

    private static string CreateTemporaryDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record SecretReferenceCandidate(
        string Id,
        string DisplayName,
        string Resource,
        string SecretReference);

    private sealed record BackupState(
        WorkspaceAppConfig AppConfig,
        IReadOnlyList<ProviderAccount> ProviderAccounts,
        IReadOnlyList<ChannelAccount> ChannelAccounts,
        IReadOnlyList<ThreadBinding> ThreadBindings,
        IReadOnlyList<PluginRecord> Plugins,
        IReadOnlyList<AutomationDefinition> AutomationDefinitions,
        IReadOnlyList<string> SessionIds);

    private sealed record BackupEvaluationResult(
        BackupState State,
        bool ChecksumVerified,
        bool CanImport,
        RepairChecklist Checklist);

    private sealed record ArchiveInspectionResult(
        string ArchivePath,
        BackupManifest Manifest);

    private sealed class StaticWorkspaceService : IWorkspaceService
    {
        public StaticWorkspaceService(string rootPath)
        {
            RootPath = Path.GetFullPath(rootPath);
        }

        public string RootPath { get; }

        public Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new WorkspaceSnapshot(
                RootPath: RootPath,
                WorkspaceVersion: KodaClawWorkspaceLayout.CurrentWorkspaceVersion,
                WorkspaceInitialized: true,
                RequiresBootstrap: false,
                ActiveMainSessionId: null,
                DeviceId: null));
        }

        public Task<WorkspaceSnapshot> EnsureInitializedAsync(CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(RootPath);
            Directory.CreateDirectory(Path.Combine(RootPath, KodaClawWorkspaceLayout.ConfigDirectory));
            Directory.CreateDirectory(Path.Combine(RootPath, KodaClawWorkspaceLayout.IdentityDirectory));
            Directory.CreateDirectory(Path.Combine(RootPath, KodaClawWorkspaceLayout.WorkspaceDirectory));
            Directory.CreateDirectory(Path.Combine(RootPath, KodaClawWorkspaceLayout.SessionsDirectory));
            return GetSnapshotAsync(cancellationToken);
        }

        public async Task<WorkspaceAppConfig> LoadAppConfigAsync(CancellationToken cancellationToken = default)
        {
            var path = Path.Combine(RootPath, KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.AppConfigFile);
            return await ReadJsonAsync<WorkspaceAppConfig>(path, cancellationToken) ?? new WorkspaceAppConfig();
        }

        public Task SaveAppConfigAsync(WorkspaceAppConfig appConfig, CancellationToken cancellationToken = default)
        {
            return WriteJsonAsync(Path.Combine(RootPath, KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.AppConfigFile), appConfig, cancellationToken);
        }

        public string GetSessionDirectory(string sessionId)
        {
            return Path.Combine(RootPath, KodaClawWorkspaceLayout.SessionsDirectory, sessionId);
        }

        public IReadOnlyList<string> GetSkillsPaths() => [];

        public Task<WorkspaceMcpConfig> ReadMcpConfigAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceMcpConfig());

        public Task SaveMcpConfigAsync(WorkspaceMcpConfig config, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<GatewayConfig> ReadGatewayConfigAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GatewayConfig());

        public Task SaveGatewayConfigAsync(GatewayConfig config, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        // Backup/restore paths don't participate in git versioning.
        public Task<bool> TryCommitWorkspaceAsync(string message, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
