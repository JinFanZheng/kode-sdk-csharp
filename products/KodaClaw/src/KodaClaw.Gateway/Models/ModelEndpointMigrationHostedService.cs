using KodaClaw.Contracts;
using KodaClaw.Storage.Json.Migration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KodaClaw.Gateway;

/// <summary>
/// Runs the one-time ModelEndpoint → ProviderAccount + AccountModel migration at startup.
///
/// Must register BEFORE <see cref="ConfigBootstrapService"/> and
/// <see cref="ModelRegistrySeedService"/> so legacy data on disk is lifted into
/// the new hierarchy before those services see the repository as "empty" and
/// skip their seed paths.
///
/// The migration itself is idempotent (<see cref="ModelEndpointMigrationService.MigrateIfNeededAsync"/>
/// early-outs when <c>config/accounts/</c> already exists or <c>config/models/</c> is absent),
/// so re-runs are safe and near-zero cost.
/// </summary>
internal sealed class ModelEndpointMigrationHostedService : IHostedService
{
    private readonly string _workspaceRoot;
    private readonly IProviderAccountRepository _repo;
    private readonly IDiagnosticsService? _diagnostics;
    private readonly ILogger<ModelEndpointMigrationHostedService>? _logger;

    public ModelEndpointMigrationHostedService(
        string workspaceRoot,
        IProviderAccountRepository repo,
        IDiagnosticsService? diagnostics = null,
        ILogger<ModelEndpointMigrationHostedService>? logger = null)
    {
        _workspaceRoot = workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _diagnostics = diagnostics;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var service = new ModelEndpointMigrationService(_workspaceRoot, _repo, _diagnostics);
            await service.MigrateIfNeededAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Migration failure must not block startup — log and continue so the user
            // can at least reach the Setup Wizard and reconfigure by hand.
            _logger?.LogError(
                ex,
                "ModelEndpointMigrationHostedService: migration failed; continuing startup. "
                + "Legacy data remains under config/models/ and can be inspected manually.");
            _diagnostics?.Record(new DiagnosticEvent(
                Id: Guid.NewGuid().ToString("N"),
                Source: "model_migration",
                EventType: "migration_failed",
                Level: "Error",
                Message: ex.Message,
                Timestamp: DateTimeOffset.UtcNow,
                Attributes: new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                }));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
