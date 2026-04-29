using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KodaClaw.Automation.Scheduler;

public sealed class AutomationSchedulerHostedService : BackgroundService
{
    private readonly IAutomationScheduler _scheduler;
    private readonly AutomationSchedulerOptions _options;
    private readonly ILogger<AutomationSchedulerHostedService>? _logger;

    public AutomationSchedulerHostedService(
        IAutomationScheduler scheduler,
        AutomationSchedulerOptions options,
        ILogger<AutomationSchedulerHostedService>? logger = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger?.LogDebug("Automation scheduler host is disabled by configuration.");
            return;
        }

        using var timer = new PeriodicTimer(_options.PollInterval);
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
                _logger?.LogWarning(ex, "Automation scheduler tick failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
