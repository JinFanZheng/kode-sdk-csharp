using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Workspace;

namespace KodaClaw.Runtime;

/// <summary>
/// Post-consolidation hook called after the Nightly Memory Consolidation automation completes.
/// Commits workspace changes made by the Agent during consolidation.
/// Demotion is now handled by the Agent via HEARTBEAT prompts (semantic judgment),
/// not by code-driven time-based thresholds.
/// </summary>
public interface IMemoryConsolidationService
{
    Task PostConsolidationAsync(CancellationToken cancellationToken = default);
}

public sealed class MemoryConsolidationService : IMemoryConsolidationService
{
    private readonly IWorkspaceService _workspaceService;
    private readonly IDiagnosticsService? _diagnosticsService;

    public MemoryConsolidationService(
        IWorkspaceService workspaceService,
        IDiagnosticsService? diagnosticsService = null)
    {
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _diagnosticsService = diagnosticsService;
    }

    public async Task PostConsolidationAsync(CancellationToken cancellationToken = default)
    {
        await _workspaceService.TryCommitWorkspaceAsync(
            "workspace(memory)[agent]: post-consolidation commit",
            cancellationToken);

        // Record consolidation timestamp for health monitoring
        try
        {
            var appConfig = await _workspaceService.LoadAppConfigAsync(cancellationToken);
            await _workspaceService.SaveAppConfigAsync(
                appConfig with { LastConsolidationAt = DateTimeOffset.UtcNow },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _diagnosticsService?.Record(new DiagnosticEvent(
                Id: Guid.NewGuid().ToString("N"),
                Source: "koda.runtime.memory_consolidation",
                EventType: "memory.consolidation_timestamp_failed",
                Level: "warning",
                Message: ex.GetBaseException().Message,
                Timestamp: DateTimeOffset.UtcNow,
                SessionId: null));
        }

        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: "koda.runtime.memory_consolidation",
            EventType: "memory.post_consolidation_committed",
            Level: "info",
            Message: "Post-consolidation workspace commit completed",
            Timestamp: DateTimeOffset.UtcNow,
            SessionId: null));
    }
}
