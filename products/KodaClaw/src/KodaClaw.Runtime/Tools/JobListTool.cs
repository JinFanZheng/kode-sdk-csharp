using KodaClaw.Contracts.Jobs;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.Runtime.Tools;

public sealed class JobListTool : ToolBase<JobListArgs>
{
    private readonly IJobRepository _jobRepository;

    public JobListTool(IJobRepository jobRepository)
    {
        _jobRepository = jobRepository;
    }

    public override string Name => "job_list";

    public override string Description =>
        "列出所有 Job，可按状态过滤。返回简要信息（id、name、type、status、next_run_at、run_count）。";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<JobListArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = true,
        RequiresApproval = false,
    };

    protected override async Task<ToolResult> ExecuteAsync(
        JobListArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        JobStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(args.Status))
        {
            if (!Enum.TryParse<JobStatus>(args.Status, ignoreCase: true, out var parsed))
                return ToolResult.Fail($"无效的 status: '{args.Status}'，必须是 pending / cancelled");
            statusFilter = parsed;
        }

        var jobs = await _jobRepository.ListAsync(statusFilter, cancellationToken);

        var items = jobs.Select(j => new
        {
            id = j.Id,
            name = j.Name,
            type = j.Type.ToString().ToLowerInvariant(),
            status = j.Status.ToString().ToLowerInvariant(),
            next_run_at = j.NextRunAt?.ToString("O"),
            run_count = j.RunCount,
        }).ToList();

        return ToolResult.Ok(new { jobs = items, total = items.Count });
    }
}

public sealed class JobListArgs
{
    [ToolParameter(Description = "按状态过滤：pending / cancelled。不传则返回全部", Required = false)]
    public string? Status { get; init; }
}
