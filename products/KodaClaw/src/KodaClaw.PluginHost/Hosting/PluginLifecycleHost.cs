using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Plugins;
using KodaClaw.Contracts.Secrets;
using KodaClaw.PluginHost.Trust;
using Kode.Agent.Mcp;
using Kode.Agent.Sdk.Core.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace KodaClaw.PluginHost.Hosting;

public sealed class PluginLifecycleHost : IPluginLifecycleHost, IAsyncDisposable
{
    private const string HostSource = "plugin.host";
    private const string HealthSource = "plugin.health";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IPluginRegistryRepository _registryRepository;
    private readonly McpClientManager _mcpClientManager;
    private readonly IPluginLogRepository? _logRepository;
    private readonly IDiagnosticsService? _diagnosticsService;
    private readonly IPluginTrustEvaluator _trustEvaluator;
    private readonly PluginHostOptions _options;
    private readonly PluginRuntimeValueResolver _runtimeValueResolver;
    private readonly ILogger<PluginLifecycleHost>? _logger;
    private readonly Dictionary<string, HostedPluginSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public PluginLifecycleHost(
        IPluginRegistryRepository registryRepository,
        McpClientManager mcpClientManager,
        IPluginTrustEvaluator trustEvaluator,
        IPluginLogRepository? logRepository = null,
        IDiagnosticsService? diagnosticsService = null,
        PluginHostOptions? options = null,
        ISecretStore? secretStore = null,
        ILogger<PluginLifecycleHost>? logger = null)
    {
        _registryRepository = registryRepository ?? throw new ArgumentNullException(nameof(registryRepository));
        _mcpClientManager = mcpClientManager ?? throw new ArgumentNullException(nameof(mcpClientManager));
        _trustEvaluator = trustEvaluator ?? throw new ArgumentNullException(nameof(trustEvaluator));
        _logRepository = logRepository;
        _diagnosticsService = diagnosticsService;
        _options = options ?? new PluginHostOptions();
        _runtimeValueResolver = new PluginRuntimeValueResolver(secretStore);
        _logger = logger;
    }

    public async Task<PluginRecord> StartAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ValidatePluginId(pluginId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var record = await LoadRequiredRecordAsync(pluginId, cancellationToken);
            record = await RefreshTrustEvidenceAsync(record, cancellationToken);
            ValidateStartPreconditions(record);

            if (_sessions.TryGetValue(pluginId, out var existingSession))
            {
                return await PersistRecordAsync(
                    record with
                    {
                        RuntimeState = PluginRuntimeState.Running,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        LastStartedAt = existingSession.StartedAt,
                        LastError = null,
                    },
                    cancellationToken);
            }

            var startingAt = DateTimeOffset.UtcNow;
            record = await PersistRecordAsync(
                record with
                {
                    RuntimeState = PluginRuntimeState.Starting,
                    UpdatedAt = startingAt,
                    LastError = null,
                },
                cancellationToken);

            await AppendLogAsync(
                pluginId,
                level: "info",
                source: HostSource,
                message: "Plugin start requested.",
                payload: new
                {
                    runtime = record.Manifest.Runtime.Transport.ToString(),
                    rootPath = record.RootPath,
                },
                cancellationToken);
            RecordDiagnostic(
                source: HostSource,
                eventType: "plugin.starting",
                level: "info",
                message: $"Plugin '{pluginId}' is starting.",
                pluginId);

            try
            {
                var session = await ConnectSessionAsync(record, cancellationToken);
                _sessions[pluginId] = session;

                var startedRecord = await PersistRecordAsync(
                    record with
                    {
                        RuntimeState = PluginRuntimeState.Running,
                        UpdatedAt = startingAt,
                        LastStartedAt = startingAt,
                        LastHealthAt = startingAt,
                        LastError = null,
                    },
                    cancellationToken);

                await AppendLogAsync(
                    pluginId,
                    level: "info",
                    source: HostSource,
                    message: "Plugin started successfully.",
                    payload: new
                    {
                        toolCount = session.Tools.Count,
                        tools = session.Tools.Select(tool => tool.Name).ToArray(),
                    },
                    cancellationToken);
                RecordDiagnostic(
                    source: HostSource,
                    eventType: "plugin.started",
                    level: "info",
                    message: $"Plugin '{pluginId}' started.",
                    pluginId,
                    new Dictionary<string, string?>
                    {
                        ["toolCount"] = session.Tools.Count.ToString(),
                    });

                return startedRecord;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return await PersistRuntimeFailureAsync(
                    record,
                    ex,
                    PluginRuntimeState.Degraded,
                    "plugin.start_failed",
                    HostSource,
                    cancellationToken);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<PluginRecord> StopAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ValidatePluginId(pluginId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var record = await LoadRequiredRecordAsync(pluginId, cancellationToken);

            _sessions.Remove(pluginId);
            await _mcpClientManager.DisconnectAsync(pluginId);

            var stoppedAt = DateTimeOffset.UtcNow;
            var stoppedRecord = await PersistRecordAsync(
                record with
                {
                    RuntimeState = PluginRuntimeState.Stopped,
                    UpdatedAt = stoppedAt,
                    LastStoppedAt = stoppedAt,
                },
                cancellationToken);

            await AppendLogAsync(
                pluginId,
                level: "info",
                source: HostSource,
                message: "Plugin stopped.",
                payload: null,
                cancellationToken);
            RecordDiagnostic(
                source: HostSource,
                eventType: "plugin.stopped",
                level: "info",
                message: $"Plugin '{pluginId}' stopped.",
                pluginId);

            return stoppedRecord;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<PluginRecord> RestartAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ValidatePluginId(pluginId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var record = await LoadRequiredRecordAsync(pluginId, cancellationToken);
            ValidateStartPreconditions(record);

            await AppendLogAsync(
                pluginId,
                level: "warning",
                source: HostSource,
                message: "Plugin restart requested.",
                payload: null,
                cancellationToken);
            RecordDiagnostic(
                source: HostSource,
                eventType: "plugin.restart.requested",
                level: "warning",
                message: $"Plugin '{pluginId}' restart requested.",
                pluginId);

            try
            {
                var session = await RestartSessionAsync(record, cancellationToken);
                _sessions[pluginId] = session;

                var restartedAt = DateTimeOffset.UtcNow;
                var restartedRecord = await PersistRecordAsync(
                    record with
                    {
                        RuntimeState = PluginRuntimeState.Running,
                        UpdatedAt = restartedAt,
                        LastStartedAt = restartedAt,
                        LastHealthAt = restartedAt,
                        RestartCount = record.RestartCount + 1,
                        LastError = null,
                    },
                    cancellationToken);

                await AppendLogAsync(
                    pluginId,
                    level: "info",
                    source: HostSource,
                    message: "Plugin restarted successfully.",
                    payload: new { restartCount = restartedRecord.RestartCount },
                    cancellationToken);
                RecordDiagnostic(
                    source: HostSource,
                    eventType: "plugin.restarted",
                    level: "info",
                    message: $"Plugin '{pluginId}' restarted.",
                    pluginId,
                    new Dictionary<string, string?>
                    {
                        ["restartCount"] = restartedRecord.RestartCount.ToString(),
                    });

                return restartedRecord;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return await PersistRuntimeFailureAsync(
                    record with { RestartCount = record.RestartCount + 1 },
                    ex,
                    PluginRuntimeState.Degraded,
                    "plugin.restart.failed",
                    HostSource,
                    cancellationToken);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<PluginRecord> CheckHealthAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ValidatePluginId(pluginId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var record = await LoadRequiredRecordAsync(pluginId, cancellationToken);
            if (record.RuntimeState != PluginRuntimeState.Running)
            {
                return record;
            }

            if (!_sessions.TryGetValue(pluginId, out var session))
            {
                return await DegradeAsync(
                    record,
                    "Plugin session is not loaded in memory.",
                    updateRestartCount: false,
                    source: HealthSource,
                    eventType: "plugin.health.session_missing",
                    cancellationToken);
            }

            var probeError = await ProbeHealthAsync(record, session.Config, cancellationToken);
            if (probeError is null)
            {
                var healthyAt = DateTimeOffset.UtcNow;
                var healthyRecord = await PersistRecordAsync(
                    record with
                    {
                        RuntimeState = PluginRuntimeState.Running,
                        UpdatedAt = healthyAt,
                        LastHealthAt = healthyAt,
                        LastError = null,
                    },
                    cancellationToken);

                await AppendLogAsync(
                    pluginId,
                    level: "info",
                    source: HealthSource,
                    message: "Plugin healthcheck passed.",
                    payload: null,
                    cancellationToken);
                RecordDiagnostic(
                    source: HealthSource,
                    eventType: "plugin.health.ok",
                    level: "info",
                    message: $"Plugin '{pluginId}' passed healthcheck.",
                    pluginId);

                return healthyRecord;
            }

            await AppendLogAsync(
                pluginId,
                level: "warning",
                source: HealthSource,
                message: probeError,
                payload: null,
                cancellationToken);
            RecordDiagnostic(
                source: HealthSource,
                eventType: "plugin.health.failed",
                level: "warning",
                message: probeError,
                pluginId);

            var attempts = record.RestartCount;
            while (attempts < _options.MaxRestartAttempts)
            {
                attempts++;
                var restartedAt = DateTimeOffset.UtcNow;
                await AppendLogAsync(
                    pluginId,
                    level: "warning",
                    source: HealthSource,
                    message: $"Plugin healthcheck failed. Attempting restart {attempts}/{_options.MaxRestartAttempts}.",
                    payload: null,
                    cancellationToken);
                RecordDiagnostic(
                    source: HealthSource,
                    eventType: "plugin.restart.requested",
                    level: "warning",
                    message: $"Plugin '{pluginId}' restart attempt {attempts}.",
                    pluginId,
                    new Dictionary<string, string?>
                    {
                        ["restartCount"] = attempts.ToString(),
                    });

                try
                {
                    session = await RestartSessionAsync(record, cancellationToken);
                    _sessions[pluginId] = session;

                    probeError = await ProbeHealthAsync(record, session.Config, cancellationToken);
                    if (probeError is null)
                    {
                        var restartedRecord = await PersistRecordAsync(
                            record with
                            {
                                RuntimeState = PluginRuntimeState.Running,
                                UpdatedAt = restartedAt,
                                LastStartedAt = restartedAt,
                                LastHealthAt = restartedAt,
                                RestartCount = attempts,
                                LastError = null,
                            },
                            cancellationToken);

                        await AppendLogAsync(
                            pluginId,
                            level: "info",
                            source: HealthSource,
                            message: "Plugin recovered after restart.",
                            payload: new { restartCount = attempts },
                            cancellationToken);
                        RecordDiagnostic(
                            source: HealthSource,
                            eventType: "plugin.restarted",
                            level: "info",
                            message: $"Plugin '{pluginId}' recovered after restart.",
                            pluginId,
                            new Dictionary<string, string?>
                            {
                                ["restartCount"] = attempts.ToString(),
                            });

                        return restartedRecord;
                    }

                    await AppendLogAsync(
                        pluginId,
                        level: "warning",
                        source: HealthSource,
                        message: probeError,
                        payload: new { restartCount = attempts },
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    probeError = ex.GetBaseException().Message;
                    await AppendLogAsync(
                        pluginId,
                        level: "error",
                        source: HealthSource,
                        message: probeError,
                        payload: new { restartCount = attempts },
                        cancellationToken);
                    _logger?.LogWarning(ex, "Plugin restart failed for {PluginId}", pluginId);
                }
            }

            return await DegradeAsync(
                record with { RestartCount = attempts },
                probeError ?? "Plugin healthcheck failed.",
                updateRestartCount: true,
                source: HealthSource,
                eventType: "plugin.degraded",
                cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public Task<IReadOnlyList<ITool>> GetToolsAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePluginId(pluginId);

        IReadOnlyList<ITool> tools = _sessions.TryGetValue(pluginId, out var session)
            ? session.Tools
            : Array.Empty<ITool>();
        return Task.FromResult(tools);
    }

    public Task<IReadOnlyList<ITool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ITool> tools = _sessions.Values.SelectMany(session => session.Tools).ToArray();
        return Task.FromResult(tools);
    }

    public async ValueTask DisposeAsync()
    {
        await _mcpClientManager.DisconnectAllAsync();
        _sessions.Clear();
        _mutex.Dispose();
    }

    private async Task<PluginRecord> LoadRequiredRecordAsync(string pluginId, CancellationToken cancellationToken)
    {
        var record = await _registryRepository.GetByIdAsync(pluginId, cancellationToken);
        return record ?? throw new InvalidOperationException($"Plugin '{pluginId}' is not registered.");
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

        return await PersistRecordAsync(
            record with
            {
                TrustEvidence = evidence,
            },
            cancellationToken);
    }

    private static void ValidateStartPreconditions(PluginRecord record)
    {
        if (record.TrustState is not (PluginTrustState.Trusted or PluginTrustState.Signed))
        {
            throw new InvalidOperationException($"Plugin '{record.Id}' must be trusted before start.");
        }

        if (record.TrustState == PluginTrustState.Signed &&
            record.TrustEvidence?.VerificationState != PluginTrustVerificationState.Verified)
        {
            throw new InvalidOperationException(
                $"Plugin '{record.Id}' must pass signature verification before start.");
        }

        if (!record.Enabled)
        {
            throw new InvalidOperationException($"Plugin '{record.Id}' must be enabled before start.");
        }

        if (record.Manifest.Runtime.Transport != PluginTransportKind.Stdio)
        {
            throw new NotSupportedException(
                $"Plugin transport '{record.Manifest.Runtime.Transport}' is not supported in Iteration 4.");
        }
    }

    private async Task<HostedPluginSession> ConnectSessionAsync(
        PluginRecord record,
        CancellationToken cancellationToken)
    {
        var config = BuildConfig(record);
        var tools = await McpToolProvider.GetToolsAsync(
            _mcpClientManager,
            config,
            _logger,
            cancellationToken);

        if (record.Manifest.Types.Contains(PluginType.Tool) && tools.Count == 0)
        {
            throw new InvalidOperationException($"Plugin '{record.Id}' did not expose any MCP tools.");
        }

        return new HostedPluginSession(
            Config: config,
            Tools: tools,
            StartedAt: DateTimeOffset.UtcNow);
    }

    private async Task<HostedPluginSession> RestartSessionAsync(
        PluginRecord record,
        CancellationToken cancellationToken)
    {
        _sessions.Remove(record.Id);
        await _mcpClientManager.DisconnectAsync(record.Id);
        return await ConnectSessionAsync(record, cancellationToken);
    }

    private async Task<string?> ProbeHealthAsync(
        PluginRecord record,
        McpConfig config,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CreateTimeoutTokenSource(record, cancellationToken);

        try
        {
            var client = await _mcpClientManager.GetOrReconnectAsync(record.Id, config, timeoutCts.Token);
            var healthcheckTool = record.Manifest.Healthcheck?.ToolName;

            if (string.IsNullOrWhiteSpace(healthcheckTool))
            {
                await client.ListToolsAsync(cancellationToken: timeoutCts.Token);
                return null;
            }

            var result = await client.CallToolAsync(
                healthcheckTool,
                arguments: null,
                cancellationToken: timeoutCts.Token);

            if (result.IsError == true)
            {
                return $"Plugin healthcheck tool '{healthcheckTool}' returned an error.";
            }

            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"Plugin healthcheck timed out for '{record.Id}'.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not TaskCanceledException)
        {
            return ex.GetBaseException().Message;
        }
    }

    private CancellationTokenSource CreateTimeoutTokenSource(
        PluginRecord record,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = record.Manifest.Healthcheck?.TimeoutSeconds
            ?? _options.DefaultHealthcheckTimeoutSeconds;
        if (timeoutSeconds <= 0)
        {
            timeoutSeconds = _options.DefaultHealthcheckTimeoutSeconds;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        return cts;
    }

    private McpConfig BuildConfig(PluginRecord record)
    {
        var runtime = record.Manifest.Runtime;
        return new McpConfig
        {
            ServerName = record.Id,
            Transport = MapTransport(runtime.Transport),
            Command = ResolveCommand(runtime.Command, record.RootPath),
            Args = ResolveArguments(runtime.Args, record.RootPath),
            Environment = _runtimeValueResolver.Resolve(runtime.Environment, runtime.EnvironmentReferences),
            Url = runtime.Url,
            Headers = _runtimeValueResolver.Resolve(runtime.Headers, runtime.HeaderReferences),
            Include = BuildIncludedTools(record.Manifest),
        };
    }

    private static McpTransportType MapTransport(PluginTransportKind transport)
    {
        return transport switch
        {
            PluginTransportKind.Stdio => McpTransportType.Stdio,
            PluginTransportKind.Http => McpTransportType.Http,
            PluginTransportKind.StreamableHttp => McpTransportType.StreamableHttp,
            PluginTransportKind.Sse => McpTransportType.Sse,
            _ => throw new NotSupportedException($"Unsupported plugin transport '{transport}'."),
        };
    }

    private static IReadOnlyList<string>? BuildIncludedTools(PluginManifest manifest)
    {
        if (manifest.Capabilities.Tools is not { Count: > 0 })
        {
            return null;
        }

        return manifest.Capabilities.Tools
            .Where(static tool => !string.IsNullOrWhiteSpace(tool))
            .Select(static tool => tool.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string? ResolveCommand(string? command, string pluginRootPath)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return command;
        }

        var trimmed = command.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            return trimmed;
        }

        if (!trimmed.Contains(Path.DirectorySeparatorChar)
            && !trimmed.Contains(Path.AltDirectorySeparatorChar))
        {
            return trimmed;
        }

        return Path.GetFullPath(Path.Combine(pluginRootPath, trimmed));
    }

    private static IReadOnlyList<string>? ResolveArguments(
        IReadOnlyList<string>? args,
        string pluginRootPath)
    {
        if (args is not { Count: > 0 })
        {
            return args;
        }

        var resolved = new List<string>(args.Count);
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg))
            {
                resolved.Add(arg);
                continue;
            }

            var trimmed = arg.Trim();
            if (Path.IsPathRooted(trimmed))
            {
                resolved.Add(trimmed);
                continue;
            }

            var candidate = Path.GetFullPath(Path.Combine(pluginRootPath, trimmed));
            resolved.Add(File.Exists(candidate) || Directory.Exists(candidate) ? candidate : trimmed);
        }

        return resolved;
    }

    private async Task<PluginRecord> DegradeAsync(
        PluginRecord record,
        string errorMessage,
        bool updateRestartCount,
        string source,
        string eventType,
        CancellationToken cancellationToken)
    {
        _sessions.Remove(record.Id);
        await _mcpClientManager.DisconnectAsync(record.Id);

        var degradedAt = DateTimeOffset.UtcNow;
        var degradedRecord = await PersistRecordAsync(
            record with
                {
                    RuntimeState = PluginRuntimeState.Degraded,
                    UpdatedAt = degradedAt,
                    LastHealthAt = degradedAt,
                    RestartCount = updateRestartCount ? record.RestartCount : record.RestartCount,
                    LastError = errorMessage,
                },
            cancellationToken);

        await AppendLogAsync(
            record.Id,
            level: "error",
            source: source,
            message: errorMessage,
            payload: new { runtimeState = PluginRuntimeState.Degraded.ToString() },
            cancellationToken);
        RecordDiagnostic(
            source: source,
            eventType: eventType,
            level: "error",
            message: errorMessage,
            record.Id,
            new Dictionary<string, string?>
            {
                ["runtimeState"] = PluginRuntimeState.Degraded.ToString(),
                ["restartCount"] = degradedRecord.RestartCount.ToString(),
            });

        return degradedRecord;
    }

    private async Task<PluginRecord> PersistRuntimeFailureAsync(
        PluginRecord record,
        Exception exception,
        PluginRuntimeState runtimeState,
        string eventType,
        string source,
        CancellationToken cancellationToken)
    {
        _sessions.Remove(record.Id);
        await _mcpClientManager.DisconnectAsync(record.Id);

        var message = exception.GetBaseException().Message;
        var failedAt = DateTimeOffset.UtcNow;
        var failedRecord = await PersistRecordAsync(
            record with
            {
                RuntimeState = runtimeState,
                UpdatedAt = failedAt,
                LastHealthAt = failedAt,
                LastError = message,
            },
            cancellationToken);

        await AppendLogAsync(
            record.Id,
            level: "error",
            source: source,
            message: message,
            payload: new { exception = exception.GetType().Name },
            cancellationToken);
        RecordDiagnostic(
            source: source,
            eventType: eventType,
            level: "error",
            message: message,
            record.Id,
            new Dictionary<string, string?>
            {
                ["exceptionType"] = exception.GetType().Name,
                ["runtimeState"] = runtimeState.ToString(),
            });
        _logger?.LogWarning(exception, "Plugin runtime failure for {PluginId}", record.Id);

        return failedRecord;
    }

    private async Task<PluginRecord> PersistRecordAsync(
        PluginRecord record,
        CancellationToken cancellationToken)
    {
        await _registryRepository.UpsertAsync(record, cancellationToken);
        return record;
    }

    private async Task AppendLogAsync(
        string pluginId,
        string level,
        string source,
        string message,
        object? payload,
        CancellationToken cancellationToken)
    {
        if (_logRepository is null)
        {
            return;
        }

        await _logRepository.AppendAsync(
            new PluginLogEntry(
                EntryId: Guid.CreateVersion7().ToString(),
                PluginId: pluginId,
                Level: level,
                Source: source,
                Message: message,
                Timestamp: DateTimeOffset.UtcNow,
                PayloadJson: payload is null ? null : JsonSerializer.Serialize(payload, JsonOptions)),
            cancellationToken);
    }

    private void RecordDiagnostic(
        string source,
        string eventType,
        string level,
        string message,
        string pluginId,
        IReadOnlyDictionary<string, string?>? attributes = null)
    {
        _diagnosticsService?.Record(
            new DiagnosticEvent(
                Id: Guid.CreateVersion7().ToString(),
                Source: source,
                EventType: eventType,
                Level: level,
                Message: message,
                Timestamp: DateTimeOffset.UtcNow,
                Attributes: MergePluginId(pluginId, attributes)));
    }

    private static IReadOnlyDictionary<string, string?> MergePluginId(
        string pluginId,
        IReadOnlyDictionary<string, string?>? attributes)
    {
        var merged = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["pluginId"] = pluginId,
        };

        if (attributes is not null)
        {
            foreach (var (key, value) in attributes)
            {
                merged[key] = value;
            }
        }

        return merged;
    }

    private static void ValidatePluginId(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            throw new ArgumentException("Plugin id is required.", nameof(pluginId));
        }
    }

    private sealed record HostedPluginSession(
        McpConfig Config,
        IReadOnlyList<ITool> Tools,
        DateTimeOffset StartedAt);
}
