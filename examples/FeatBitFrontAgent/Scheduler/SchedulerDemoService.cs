namespace Kode.Agent.WebApiAssistant.Scheduler;

/// <summary>
/// 任务调度器演示服务 - 展示如何使用 TaskScheduler 注册定时任务
/// </summary>
public class SchedulerDemoService
{
    private readonly TaskScheduler _scheduler;
    private readonly ILogger<SchedulerDemoService> _logger;

    public SchedulerDemoService(TaskScheduler scheduler, ILogger<SchedulerDemoService> logger)
    {
        _scheduler = scheduler;
        _logger = logger;
        RegisterDemoTasks();
    }

    /// <summary>
    /// 注册演示任务
    /// </summary>
    private void RegisterDemoTasks()
    {
        // 示例 1: 每 5 分钟记录一次健康状态
        _scheduler.RegisterTask(new ScheduledTask
        {
            Name = "HealthCheck",
            CronExpression = "*/5 * * * *",  // 每 5 分钟
            Action = async () =>
            {
                var memoryUsage = GC.GetTotalMemory(false) / 1024 / 1024;
                _logger.LogInformation("[HealthCheck] System healthy. Memory: {MemoryMB}MB", memoryUsage);

                // 这里可以添加更多健康检查逻辑
                // 例如：检查数据库连接、外部 API 可用性等
                await Task.CompletedTask;
            }
        });

        // 示例 2: 每小时清理临时文件
        _scheduler.RegisterTask(new ScheduledTask
        {
            Name = "CleanupTempFiles",
            CronExpression = "0 * * * *",  // 每小时
            Action = async () =>
            {
                try
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), "kode-agent");
                    if (Directory.Exists(tempDir))
                    {
                        var oldFiles = Directory.GetFiles(tempDir, "*.*", SearchOption.AllDirectories)
                            .Where(f => File.GetCreationTime(f) < DateTime.Now.AddDays(-1));

                        var count = 0;
                        foreach (var file in oldFiles)
                        {
                            try
                            {
                                File.Delete(file);
                                count++;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "[Cleanup] Failed to delete {File}", file);
                            }
                        }

                        if (count > 0)
                        {
                            _logger.LogInformation("[Cleanup] Removed {Count} old temporary files", count);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Cleanup] Error during temp file cleanup");
                }
                await Task.CompletedTask;
            }
        });

        // 示例 3: 每天凌晨 2 点执行数据统计（模拟）
        _scheduler.RegisterTask(new ScheduledTask
        {
            Name = "DailyStats",
            CronExpression = "0 2 * * *",  // 每天凌晨 2 点
            Action = async () =>
            {
                _logger.LogInformation("[DailyStats] Running daily statistics at {Time}", DateTime.Now);

                // 这里可以添加数据统计逻辑
                // 例如：统计当日请求数、活跃用户数、工具调用次数等
                await Task.Delay(100);  // 模拟处理
                _logger.LogInformation("[DailyStats] Statistics completed");
            }
        });

        _logger.LogInformation("[SchedulerDemo] Registered {Count} demo tasks", 3);
    }
}
