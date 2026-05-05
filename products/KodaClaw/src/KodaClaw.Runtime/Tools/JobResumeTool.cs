using KodaClaw.Contracts.Jobs;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.Runtime.Tools;

public sealed class JobResumeTool : ToolBase<JobResumeArgs>
{
    private readonly IJobRepository _jobRepository;

    public JobResumeTool(IJobRepository jobRepository)
    {
        _jobRepository = jobRepository;
    }

    public override string Name => "job_resume";

    public override string Description =>
        "恢复一个 paused 状态的 Job。恢复后状态变回 pending，调度器将在下个 tick 重新调度。" +
        "如果 next_run_at 已过期，调度器会立即触发执行。";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<JobResumeArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = true,
    };

    protected override async Task<ToolResult> ExecuteAsync(
        JobResumeArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var job = await _jobRepository.GetByIdAsync(args.JobId, cancellationToken);
        if (job == null)
            return ToolResult.Fail($"not_found: job_id 为 '{args.JobId}' 的 Job 不存在");

        if (job.Status != JobStatus.Paused)
            return ToolResult.Fail($"resume_not_allowed: 只能 resume paused 状态的 Job，当前状态为 {JobCreateTool.StatusToString(job.Status)}");

        var previousStatus = JobCreateTool.StatusToString(job.Status);
        var updated = job with
        {
            Status = JobStatus.Pending,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await _jobRepository.UpdateAsync(updated, cancellationToken);

        return ToolResult.Ok(new
        {
            job_id = job.Id,
            previous_status = previousStatus,
            new_status = "pending",
        });
    }
}

public sealed class JobResumeArgs
{
    [ToolParameter(Description = "Job ID")]
    public required string JobId { get; init; }
}
