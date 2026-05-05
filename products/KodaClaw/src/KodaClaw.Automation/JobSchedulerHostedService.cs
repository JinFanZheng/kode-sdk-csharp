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
        // 延迟启动：等 Host 完全就绪后再开始调度
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        // 启动时 zombie 检测
        try
        {
            await _scheduler.DetectZombiesAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "JobScheduler zombie detection failed.");
        }

        // 主循环：整个包在 try-catch 中，包括 PeriodicTimer 的异常
        try
        {
            using var timer = new PeriodicTimer(_scheduler.TickInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await _scheduler.TickAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogWarning(ex, "JobScheduler tick failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭——Host 取消 token 时 PeriodicTimer 抛出
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "JobScheduler loop failed unexpectedly.");
        }
    }
}
