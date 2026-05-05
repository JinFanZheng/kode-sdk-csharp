using KodaClaw.Contracts.Jobs;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.Runtime.Tools;

public sealed class JobCancelTool : ToolBase<JobCancelArgs>
{
    private readonly IJobRepository _jobRepository;

    public JobCancelTool(IJobRepository jobRepository)
    {
        _jobRepository = jobRepository;
    }

    public override string Name => "job_cancel";

    public override string Description =>
        "取消指定的 Job。将状态从 pending 改为 cancelled。已取消的 Job 再次取消不报错。";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<JobCancelArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = true,
    };

    protected override async Task<ToolResult> ExecuteAsync(
        JobCancelArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var job = await _jobRepository.GetByIdAsync(args.JobId, cancellationToken);
        if (job == null)
            return ToolResult.Fail($"not_found: job_id 为 '{args.JobId}' 的 Job 不存在");

        var previousStatus = JobCreateTool.StatusToString(job.Status);

        if (job.Status == JobStatus.Cancelled)
        {
            return ToolResult.Ok(new
            {
                job_id = args.JobId,
                previous_status = previousStatus,
                new_status = previousStatus,
                message = "Job 已处于 cancelled 状态",
            });
        }

        var cancelled = job with { Status = JobStatus.Cancelled };
        await _jobRepository.UpdateAsync(cancelled, cancellationToken);

        return ToolResult.Ok(new
        {
            job_id = args.JobId,
            previous_status = previousStatus,
            new_status = "cancelled",
        });
    }
}

public sealed class JobCancelArgs
{
    [ToolParameter(Description = "Job ID")]
    public required string JobId { get; init; }
}
