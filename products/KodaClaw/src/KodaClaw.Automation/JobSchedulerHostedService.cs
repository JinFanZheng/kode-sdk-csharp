using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KodaClaw.Automation;

public sealed class JobSchedulerHostedService : BackgroundService
{
    private readonly JobScheduler _scheduler;
    private readonly ILogger<JobSchedulerHostedService>? _logger;

    public JobSchedulerHostedService(JobScheduler scheduler, ILogger<JobSchedulerHostedService>? logger = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_scheduler.TickInterval);
        do
        {
            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                await _scheduler.TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "JobScheduler tick failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
