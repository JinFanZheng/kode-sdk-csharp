using System.Threading.Channels;
using KodaClaw.Contracts.Workspace;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KodaClaw.Workspace.Heartbeat;

internal sealed class HeartbeatFileWatcherHostedService : BackgroundService
{
    private readonly IHeartbeatSyncService _syncService;
    private readonly IWorkspaceService _workspaceService;
    private readonly ILogger<HeartbeatFileWatcherHostedService>? _logger;

    public HeartbeatFileWatcherHostedService(
        IHeartbeatSyncService syncService,
        IWorkspaceService workspaceService,
        ILogger<HeartbeatFileWatcherHostedService>? logger = null)
    {
        _syncService = syncService;
        _workspaceService = workspaceService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initResult = await _syncService.SyncAsync(stoppingToken);
        if (initResult.CompilationFailed)
        {
            _logger?.LogWarning(
                "HEARTBEAT.md initial sync resulted in compilation failure.");
        }

        var watchDir = Path.Combine(
            _workspaceService.RootPath,
            KodaClawWorkspaceLayout.WorkspaceDirectory);

        if (!Directory.Exists(watchDir))
        {
            _logger?.LogDebug(
                "Workspace directory {Dir} does not exist, skipping HEARTBEAT.md watcher.", watchDir);
            return;
        }

        var channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        using var watcher = new FileSystemWatcher(watchDir, KodaClawWorkspaceLayout.HeartbeatFile)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };

        watcher.Changed += (_, _) => channel.Writer.TryWrite(true);
        watcher.Created += (_, _) => channel.Writer.TryWrite(true);

        _logger?.LogInformation(
            "Watching {Dir}/HEARTBEAT.md for changes.", watchDir);

        await foreach (var signal in channel.Reader.ReadAllAsync(stoppingToken))
        {
            _ = signal;
            await Task.Delay(500, stoppingToken);

            // Drain any additional signals that arrived during debounce
            while (channel.Reader.TryRead(out _)) { }

            _logger?.LogDebug(
                "HEARTBEAT.md change detected, triggering sync.");
            var syncResult = await _syncService.SyncAsync(stoppingToken);
            if (syncResult.CompilationFailed)
            {
                _logger?.LogWarning(
                    "HEARTBEAT.md file change sync resulted in compilation failure.");
            }
        }
    }
}
