using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Workspace.Git;

namespace KodaClaw.Workspace;

public sealed class WorkspaceService : IWorkspaceService
{
    private const string DiagnosticSource = "koda.workspace";

    private static readonly string[] RequiredDirectories =
    [
        KodaClawWorkspaceLayout.ConfigDirectory,
        KodaClawWorkspaceLayout.IdentityDirectory,
        KodaClawWorkspaceLayout.WorkspaceDirectory,
        KodaClawWorkspaceLayout.SessionsDirectory,
        KodaClawWorkspaceLayout.LogsDirectory,
        KodaClawWorkspaceLayout.CacheDirectory,
        Path.Combine(KodaClawWorkspaceLayout.WorkspaceDirectory, "memory"),
        Path.Combine(KodaClawWorkspaceLayout.WorkspaceDirectory, "knowledge"),
        Path.Combine(KodaClawWorkspaceLayout.WorkspaceDirectory, "tasks"),
        Path.Combine(KodaClawWorkspaceLayout.WorkspaceDirectory, "inbox"),
        Path.Combine(KodaClawWorkspaceLayout.WorkspaceDirectory, "canvas"),
        Path.Combine(KodaClawWorkspaceLayout.WorkspaceDirectory, "canvas", "artifacts"),
        Path.Combine(KodaClawWorkspaceLayout.WorkspaceDirectory, "channels"),
        Path.Combine(KodaClawWorkspaceLayout.WorkspaceDirectory, "plugins"),
        KodaClawWorkspaceLayout.WorkspaceSkillsDirectory,
        KodaClawWorkspaceLayout.MediaDirectory,
        KodaClawWorkspaceLayout.MemorySessionsDirectory,
        KodaClawWorkspaceLayout.MemorySessionsPendingDirectory,
        KodaClawWorkspaceLayout.MemoryTopicsDirectory,
        KodaClawWorkspaceLayout.MemoryDormantDirectory,
        KodaClawWorkspaceLayout.MemoryArchiveDirectory,
    ];

    private readonly IWorkspaceGitService? _git;

    // 防止多个 HostedService 并发调用 EnsureInitializedAsync 时 Windows 文件锁竞态
    private static readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly IDiagnosticsService? _diagnosticsService;

    public WorkspaceService(
        KodaClawWorkspaceOptions options,
        IWorkspaceGitService? git = null,
        IDiagnosticsService? diagnosticsService = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        RootPath = options.ResolveRootPath();
        _git = git;
        _diagnosticsService = diagnosticsService;
    }

    public string RootPath { get; }

    public async Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var initialized = IsInitialized();
        var appConfig = initialized
            ? await LoadAppConfigAsync(cancellationToken)
            : new WorkspaceAppConfig();
        var deviceIdentity = initialized
            ? await LoadOrUpgradeDeviceIdentityAsync(cancellationToken)
            : null;

        return new WorkspaceSnapshot(
            RootPath,
            appConfig.WorkspaceVersion,
            initialized,
            !appConfig.BootstrapCompleted,
            appConfig.ActiveMainSessionId,
            deviceIdentity?.DeviceId,
            deviceIdentity?.FingerprintHash,
            deviceIdentity?.RotatedAtUtc,
            deviceIdentity?.RotationReason,
            deviceIdentity?.AppVersion,
            deviceIdentity?.LastSeenAtUtc);
    }

    public async Task<WorkspaceSnapshot> EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(RootPath);
    
            foreach (var relativeDirectory in RequiredDirectories)
            {
                Directory.CreateDirectory(GetAbsolutePath(relativeDirectory));
            }
    
            var appConfigPath = GetAbsolutePath(
                KodaClawWorkspaceLayout.ConfigDirectory,
                KodaClawWorkspaceLayout.AppConfigFile);
    
            await WriteJsonIfMissingAsync(
                appConfigPath,
                new WorkspaceAppConfig(),
                cancellationToken);
            var appConfig = await LoadAppConfigAsync(cancellationToken);
            await WriteJsonIfMissingAsync(
                GetAbsolutePath(KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.GatewayConfigFile),
                new GatewayConfig(),
                cancellationToken);
            await WriteTextIfMissingAsync(
                GetAbsolutePath(KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.ModelsConfigFile),
                DefaultWorkspaceTemplates.EmptyObjectJson(),
                cancellationToken);
            await WriteTextIfMissingAsync(
                GetAbsolutePath(KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.PluginsConfigFile),
                DefaultWorkspaceTemplates.EmptyObjectJson(),
                cancellationToken);
            await WriteJsonIfMissingAsync(
                GetAbsolutePath(KodaClawWorkspaceLayout.IdentityDirectory, KodaClawWorkspaceLayout.DeviceIdentityFile),
                CreateDeviceIdentity(),
                cancellationToken);
            await WriteTextIfMissingAsync(
                GetAbsolutePath(KodaClawWorkspaceLayout.IdentityDirectory, KodaClawWorkspaceLayout.ProfileFile),
                DefaultWorkspaceTemplates.EmptyObjectJson(),
                cancellationToken);
    
            await WriteTextIfMissingAsync(
                GetAbsolutePath(KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.AgentsFile),
                DefaultWorkspaceTemplates.Agents(),
                cancellationToken);
            await WriteTextIfMissingAsync(
                GetAbsolutePath(KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.IdentityFile),
                DefaultWorkspaceTemplates.Identity(),
                cancellationToken);
            await WriteTextIfMissingAsync(
                GetAbsolutePath(KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.SoulFile),
                DefaultWorkspaceTemplates.Soul(),
                cancellationToken);
            await WriteTextIfMissingAsync(
                GetAbsolutePath(KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.OntologyFile),
                DefaultWorkspaceTemplates.Ontology(),
                cancellationToken);
            await WriteTextIfMissingAsync(
                GetAbsolutePath(KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.UserFile),
                DefaultWorkspaceTemplates.User(),
                cancellationToken);
            await WriteTextIfMissingAsync(
                GetAbsolutePath(KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.MemoryFile),
                DefaultWorkspaceTemplates.Memory(),
                cancellationToken);
            await WriteTextIfMissingAsync(
                GetAbsolutePath(KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.HeartbeatFile),
                DefaultWorkspaceTemplates.Heartbeat(),
                cancellationToken);
            if (!appConfig.BootstrapCompleted)
            {
                await WriteTextIfMissingAsync(
                    GetAbsolutePath(KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.BootstrapFile),
                    DefaultWorkspaceTemplates.Bootstrap(),
                    cancellationToken);
            }
            await WriteTextIfMissingAsync(
                GetAbsolutePath(KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.ToolsFile),
                DefaultWorkspaceTemplates.Tools(),
                cancellationToken);
            await WriteTextIfMissingAsync(
                GetAbsolutePath(KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.McpConfigFile),
                DefaultWorkspaceTemplates.McpConfig(),
                cancellationToken);
            await WriteTextIfMissingAsync(
                GetAbsolutePath(KodaClawWorkspaceLayout.WorkspaceDirectory, "canvas", "index.html"),
                DefaultWorkspaceTemplates.CanvasIndex(),
                cancellationToken);
            await WriteTextIfMissingAsync(
                GetAbsolutePath(KodaClawWorkspaceLayout.WorkspaceDirectory, "canvas", "state.json"),
                DefaultWorkspaceTemplates.CanvasState(),
                cancellationToken);
    
            if (_git is not null)
                await _git.EnsureGitRepoAsync(cancellationToken);
    
            RecordDiagnostic("workspace.initialized", "debug",
                $"Workspace initialized at {RootPath}.");
    
            return await GetSnapshotAsync(cancellationToken);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<WorkspaceAppConfig> LoadAppConfigAsync(CancellationToken cancellationToken = default)
    {
        var path = GetAbsolutePath(KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.AppConfigFile);
        if (!File.Exists(path))
        {
            return new WorkspaceAppConfig();
        }

        await using var stream = await OpenReadAsync(path, cancellationToken);
        if (stream.Length == 0)
            return new WorkspaceAppConfig();
        var config = await JsonSerializer.DeserializeAsync<WorkspaceAppConfig>(stream, WorkspaceJson.Default, cancellationToken);
        return config ?? new WorkspaceAppConfig();
    }

    public async Task SaveAppConfigAsync(WorkspaceAppConfig appConfig, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(appConfig);
        Directory.CreateDirectory(GetAbsolutePath(KodaClawWorkspaceLayout.ConfigDirectory));
        await WriteJsonAsync(
            GetAbsolutePath(KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.AppConfigFile),
            appConfig,
            cancellationToken);
    }

    public string GetSessionDirectory(string sessionId)
    {
        ValidateSessionId(sessionId);
        return GetAbsolutePath(KodaClawWorkspaceLayout.SessionsDirectory, sessionId);
    }

    public IReadOnlyList<string> GetSkillsPaths()
    {
        // Priority order: Layer 1 (lowest, built-in) → Layer 3 (global shared) → Layer 2 (workspace-specific, highest)
        return
        [
            Path.Combine(AppContext.BaseDirectory, "skills"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills"),
            Path.Combine(RootPath, KodaClawWorkspaceLayout.WorkspaceSkillsDirectory),
        ];
    }

    public async Task<WorkspaceMcpConfig> ReadMcpConfigAsync(CancellationToken cancellationToken = default)
    {
        var path = GetAbsolutePath(KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.McpConfigFile);
        if (!File.Exists(path))
        {
            return new WorkspaceMcpConfig();
        }

        try
        {
            await using var stream = await OpenReadAsync(path, cancellationToken);
            var config = await JsonSerializer.DeserializeAsync<WorkspaceMcpConfig>(stream, WorkspaceJson.Default, cancellationToken);
            return config ?? new WorkspaceMcpConfig();
        }
        catch (JsonException ex)
        {
            RecordDiagnostic("workspace.config.parse_failed", "warning",
                $"Failed to parse mcp.json: {ex.Message}",
                new Dictionary<string, string?> { ["file"] = "mcp.json" });
            return new WorkspaceMcpConfig();
        }
    }

    public Task<bool> TryCommitWorkspaceAsync(string message, CancellationToken cancellationToken = default) =>
        _git is not null
            ? _git.TryCommitAsync(message, cancellationToken)
            : Task.FromResult(false);

    public async Task SaveMcpConfigAsync(WorkspaceMcpConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await WriteJsonAsync(
            GetAbsolutePath(KodaClawWorkspaceLayout.WorkspaceDirectory, KodaClawWorkspaceLayout.McpConfigFile),
            config,
            cancellationToken);
    }

    public async Task<GatewayConfig> ReadGatewayConfigAsync(CancellationToken cancellationToken = default)
    {
        var path = GetAbsolutePath(KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.GatewayConfigFile);
        if (!File.Exists(path))
            return new GatewayConfig();

        try
        {
            await using var stream = await OpenReadAsync(path, cancellationToken);
            if (stream.Length == 0)
                return new GatewayConfig();
            var config = await JsonSerializer.DeserializeAsync<GatewayConfig>(stream, WorkspaceJson.Default, cancellationToken);
            return config ?? new GatewayConfig();
        }
        catch (JsonException ex)
        {
            RecordDiagnostic("workspace.config.parse_failed", "warning",
                $"Failed to parse gateway.json: {ex.Message}",
                new Dictionary<string, string?> { ["file"] = "gateway.json" });
            return new GatewayConfig();
        }
    }

    public async Task SaveGatewayConfigAsync(GatewayConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        Directory.CreateDirectory(GetAbsolutePath(KodaClawWorkspaceLayout.ConfigDirectory));
        await WriteJsonAsync(
            GetAbsolutePath(KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.GatewayConfigFile),
            config,
            cancellationToken);
    }

    private bool IsInitialized()
    {
        var deviceIdentityPath = GetAbsolutePath(KodaClawWorkspaceLayout.IdentityDirectory, KodaClawWorkspaceLayout.DeviceIdentityFile);
        var appConfigPath = GetAbsolutePath(KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.AppConfigFile);
        return Directory.Exists(RootPath)
            && File.Exists(appConfigPath)
            && new FileInfo(appConfigPath).Length > 0
            && File.Exists(deviceIdentityPath)
            && new FileInfo(deviceIdentityPath).Length > 0;
    }

    private async Task<DeviceIdentity?> LoadOrUpgradeDeviceIdentityAsync(CancellationToken cancellationToken)
    {
        var path = GetAbsolutePath(KodaClawWorkspaceLayout.IdentityDirectory, KodaClawWorkspaceLayout.DeviceIdentityFile);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return null;
        }

        DeviceIdentity? deviceIdentity;
        try
        {
            await using var stream = await OpenReadAsync(path, cancellationToken);
            deviceIdentity = await JsonSerializer.DeserializeAsync<DeviceIdentity>(stream, WorkspaceJson.Default, cancellationToken);
        }
        catch (JsonException ex)
        {
            RecordDiagnostic("workspace.config.parse_failed", "warning",
                $"Failed to parse device identity: {ex.Message}",
                new Dictionary<string, string?> { ["file"] = "device-identity.json" });
            return null;
        }

        if (deviceIdentity is null)
        {
            return null;
        }

        var upgradedIdentity = UpgradeDeviceIdentity(deviceIdentity);
        if (upgradedIdentity != deviceIdentity)
        {
            await WriteJsonAsync(path, upgradedIdentity, cancellationToken);
        }

        return upgradedIdentity;
    }

    private DeviceIdentity CreateDeviceIdentity()
    {
        return new DeviceIdentity(
            Guid.NewGuid().ToString("N"),
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            DateTimeOffset.UtcNow.ToString("O"),
            FingerprintHash: ComputeFingerprintHash(Environment.MachineName, RuntimeInformation.OSDescription),
            WorkspaceRootHash: ComputeWorkspaceRootHash(RootPath),
            AppVersion: GetCurrentAppVersion(),
            LastSeenAtUtc: DateTimeOffset.UtcNow.ToString("O"));
    }

    private DeviceIdentity UpgradeDeviceIdentity(DeviceIdentity deviceIdentity)
    {
        var fingerprintHash = string.IsNullOrWhiteSpace(deviceIdentity.FingerprintHash)
            ? ComputeFingerprintHash(deviceIdentity.MachineName, deviceIdentity.Platform)
            : deviceIdentity.FingerprintHash;
        var workspaceRootHash = string.IsNullOrWhiteSpace(deviceIdentity.WorkspaceRootHash)
            ? ComputeWorkspaceRootHash(RootPath)
            : deviceIdentity.WorkspaceRootHash;
        var appVersion = string.IsNullOrWhiteSpace(deviceIdentity.AppVersion)
            ? GetCurrentAppVersion()
            : deviceIdentity.AppVersion;
        var lastSeenAtUtc = string.IsNullOrWhiteSpace(deviceIdentity.LastSeenAtUtc)
            ? DateTimeOffset.UtcNow.ToString("O")
            : deviceIdentity.LastSeenAtUtc;

        if (string.Equals(fingerprintHash, deviceIdentity.FingerprintHash, StringComparison.Ordinal) &&
            string.Equals(workspaceRootHash, deviceIdentity.WorkspaceRootHash, StringComparison.Ordinal) &&
            string.Equals(appVersion, deviceIdentity.AppVersion, StringComparison.Ordinal) &&
            string.Equals(lastSeenAtUtc, deviceIdentity.LastSeenAtUtc, StringComparison.Ordinal))
        {
            return deviceIdentity;
        }

        return deviceIdentity with
        {
            FingerprintHash = fingerprintHash,
            WorkspaceRootHash = workspaceRootHash,
            AppVersion = appVersion,
            LastSeenAtUtc = lastSeenAtUtc,
        };
    }

    private static string ComputeFingerprintHash(string machineName, string platform)
    {
        var payload = string.Join(
            "\n",
            NormalizeFingerprintComponent(machineName),
            NormalizeFingerprintComponent(platform),
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString());

        return ComputeSha256Hex(payload);
    }

    private static string ComputeWorkspaceRootHash(string rootPath)
    {
        return ComputeSha256Hex(Path.GetFullPath(rootPath).Trim());
    }

    private static string ComputeSha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string NormalizeFingerprintComponent(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private static string GetCurrentAppVersion()
    {
        return typeof(WorkspaceService).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    private static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        }

        if (sessionId.Contains("..", StringComparison.Ordinal)
            || sessionId.IndexOf(Path.DirectorySeparatorChar) >= 0
            || sessionId.IndexOf(Path.AltDirectorySeparatorChar) >= 0
            || sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Session id contains invalid path characters.", nameof(sessionId));
        }
    }

    private string GetAbsolutePath(params string[] segments)
    {
        if (segments.Length == 0)
        {
            return RootPath;
        }

        var path = RootPath;
        foreach (var segment in segments)
        {
            path = Path.Combine(path, segment);
        }

        return path;
    }

    private static async Task WriteTextIfMissingAsync(string path, string content, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, cancellationToken);
    }

    /// <summary>
    /// Opens a file for reading with <see cref="FileShare.ReadWrite"/> and retries on
    /// <see cref="IOException"/> to tolerate transient Windows file locks (AV scanner,
    /// concurrent Gateway process briefly holding a write handle, etc.).
    /// </summary>
    private static async Task<FileStream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        IOException? lastEx = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(50 * attempt, cancellationToken);
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
            }
            catch (IOException ex) when (attempt < maxAttempts - 1)
            {
                lastEx = ex;
            }
        }
        throw lastEx!;
    }

    private static async Task WriteJsonIfMissingAsync<T>(string path, T payload, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return;
        }

        await WriteJsonAsync(path, payload, cancellationToken);
    }

    private static async Task WriteJsonAsync<T>(string path, T payload, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, payload, WorkspaceJson.Default, cancellationToken);
    }

    private void RecordDiagnostic(
        string eventType,
        string level,
        string message,
        IReadOnlyDictionary<string, string?>? attributes = null)
    {
        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: $"diag-{Guid.NewGuid():N}",
            Source: DiagnosticSource,
            EventType: eventType,
            Level: level,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow,
            Attributes: attributes));
    }
}
