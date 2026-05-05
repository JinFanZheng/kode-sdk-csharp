using KodaClaw.Contracts.Jobs;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.Runtime.Tools;

public sealed class JobStatusTool : ToolBase<JobStatusArgs>
{
    private readonly IJobRepository _jobRepository;

    public JobStatusTool(IJobRepository jobRepository)
    {
        _jobRepository = jobRepository;
    }

    public override string Name => "job_status";

    public override string Description =>
        "查看指定 Job 的完整信息，包括所有配置和运行记录。";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<JobStatusArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = true,
        RequiresApproval = false,
    };

    protected override async Task<ToolResult> ExecuteAsync(
        JobStatusArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var job = await _jobRepository.GetByIdAsync(args.JobId, cancellationToken);
        if (job == null)
            return ToolResult.Fail($"not_found: job_id 为 '{args.JobId}' 的 Job 不存在");

        return ToolResult.Ok(new
        {
            id = job.Id,
            name = job.Name,
            type = job.Type.ToString().ToLowerInvariant(),
            prompt = job.Prompt,
            status = job.Status.ToString().ToLowerInvariant(),
            schema_version = job.SchemaVersion,
            cron = job.Cron,
            next_run_at = job.NextRunAt?.ToString("O"),
            fallback_interval_minutes = job.FallbackIntervalMinutes,
            concurrency_key = job.ConcurrencyKey,
            runs = job.Runs.Select(r => new
            {
                run_id = r.RunId,
                started_at = r.StartedAt.ToString("O"),
                completed_at = r.CompletedAt?.ToString("O"),
                status = r.Status,
                retry_count = r.RetryCount,
                result = r.Result,
                next_run_set = r.NextRunSet?.ToString("O"),
            }),
            run_count = job.RunCount,
            channels = job.Channels,
            delivery_mode = job.DeliveryMode.ToString().ToLowerInvariant(),
            timeout_minutes = job.TimeoutMinutes,
            max_retries = job.MaxRetries,
            max_consecutive_failures = job.MaxConsecutiveFailures,
            created_at = job.CreatedAt.ToString("O"),
            updated_at = job.UpdatedAt.ToString("O"),
        });
    }
}

public sealed class JobStatusArgs
{
    [ToolParameter(Description = "Job ID")]
    public required string JobId { get; init; }
}
