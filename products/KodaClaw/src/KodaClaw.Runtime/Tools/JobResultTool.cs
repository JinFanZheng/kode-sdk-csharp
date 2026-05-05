using KodaClaw.Contracts.Jobs;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.Runtime.Tools;

public sealed class JobResultTool : ToolBase<JobResultArgs>
{
    private readonly IJobRepository _jobRepository;

    public JobResultTool(IJobRepository jobRepository)
    {
        _jobRepository = jobRepository;
    }

    public override string Name => "job_result";

    public override string Description =>
        "查看 Job 的运行结果。Phase 1a 中 Job 尚未被调度执行，始终返回无结果。";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<JobResultArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = true,
        RequiresApproval = false,
    };

    protected override async Task<ToolResult> ExecuteAsync(
        JobResultArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var job = await _jobRepository.GetByIdAsync(args.JobId, cancellationToken);
        if (job == null)
            return ToolResult.Fail($"not_found: job_id 为 '{args.JobId}' 的 Job 不存在");

        return ToolResult.Ok(new
        {
            job_id = job.Id,
            has_result = false,
            message = "该 Job 尚未执行",
        });
    }
}

public sealed class JobResultArgs
{
    [ToolParameter(Description = "Job ID")]
    public required string JobId { get; init; }

    [ToolParameter(Description = "Run ID，不传则返回最新的 run", Required = false)]
    public string? RunId { get; init; }
}
