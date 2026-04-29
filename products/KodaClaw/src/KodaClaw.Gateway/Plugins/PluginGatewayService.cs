using KodaClaw.Contracts;
using KodaClaw.Contracts.Plugins;
using KodaClaw.Contracts.Workspace;
using KodaClaw.PluginHost.Hosting;
using KodaClaw.PluginHost.Manifest;
using KodaClaw.PluginHost.Permissions;
using KodaClaw.PluginHost.Trust;
using Microsoft.Extensions.Configuration;
using PluginPermissionRiskSummary = KodaClaw.Contracts.Plugins.PluginPermissionRiskSummary;

namespace KodaClaw.Gateway.Plugins;

internal interface IPluginGatewayService
{
    Task<PluginsQueryResponse> ListAsync(PluginQuery? query = null, CancellationToken cancellationToken = default);

    Task<PluginDetail?> GetDetailAsync(string pluginId, CancellationToken cancellationToken = default);

    Task<PluginDetail> InstallLocalAsync(
        InstallLocalPluginRequest request,
        CancellationToken cancellationToken = default);

    Task<PluginsQueryResponse> DiscoverAsync(CancellationToken cancellationToken = default);

    Task<PluginDetail?> TrustAsync(string pluginId, CancellationToken cancellationToken = default);

    Task<PluginDetail?> SetEnabledAsync(
        string pluginId,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<PluginDetail?> StartAsync(string pluginId, CancellationToken cancellationToken = default);

    Task<PluginDetail?> StopAsync(string pluginId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PluginLogEntry>> GetLogsAsync(
        string pluginId,
        int limit,
        CancellationToken cancellationToken = default);
}

internal sealed class PluginGatewayService : IPluginGatewayService
{
    private const string ManifestFileName = "plugin.json";
    private const string BundledPluginsDirectoryName = "plugins";

    private readonly IWorkspaceService _workspaceService;
    private readonly IPluginRegistryRepository _registryRepository;
    private readonly IPluginLogRepository _logRepository;
    private readonly IPluginLifecycleHost _lifecycleHost;
    private readonly PluginManifestLoader _manifestLoader;
    private readonly IPluginTrustEvaluator _trustEvaluator;
    private readonly IConfiguration _configuration;

    public PluginGatewayService(
        IWorkspaceService workspaceService,
        IPluginRegistryRepository registryRepository,
        IPluginLogRepository logRepository,
        IPluginLifecycleHost lifecycleHost,
        PluginManifestLoader manifestLoader,
        IPluginTrustEvaluator trustEvaluator,
        IConfiguration configuration)
    {
        _workspaceService = workspaceService;
        _registryRepository = registryRepository;
        _logRepository = logRepository;
        _lifecycleHost = lifecycleHost;
        _manifestLoader = manifestLoader;
        _trustEvaluator = trustEvaluator;
        _configuration = configuration;
    }

    public async Task<PluginsQueryResponse> ListAsync(
        PluginQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var records = await _registryRepository.ListAsync(query, cancellationToken);
        return new PluginsQueryResponse(records.Select(ToSummary).ToArray());
    }

    public async Task<PluginDetail?> GetDetailAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ValidatePluginId(pluginId);

        var record = await _registryRepository.GetByIdAsync(pluginId, cancellationToken);
        return record is null
            ? null
            : await BuildDetailAsync(record, cancellationToken);
    }

    public async Task<PluginDetail> InstallLocalAsync(
        InstallLocalPluginRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Path))
        {
            throw new ArgumentException("Plugin install path is required.", nameof(request));
        }

        var sourceRoot = Path.GetFullPath(request.Path.Trim());
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"Plugin directory was not found: '{sourceRoot}'.");
        }

        var manifest = await _manifestLoader.LoadFromFileAsync(
            Path.Combine(sourceRoot, ManifestFileName),
            cancellationToken);
        var destinationRoot = Path.Combine(
            _workspaceService.RootPath,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            BundledPluginsDirectoryName,
            manifest.Id);
        var existing = await _registryRepository.GetByIdAsync(manifest.Id, cancellationToken);

        if (existing is { RuntimeState: PluginRuntimeState.Running or PluginRuntimeState.Starting })
        {
            throw new InvalidOperationException("Stop the plugin before reinstalling it.");
        }

        if (!PathsEqual(sourceRoot, destinationRoot))
        {
            CopyDirectory(sourceRoot, destinationRoot);
        }

        var trustEvidence = await _trustEvaluator.EvaluateAsync(destinationRoot, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var record = existing is null
            ? new PluginRecord(
                Id: manifest.Id,
                Manifest: manifest,
                InstallSource: PluginInstallSource.LocalDirectory,
                RootPath: destinationRoot,
                TrustState: PluginTrustState.Untrusted,
                Enabled: false,
                RuntimeState: PluginRuntimeState.Stopped,
                DiscoveredAt: now,
                InstalledAt: now,
                UpdatedAt: now,
                TrustEvidence: trustEvidence)
            : existing with
            {
                Manifest = manifest,
                InstallSource = PluginInstallSource.LocalDirectory,
                RootPath = destinationRoot,
                TrustEvidence = trustEvidence,
                InstalledAt = now,
                UpdatedAt = now,
                RuntimeState = existing.RuntimeState == PluginRuntimeState.Degraded
                    ? PluginRuntimeState.Stopped
                    : existing.RuntimeState,
            };

        await _registryRepository.UpsertAsync(record, cancellationToken);
        return await BuildDetailAsync(record, cancellationToken);
    }

    public async Task<PluginsQueryResponse> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var discovered = new Dictionary<string, PluginRecord>(StringComparer.Ordinal);
        foreach (var discoveryRoot in EnumerateDiscoveryRoots())
        {
            if (!Directory.Exists(discoveryRoot.Path))
            {
                continue;
            }

            foreach (var pluginRoot in Directory.EnumerateDirectories(discoveryRoot.Path))
            {
                var manifestPath = Path.Combine(pluginRoot, ManifestFileName);
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                try
                {
                    var manifest = await _manifestLoader.LoadFromFileAsync(manifestPath, cancellationToken);
                    var existing = await _registryRepository.GetByIdAsync(manifest.Id, cancellationToken);
                    var trustEvidence = await _trustEvaluator.EvaluateAsync(pluginRoot, cancellationToken);
                    var now = DateTimeOffset.UtcNow;
                    var record = existing is null
                        ? new PluginRecord(
                            Id: manifest.Id,
                            Manifest: manifest,
                            InstallSource: discoveryRoot.InstallSource,
                            RootPath: pluginRoot,
                            TrustState: PluginTrustState.Untrusted,
                            Enabled: false,
                            RuntimeState: PluginRuntimeState.Stopped,
                            DiscoveredAt: now,
                            InstalledAt: now,
                            UpdatedAt: now,
                            TrustEvidence: trustEvidence)
                        : existing with
                        {
                            Manifest = manifest,
                            InstallSource = discoveryRoot.InstallSource,
                            RootPath = pluginRoot,
                            TrustEvidence = trustEvidence,
                            UpdatedAt = now,
                        };

                    await _registryRepository.UpsertAsync(record, cancellationToken);
                    discovered[record.Id] = record;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Skip invalid plugin candidates; diagnostics are recorded by the caller.
                }
            }
        }

        var items = discovered.Values
            .OrderByDescending(item => item.UpdatedAt)
            .Select(ToSummary)
            .ToArray();
        return new PluginsQueryResponse(items);
    }

    public async Task<PluginDetail?> TrustAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        var record = await LoadRecordAsync(pluginId, cancellationToken);
        if (record is null)
        {
            return null;
        }

        record = await RefreshTrustEvidenceAsync(record, cancellationToken);
        var trustState = record.TrustEvidence?.VerificationState == PluginTrustVerificationState.Verified
            ? PluginTrustState.Signed
            : PluginTrustState.Trusted;
        var trusted = record with
        {
            TrustState = trustState,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _registryRepository.UpsertAsync(trusted, cancellationToken);
        return await BuildDetailAsync(trusted, cancellationToken);
    }

    public async Task<PluginDetail?> SetEnabledAsync(
        string pluginId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var record = await LoadRecordAsync(pluginId, cancellationToken);
        if (record is null)
        {
            return null;
        }

        PluginRecord updated;
        if (!enabled)
        {
            updated = record.RuntimeState == PluginRuntimeState.Stopped
                ? record with
                {
                    Enabled = false,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    RuntimeState = PluginRuntimeState.Stopped,
                }
                : await _lifecycleHost.StopAsync(pluginId, cancellationToken);

            updated = updated with
            {
                Enabled = false,
                UpdatedAt = DateTimeOffset.UtcNow,
                RuntimeState = PluginRuntimeState.Stopped,
            };
            await _registryRepository.UpsertAsync(updated, cancellationToken);
            return await BuildDetailAsync(updated, cancellationToken);
        }

        updated = record with
        {
            Enabled = true,
            UpdatedAt = DateTimeOffset.UtcNow,
            RuntimeState = record.RuntimeState == PluginRuntimeState.Degraded
                ? PluginRuntimeState.Stopped
                : record.RuntimeState,
        };
        await _registryRepository.UpsertAsync(updated, cancellationToken);
        return await BuildDetailAsync(updated, cancellationToken);
    }

    public async Task<PluginDetail?> StartAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        var record = await LoadRecordAsync(pluginId, cancellationToken);
        if (record is null)
        {
            return null;
        }

        var started = await _lifecycleHost.StartAsync(pluginId, cancellationToken);
        return await BuildDetailAsync(started, cancellationToken);
    }

    public async Task<PluginDetail?> StopAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        var record = await LoadRecordAsync(pluginId, cancellationToken);
        if (record is null)
        {
            return null;
        }

        var stopped = await _lifecycleHost.StopAsync(pluginId, cancellationToken);
        return await BuildDetailAsync(stopped, cancellationToken);
    }

    public async Task<IReadOnlyList<PluginLogEntry>> GetLogsAsync(
        string pluginId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidatePluginId(pluginId);
        return await _logRepository.ListAsync(pluginId, limit, cancellationToken);
    }

    private async Task<PluginRecord?> LoadRecordAsync(string pluginId, CancellationToken cancellationToken)
    {
        ValidatePluginId(pluginId);
        return await _registryRepository.GetByIdAsync(pluginId, cancellationToken);
    }

    private async Task<PluginDetail> BuildDetailAsync(
        PluginRecord record,
        CancellationToken cancellationToken)
    {
        record = await RefreshTrustEvidenceAsync(record, cancellationToken);
        var permissionSummary = PluginPermissionPolicy.SummarizeRisk(record.Manifest.Permissions);
        var availableTools = await ResolveAvailableToolsAsync(record, cancellationToken);

        return new PluginDetail(
            Record: record,
            PermissionSummary: new PluginPermissionRiskSummary(
                permissionSummary.HighRiskReasons,
                permissionSummary.MediumRiskReasons),
            HealthSummary: BuildHealthSummary(record),
            AvailableTools: availableTools);
    }

    private async Task<PluginRecord> RefreshTrustEvidenceAsync(
        PluginRecord record,
        CancellationToken cancellationToken)
    {
        var evidence = await _trustEvaluator.EvaluateAsync(record.RootPath, cancellationToken);
        if (Equals(record.TrustEvidence, evidence))
        {
            return record;
        }

        var updated = record with
        {
            TrustEvidence = evidence,
        };
        await _registryRepository.UpsertAsync(updated, cancellationToken);
        return updated;
    }

    private async Task<IReadOnlyList<string>> ResolveAvailableToolsAsync(
        PluginRecord record,
        CancellationToken cancellationToken)
    {
        if (record.RuntimeState == PluginRuntimeState.Running)
        {
            var activeTools = await _lifecycleHost.GetToolsAsync(record.Id, cancellationToken);
            var activeToolNames = activeTools.Select(tool => tool.Name).ToArray();
            if (activeToolNames.Length > 0)
            {
                return activeToolNames;
            }
        }

        return BuildDeclaredToolNames(record);
    }

    private static IReadOnlyList<string> BuildDeclaredToolNames(PluginRecord record)
    {
        var tools = record.Manifest.Capabilities.Tools;
        if (tools is not { Count: > 0 })
        {
            return Array.Empty<string>();
        }

        return tools
            .Select(tool => $"mcp__{record.Id}__{tool}")
            .ToArray();
    }

    private static PluginHealthSummary BuildHealthSummary(PluginRecord record)
    {
        var status = record.RuntimeState switch
        {
            PluginRuntimeState.Running when string.IsNullOrWhiteSpace(record.LastError) => "Healthy",
            PluginRuntimeState.Running => "Warning",
            PluginRuntimeState.Degraded => "Degraded",
            PluginRuntimeState.Starting => "Starting",
            PluginRuntimeState.Stopped => "Stopped",
            _ => record.RuntimeState.ToString(),
        };
        var message = string.IsNullOrWhiteSpace(record.LastError)
            ? record.RuntimeState switch
            {
                PluginRuntimeState.Running => "Plugin runtime is responding.",
                PluginRuntimeState.Starting => "Plugin runtime is starting.",
                PluginRuntimeState.Stopped => "Plugin runtime is stopped.",
                PluginRuntimeState.Degraded => "Plugin runtime needs operator attention.",
                _ => null,
            }
            : record.LastError;

        return new PluginHealthSummary(
            Status: status,
            Message: message,
            LastHealthAt: record.LastHealthAt,
            RestartCount: record.RestartCount);
    }

    private static PluginSummary ToSummary(PluginRecord record)
    {
        return new PluginSummary(
            Id: record.Id,
            Name: record.Manifest.Name,
            Version: record.Manifest.Version,
            Types: record.Manifest.Types,
            InstallSource: record.InstallSource,
            TrustState: record.TrustState,
            Enabled: record.Enabled,
            RuntimeState: record.RuntimeState,
            RootPath: record.RootPath,
            UpdatedAt: record.UpdatedAt,
            LastError: record.LastError);
    }

    private IEnumerable<PluginDiscoveryRoot> EnumerateDiscoveryRoots()
    {
        var roots = new List<PluginDiscoveryRoot>();
        var configPluginsRoot = Path.Combine(
            _workspaceService.RootPath,
            KodaClawWorkspaceLayout.ConfigDirectory,
            BundledPluginsDirectoryName);
        roots.Add(new PluginDiscoveryRoot(configPluginsRoot, PluginInstallSource.Bundled));

        var configuredRoot = _configuration["KODACLAW_BUNDLED_PLUGINS_ROOT"];
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            foreach (var rawPath in configuredRoot.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                roots.Add(new PluginDiscoveryRoot(Path.GetFullPath(rawPath), PluginInstallSource.Bundled));
            }
        }

        var workspacePluginsRoot = Path.Combine(
            _workspaceService.RootPath,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            BundledPluginsDirectoryName);
        roots.Add(new PluginDiscoveryRoot(workspacePluginsRoot, PluginInstallSource.LocalDirectory));

        return roots
            .DistinctBy(item => item.Path)
            .ToArray();
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);

        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, directory);
            Directory.CreateDirectory(Path.Combine(destinationRoot, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, file);
            var destinationPath = Path.Combine(destinationRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            comparison);
    }

    private static void ValidatePluginId(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            throw new ArgumentException("Plugin id is required.", nameof(pluginId));
        }
    }

    private sealed record PluginDiscoveryRoot(string Path, PluginInstallSource InstallSource);
}
