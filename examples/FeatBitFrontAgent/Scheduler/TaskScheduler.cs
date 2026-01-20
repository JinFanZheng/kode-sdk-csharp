namespace Kode.Agent.WebApiAssistant.Scheduler;

/// <summary>
/// 调度任务
/// </summary>
public class ScheduledTask
{
    public string TaskId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public Func<Task> Action { get; set; } = () => Task.CompletedTask;
    public bool IsEnabled { get; set; } = true;
    public DateTime? LastRun { get; set; }
    public DateTime? NextRun { get; set; }
}

/// <summary>
/// 任务调度器
/// </summary>
public class TaskScheduler : BackgroundService
{
    private readonly List<ScheduledTask> _tasks = new();
    private readonly ILogger<TaskScheduler> _logger;
    private readonly PeriodicTimer _timer;

    public TaskScheduler(ILogger<TaskScheduler> logger)
    {
        _logger = logger;
        _timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// 注册任务
    /// </summary>
    public void RegisterTask(ScheduledTask task)
    {
        task.TaskId = Guid.NewGuid().ToString("N");
        task.NextRun = CalculateNextRun(task.CronExpression);
        _tasks.Add(task);
        _logger.LogInformation("Registered task: {Name}, next run: {NextRun}", task.Name, task.NextRun);
    }

    /// <summary>
    /// 移除任务
    /// </summary>
    public void RemoveTask(string taskId)
    {
        var task = _tasks.FirstOrDefault(t => t.TaskId == taskId);
        if (task != null)
        {
            _tasks.Remove(task);
            _logger.LogInformation("Removed task: {Name}", task.Name);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TaskScheduler started");

        while (await _timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = DateTime.UtcNow;

            foreach (var task in _tasks.Where(t => t.IsEnabled && t.NextRun <= now))
            {
                try
                {
                    _logger.LogInformation("Executing task: {Name}", task.Name);
                    await task.Action();
                    task.LastRun = now;
                    task.NextRun = CalculateNextRun(task.CronExpression);
                    _logger.LogInformation("Task completed: {Name}, next run: {NextRun}",
                        task.Name, task.NextRun);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Task failed: {Name}", task.Name);
                }
            }
        }
    }

    /// <summary>
    /// 计算下次运行时间（简化版 Cron 解析）
    /// </summary>
    private static DateTime CalculateNextRun(string cronExpression)
    {
        // 简化实现：只支持 "0 * * * *" 格式的分钟级 Cron
        // 完整实现需要 Cron 解析库
        var now = DateTime.UtcNow;
        return now.AddMinutes(1).AddSeconds(-now.Second);
    }
}
