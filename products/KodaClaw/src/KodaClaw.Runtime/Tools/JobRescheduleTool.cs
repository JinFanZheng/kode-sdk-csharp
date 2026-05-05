using KodaClaw.Contracts.Jobs;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.Runtime.Tools;

public sealed class JobRescheduleTool : ToolBase<JobRescheduleArgs>
{
    private readonly IJobRepository _jobRepository;

    public JobRescheduleTool(IJobRepository jobRepository)
    {
        _jobRepository = jobRepository;
    }

    public override string Name => "job_reschedule";

    public override string Description =>
        "重新调度 self-driven Job 的下次执行时间。仅当前正在执行的 self-driven Job session 可调用。" +
        "next_run_at 必须在当前时间 +5 分钟到 +30 天之间。";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<JobRescheduleArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = false,
    };

    protected override async Task<ToolResult> ExecuteAsync(
        JobRescheduleArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var job = await _jobRepository.GetByIdAsync(args.JobId, cancellationToken);
        if (job == null)
            return ToolResult.Fail($"not_found: job_id 为 '{args.JobId}' 的 Job 不存在");

        if (job.Type != JobType.SelfDriven)
            return ToolResult.Fail("reschedule_not_allowed: 仅 self-driven Job 可调用 reschedule，且只有当前正在执行的 run 可以");

        var now = DateTimeOffset.UtcNow;

        if (!DateTimeOffset.TryParse(args.NextRunAt, out var nextRunAt))
            return ToolResult.Fail($"无效的 next_run_at 格式: '{args.NextRunAt}'，需使用 ISO 8601 格式");

        if (nextRunAt < now.AddMinutes(5))
            return ToolResult.Fail("reschedule_not_allowed: next_run_at 必须 >= 当前时间 + 5 分钟");

        if (nextRunAt > now.AddDays(30))
            return ToolResult.Fail("reschedule_not_allowed: next_run_at 必须 <= 当前时间 + 30 天");

        var updated = job with
        {
            NextRunAt = nextRunAt,
            UpdatedAt = now,
        };

        await _jobRepository.UpdateAsync(updated, cancellationToken);

        return ToolResult.Ok(new
        {
            job_id = job.Id,
            next_run_at = nextRunAt.ToString("O"),
        });
    }
}

public sealed class JobRescheduleArgs
{
    [ToolParameter(Description = "Job ID")]
    public required string JobId { get; init; }

    [ToolParameter(Description = "下次执行时间 ISO 8601，必须 >= 当前时间+5min 且 <= 当前时间+30d")]
    public required string NextRunAt { get; init; }
}
