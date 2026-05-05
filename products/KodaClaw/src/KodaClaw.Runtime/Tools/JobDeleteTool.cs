using KodaClaw.Contracts.Jobs;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.Runtime.Tools;

public sealed class JobDeleteTool : ToolBase<JobDeleteArgs>
{
    private readonly IJobRepository _jobRepository;

    public JobDeleteTool(IJobRepository jobRepository)
    {
        _jobRepository = jobRepository;
    }

    public override string Name => "job_delete";

    public override string Description =>
        "删除指定的 Job。仅允许删除 status=cancelled 的 Job，pending 状态需先 job_cancel。";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<JobDeleteArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = true,
    };

    protected override async Task<ToolResult> ExecuteAsync(
        JobDeleteArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var job = await _jobRepository.GetByIdAsync(args.JobId, cancellationToken);
        if (job == null)
            return ToolResult.Fail($"not_found: job_id 为 '{args.JobId}' 的 Job 不存在");

        if (job.Status != JobStatus.Cancelled)
            return ToolResult.Fail("cannot_delete_active_job: status 为 pending 的 Job 不允许直接删除。请先 job_cancel。");

        await _jobRepository.DeleteAsync(args.JobId, cancellationToken);

        return ToolResult.Ok(new
        {
            job_id = args.JobId,
            deleted = true,
        });
    }
}

public sealed class JobDeleteArgs
{
    [ToolParameter(Description = "Job ID")]
    public required string JobId { get; init; }
}
