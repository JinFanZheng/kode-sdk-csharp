using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Jobs;
using KodaClaw.Runtime.Sessions;
using Kode.Agent.Sdk.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace KodaClaw.Automation;

public sealed class JobScheduler
{
    private readonly IJobRepository _repo;
    private readonly IAutomationSessionService _sessionService;
    private readonly IAutomationNotificationService? _notificationService;
    private readonly int _maxConcurrent;
    private readonly ILogger<JobScheduler>? _logger;

    public JobScheduler(
        IJobRepository repo,
        IAutomationSessionService sessionService,
        JobSchedulerOptions options,
        IAutomationNotificationService? notificationService = null,
        ILogger<JobScheduler>? logger = null)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        ArgumentNullException.ThrowIfNull(options);
        _maxConcurrent = options.MaxConcurrent;
        TickInterval = options.TickInterval;
        _notificationService = notificationService;
        _logger = logger;
    }

    public TimeSpan TickInterval { get; }

    /// <summary>
    /// 核心调度循环：扫描 pending Job，按 FIFO 标记为 running，受 maxConcurrent 限制。
    /// 然后执行看门狗检查。返回本 tick 启动的 Job 数量。
    /// </summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 1. 调度：pending → running（含错过补偿），并启动 session 执行
        var started = await SchedulePendingJobsAsync(ct);

        // 2. 看门狗：扫描 running Job，超时标记 failed
        await RunWatchdogAsync(ct);

        return started;
    }

    /// <summary>
    /// 启动时 zombie 检测：进程重启导致所有 running Session 已死，
    /// 将 persisted running Job 标记为 failed 并触发失败处理。
    /// </summary>
    public async Task DetectZombiesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var runningJobs = await _repo.ListAsync(JobStatus.Running, ct);
        if (runningJobs.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var job in runningJobs)
        {
            ct.ThrowIfCancellationRequested();

            var failedRun = new JobRunRecord(
                RunId: $"run-{now:yyyyMMddTHHmmss}",
                StartedAt: job.UpdatedAt,
                CompletedAt: now,
                Status: "failed",
                RetryCount: 0,
                Result: "zombie_process_restart",
                NextRunSet: null);

            // zombie 替换 running 记录（若已有）或追加（向后兼容无 run record 的 running Job）
            var runs = ReplaceLatestRun(job.Runs, failedRun);
            var updated = job with { Runs = runs, UpdatedAt = now };

            await ApplyFailurePolicyAsync(updated, 1, now, ct);
        }

        _logger?.LogWarning("Zombie detection completed: {Count} running jobs marked zombie", runningJobs.Count);
    }

    private async Task<int> SchedulePendingJobsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // 获取所有 pending Job（含错过补偿：next_run_at 已过期的也会被选出）
        var pendingJobs = await _repo.ListAsync(JobStatus.Pending, ct);

        var dueJobs = pendingJobs
            .Where(j => j.NextRunAt == null || j.NextRunAt <= now)
            .OrderBy(j => j.NextRunAt)
            .ToList();

        if (dueJobs.Count == 0)
            return 0;

        // 并发控制
        var runningJobs = await _repo.ListAsync(JobStatus.Running, ct);
        var availableSlots = _maxConcurrent - runningJobs.Count;
        if (availableSlots <= 0)
            return 0;

        var toStart = dueJobs.Take(availableSlots).ToList();

        var started = 0;
        foreach (var job in toStart)
        {
            ct.ThrowIfCancellationRequested();

            // 防竞态
            var latest = await _repo.GetByIdAsync(job.Id, ct);
            if (latest is null || latest.Status != JobStatus.Pending)
                continue;

            var startedAt = DateTimeOffset.UtcNow;

            // 创建 run record（retry 已预建 "running" 记录时跳过，避免重复）
            var latestRun = latest.Runs.LastOrDefault();
            var runs = latestRun?.Status == "running"
                ? latest.Runs
                : AppendRun(latest.Runs, new JobRunRecord(
                    RunId: $"run-{startedAt:yyyyMMddTHHmmss}",
                    StartedAt: startedAt,
                    CompletedAt: null,
                    Status: "running",
                    RetryCount: 0,
                    Result: null,
                    NextRunSet: null));

            var running = latest with
            {
                Status = JobStatus.Running,
                UpdatedAt = startedAt,
                Runs = runs,
            };

            // recurring：预计算 next_run_at
            if (latest.Type == JobType.Recurring && latest.Cron is not null)
            {
                running = running with
                {
                    NextRunAt = AutomationCronComputer.ComputeNextRunAt(latest.Cron, startedAt),
                };
            }

            await _repo.UpdateAsync(running, ct);
            started++;

            _logger?.LogDebug("Job {JobId} transitioned pending→running", latest.Id);

            // 启动 session 执行（fire-and-forget，每个 job 自行处理结果）
            _ = RunJobSessionAsync(running, ct);
        }

        return started;
    }

    private async Task RunJobSessionAsync(JobDefinition runningJob, CancellationToken ct)
    {
        try
        {
            var handle = await _sessionService.StartJobSessionAsync(runningJob, ct);
            AgentRunResult runResult;
            try
            {
                runResult = await handle.Agent.RunAsync(runningJob.Prompt, ct);
            }
            finally
            {
                await handle.Agent.DisposeAsync();
            }

            await CompleteJobRunAsync(runningJob, runResult, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Job {JobId} session execution failed", runningJob.Id);
            await HandleJobSessionErrorAsync(runningJob, ex, ct);
        }
    }

    /// <summary>
    /// Session 成功完成后的处理：写 RunRecord，更新状态，发送通知（§4.5-§4.6）。
    /// </summary>
    private async Task CompleteJobRunAsync(
        JobDefinition runningJob, AgentRunResult runResult, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var latest = await _repo.GetByIdAsync(runningJob.Id, ct);
        if (latest is null || latest.Status != JobStatus.Running)
            return;

        var resultText = runResult.Response?.Trim() ?? "";
        var status = runResult.Success ? "completed" : "failed";

        // 构建完成后的 run record（替换 running 记录）
        var latestRun = latest.Runs.LastOrDefault();
        DateTimeOffset? nextRunSet = null;
        if (latest.Type == JobType.Recurring && latest.Cron is not null)
        {
            nextRunSet = AutomationCronComputer.ComputeNextRunAt(latest.Cron, now);
        }
        else if (latest.Type == JobType.SelfDriven)
        {
            nextRunSet = now.AddMinutes(latest.FallbackIntervalMinutes ?? 60);
        }

        var completedRun = new JobRunRecord(
            RunId: latestRun?.RunId ?? $"run-{now:yyyyMMddTHHmmss}",
            StartedAt: latestRun?.StartedAt ?? now,
            CompletedAt: now,
            Status: status,
            RetryCount: latestRun?.RetryCount ?? 0,
            Result: resultText.Length > 2000 ? resultText[..2000] : resultText,
            NextRunSet: nextRunSet);

        var runs = ReplaceLatestRun(latest.Runs, completedRun);

        JobDefinition updated;
        if (!runResult.Success)
        {
            // 失败：走重试逻辑
            var retryCount = (latestRun?.RetryCount ?? 0) + 1;
            updated = latest with { Runs = runs, UpdatedAt = now };
            await ApplyFailurePolicyAsync(updated, retryCount, now, ct);
            return;
        }

        // 成功
        if (latest.Type == JobType.OneShot)
        {
            updated = latest with
            {
                Status = JobStatus.Completed,
                NextRunAt = null,
                Runs = runs,
                UpdatedAt = now,
            };
        }
        else if (latest.Type == JobType.Recurring)
        {
            updated = latest with
            {
                Status = JobStatus.Pending,
                NextRunAt = nextRunSet,
                Runs = runs,
                UpdatedAt = now,
            };
        }
        else // SelfDriven
        {
            updated = latest with
            {
                Status = JobStatus.Pending,
                NextRunAt = nextRunSet,
                Runs = runs,
                UpdatedAt = now,
            };
        }

        await _repo.UpdateAsync(updated, ct);

        _logger?.LogInformation("Job {JobId} completed: {Status}", latest.Id, status);

        // §4.6：成功时推送通知到渠道
        if (updated.DeliveryMode == JobDeliveryMode.Auto && updated.Channels.Count > 0)
        {
            await PushJobResultToChannelsAsync(updated, resultText, ct);
        }
    }

    /// <summary>
    /// Session 异常失败处理。
    /// </summary>
    private async Task HandleJobSessionErrorAsync(
        JobDefinition runningJob, Exception ex, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var latest = await _repo.GetByIdAsync(runningJob.Id, ct);
        if (latest is null || latest.Status != JobStatus.Running)
            return;

        var latestRun = latest.Runs.LastOrDefault();
        var failedRun = new JobRunRecord(
            RunId: latestRun?.RunId ?? $"run-{now:yyyyMMddTHHmmss}",
            StartedAt: latestRun?.StartedAt ?? now,
            CompletedAt: now,
            Status: "failed",
            RetryCount: latestRun?.RetryCount ?? 0,
            Result: $"session_error: {ex.GetBaseException().Message}",
            NextRunSet: null);

        var runs = ReplaceLatestRun(latest.Runs, failedRun);
        var retryCount = (latestRun?.RetryCount ?? 0) + 1;
        var updated = latest with { Runs = runs, UpdatedAt = now };

        await ApplyFailurePolicyAsync(updated, retryCount, now, ct);
    }

    /// <summary>
    /// 推送 Job 结果到配置的渠道（§4.6）。
    /// </summary>
    private async Task PushJobResultToChannelsAsync(
        JobDefinition job, string resultText, CancellationToken ct)
    {
        if (_notificationService is null || job.Channels.Count == 0)
            return;

        try
        {
            var message = string.IsNullOrWhiteSpace(resultText)
                ? $"Job '{job.Name}' completed with no output."
                : resultText;

            await _notificationService.PushAsync(job.Channels, message, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Job {JobId} channel push failed for {ChannelCount} channels",
                job.Id, job.Channels.Count);
        }
    }

    private async Task RunWatchdogAsync(CancellationToken ct)
    {
        var runningJobs = await _repo.ListAsync(JobStatus.Running, ct);
        if (runningJobs.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var job in runningJobs)
        {
            ct.ThrowIfCancellationRequested();

            // 确定 started_at：优先取最新 run 的 StartedAt，否则用 UpdatedAt
            var latestRun = job.Runs.LastOrDefault();
            var startedAt = latestRun?.StartedAt ?? job.UpdatedAt;
            var deadline = startedAt + TimeSpan.FromMinutes(job.TimeoutMinutes);

            if (now < deadline) continue;

            // 超时：标记 run 为 failed
            var failedRun = new JobRunRecord(
                RunId: latestRun?.RunId ?? $"run-{now:yyyyMMddTHHmmss}",
                StartedAt: latestRun?.StartedAt ?? startedAt,
                CompletedAt: now,
                Status: "failed",
                RetryCount: latestRun?.RetryCount ?? 0,
                Result: "timeout",
                NextRunSet: null);

            var runs = ReplaceLatestRun(job.Runs, failedRun);
            var retryCount = (latestRun?.RetryCount ?? 0) + 1;
            var updated = job with { Runs = runs, UpdatedAt = now };

            await ApplyFailurePolicyAsync(updated, retryCount, now, ct);

            _logger?.LogWarning("Job {JobId} timed out after {TimeoutMinutes}min", job.Id, job.TimeoutMinutes);
        }
    }

    /// <summary>
    /// 应用失败处理策略：retry / 连续失败检测 / 最终失败。
    /// </summary>
    private async Task ApplyFailurePolicyAsync(
        JobDefinition job, int retryCount, DateTimeOffset now, CancellationToken ct)
    {
        if (retryCount <= job.MaxRetries)
        {
            // 自动重试：立即重新调度
            var retryRun = new JobRunRecord(
                RunId: $"run-{now:yyyyMMddTHHmmss}-retry{retryCount}",
                StartedAt: now,
                CompletedAt: null,
                Status: "running",
                RetryCount: retryCount,
                Result: null,
                NextRunSet: null);

            var runs = AppendRun(job.Runs, retryRun);
            job = job with
            {
                Status = JobStatus.Pending,
                NextRunAt = now,
                Runs = runs,
                UpdatedAt = now,
            };

            _logger?.LogInformation("Job {JobId} retry {RetryCount}/{MaxRetries}", job.Id, retryCount, job.MaxRetries);
        }
        else
        {
            // 重试耗尽：计数连续失败
            var consecutiveFailures = CountConsecutiveFailures(job.Runs);

            if (job.Type == JobType.Recurring && consecutiveFailures < job.MaxConsecutiveFailures)
            {
                // 本次 run 失败，但 Job 继续按 cron 调度
                var nextRunAt = job.Cron is not null
                    ? AutomationCronComputer.ComputeNextRunAt(job.Cron, now)
                    : now.AddHours(1);

                job = job with
                {
                    Status = JobStatus.Pending,
                    NextRunAt = nextRunAt,
                    UpdatedAt = now,
                };
            }
            else
            {
                // 最终失败
                job = job with { Status = JobStatus.Failed, UpdatedAt = now };
                _logger?.LogError("Job {JobId} failed after {RetryCount} retries, {ConsecutiveFailures} consecutive failures",
                    job.Id, retryCount, consecutiveFailures);
            }
        }

        await _repo.UpdateAsync(job, ct);
    }

    /// <summary>
    /// 从 run_history 尾部计数连续失败的 run（status = "failed"）。
    /// </summary>
    private static int CountConsecutiveFailures(IReadOnlyList<JobRunRecord> runs)
    {
        var count = 0;
        for (var i = runs.Count - 1; i >= 0; i--)
        {
            if (runs[i].Status == "failed")
                count++;
            else
                break;
        }
        return count;
    }

    private static IReadOnlyList<JobRunRecord> AppendRun(
        IReadOnlyList<JobRunRecord> runs, JobRunRecord newRun)
    {
        var list = runs.ToList();
        list.Add(newRun);
        if (list.Count > 50) list.RemoveAt(0);
        return list;
    }

    private static IReadOnlyList<JobRunRecord> ReplaceLatestRun(
        IReadOnlyList<JobRunRecord> runs, JobRunRecord newRun)
    {
        var list = runs.ToList();
        if (list.Count > 0)
            list[list.Count - 1] = newRun;
        else
            list.Add(newRun);
        return list;
    }
}
