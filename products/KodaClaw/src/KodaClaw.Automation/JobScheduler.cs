using KodaClaw.Contracts.Jobs;
using Microsoft.Extensions.Logging;

namespace KodaClaw.Automation;

public sealed class JobScheduler
{
    private readonly IJobRepository _repo;
    private readonly int _maxConcurrent;
    private readonly ILogger<JobScheduler>? _logger;

    public JobScheduler(IJobRepository repo, JobSchedulerOptions options, ILogger<JobScheduler>? logger = null)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        ArgumentNullException.ThrowIfNull(options);
        _maxConcurrent = options.MaxConcurrent;
        TickInterval = options.TickInterval;
        _logger = logger;
    }

    public TimeSpan TickInterval { get; }

    /// <summary>
    /// 核心调度循环：扫描 pending Job，按 FIFO 标记为 running，受 maxConcurrent 限制。
    /// 返回本 tick 启动的 Job 数量。
    /// </summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;

        // 1. 获取所有 pending Job
        var pendingJobs = await _repo.ListAsync(JobStatus.Pending, ct);

        // 2. 过滤：now >= NextRunAt，且 NextRunAt 不为 null
        var dueJobs = pendingJobs
            .Where(j => j.NextRunAt is not null && j.NextRunAt <= now)
            .OrderBy(j => j.NextRunAt)
            .ToList();

        if (dueJobs.Count == 0)
            return 0;

        // 3. 并发控制：检查当前 running 数量
        var runningJobs = await _repo.ListAsync(JobStatus.Running, ct);
        var availableSlots = _maxConcurrent - runningJobs.Count;
        if (availableSlots <= 0)
            return 0;

        var toStart = dueJobs.Take(availableSlots).ToList();

        var started = 0;
        foreach (var job in toStart)
        {
            ct.ThrowIfCancellationRequested();

            // 4a. 防竞态：重新读取最新状态
            var latest = await _repo.GetByIdAsync(job.Id, ct);
            if (latest is null || latest.Status != JobStatus.Pending)
                continue;

            // 4b. 写入 status = running
            var started_at = DateTimeOffset.UtcNow;
            var running = latest with
            {
                Status = JobStatus.Running,
                UpdatedAt = started_at,
            };

            // 4c. 如果是 recurring：预计算 next_run_at
            if (latest.Type == JobType.Recurring && latest.Cron is not null)
            {
                running = running with
                {
                    NextRunAt = AutomationCronComputer.ComputeNextRunAt(latest.Cron, started_at),
                };
            }

            await _repo.UpdateAsync(running, ct);
            started++;

            _logger?.LogDebug("Job {JobId} transitioned pending→running", latest.Id);
        }

        return started;
    }
}
