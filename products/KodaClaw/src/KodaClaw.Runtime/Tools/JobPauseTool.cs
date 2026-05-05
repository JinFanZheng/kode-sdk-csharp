using KodaClaw.Contracts.Jobs;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.Runtime.Tools;

public sealed class JobPauseTool : ToolBase<JobPauseArgs>
{
    private readonly IJobRepository _jobRepository;

    public JobPauseTool(IJobRepository jobRepository)
    {
        _jobRepository = jobRepository;
    }

    public override string Name => "job_pause";

    public override string Description =>
        "暂停一个 pending 状态的 Job。暂停后调度器将跳过该 Job，直到手动 resume。" +
        "只能暂停 pending 状态的 Job，running 中的 Job 需等待当前 run 完成。";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<JobPauseArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = true,
    };

    protected override async Task<ToolResult> ExecuteAsync(
        JobPauseArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var job = await _jobRepository.GetByIdAsync(args.JobId, cancellationToken);
        if (job == null)
            return ToolResult.Fail($"not_found: job_id 为 '{args.JobId}' 的 Job 不存在");

        if (job.Status != JobStatus.Pending)
            return ToolResult.Fail($"pause_not_allowed: 只能暂停 pending 状态的 Job，当前状态为 {JobCreateTool.StatusToString(job.Status)}");

        var previousStatus = JobCreateTool.StatusToString(job.Status);
        var updated = job with
        {
            Status = JobStatus.Paused,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await _jobRepository.UpdateAsync(updated, cancellationToken);

        return ToolResult.Ok(new
        {
            job_id = job.Id,
            previous_status = previousStatus,
            new_status = "paused",
        });
    }
}

public sealed class JobPauseArgs
{
    [ToolParameter(Description = "Job ID")]
    public required string JobId { get; init; }
}
