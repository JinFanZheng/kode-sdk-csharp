using KodaClaw.Automation;
using KodaClaw.Contracts.Jobs;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.Runtime.Tools;

public sealed class JobCreateTool : ToolBase<JobCreateArgs>
{
    private readonly IJobRepository _jobRepository;

    public JobCreateTool(IJobRepository jobRepository)
    {
        _jobRepository = jobRepository;
    }

    public override string Name => "job_create";

    public override string Description =>
        "创建一个新的 Job（定时任务）。支持 one-shot（单次）、recurring（周期 cron）、" +
        "self-driven（自驱动）三种类型。创建后状态为 pending，需手动 job_cancel 取消。";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<JobCreateArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = true,
    };

    protected override async Task<ToolResult> ExecuteAsync(
        JobCreateArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        // Parse type
        if (!TryParseJobType(args.Type, out var jobType))
            return ToolResult.Fail($"无效的 type: '{args.Type}'，必须是 one-shot / recurring / self-driven");

        // Parse delivery mode
        var deliveryMode = JobDeliveryMode.None;
        if (!string.IsNullOrWhiteSpace(args.DeliveryMode) &&
            !TryParseJobDeliveryMode(args.DeliveryMode, out deliveryMode))
            return ToolResult.Fail($"无效的 delivery_mode: '{args.DeliveryMode}'，必须是 auto / approval / none");

        // Parse next_run_at
        DateTimeOffset? nextRunAt = null;
        if (!string.IsNullOrWhiteSpace(args.NextRunAt))
        {
            if (!DateTimeOffset.TryParse(args.NextRunAt, out var parsed))
                return ToolResult.Fail($"无效的 next_run_at 格式: '{args.NextRunAt}'，需使用 ISO 8601 格式");
            nextRunAt = parsed;
        }

        // Default for one-shot: next_run_at = now
        if (jobType == JobType.OneShot && nextRunAt == null)
            nextRunAt = DateTimeOffset.UtcNow;

        // Default for recurring: compute next cron time
        if (jobType == JobType.Recurring && nextRunAt == null && !string.IsNullOrWhiteSpace(args.Cron))
            nextRunAt = AutomationCronComputer.ComputeNextRunAt(args.Cron, DateTimeOffset.UtcNow);

        var now = DateTimeOffset.UtcNow;

        var job = new JobDefinition
        {
            Name = args.Name ?? "",
            Type = jobType,
            Prompt = args.Prompt ?? "",
            Cron = args.Cron,
            NextRunAt = nextRunAt,
            FallbackIntervalMinutes = args.FallbackIntervalMinutes,
            Channels = args.Channels ?? [],
            DeliveryMode = deliveryMode,
            TimeoutMinutes = args.TimeoutMinutes ?? 10,
            MaxRetries = args.MaxRetries ?? 1,
            MaxConsecutiveFailures = args.MaxConsecutiveFailures ?? 10,
            ConcurrencyKey = "",
        };

        var errors = job.ValidateForCreate(now);
        if (errors.Count > 0)
            return ToolResult.Fail($"validation_failed: {errors[0]}");

        var created = await _jobRepository.CreateAsync(job, cancellationToken);

        return ToolResult.Ok(new
        {
            job_id = created.Id,
            name = created.Name,
            type = created.Type.ToString(),
            status = created.Status.ToString(),
            created_at = created.CreatedAt.ToString("O"),
        });
    }

    internal static bool TryParseJobType(string type, out JobType result)
    {
        if (string.Equals(type, "one-shot", StringComparison.OrdinalIgnoreCase))
        { result = JobType.OneShot; return true; }
        if (string.Equals(type, "recurring", StringComparison.OrdinalIgnoreCase))
        { result = JobType.Recurring; return true; }
        if (string.Equals(type, "self-driven", StringComparison.OrdinalIgnoreCase))
        { result = JobType.SelfDriven; return true; }
        result = default;
        return false;
    }

    internal static bool TryParseJobDeliveryMode(string mode, out JobDeliveryMode result)
    {
        if (string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase))
        { result = JobDeliveryMode.Auto; return true; }
        if (string.Equals(mode, "approval", StringComparison.OrdinalIgnoreCase))
        { result = JobDeliveryMode.Approval; return true; }
        if (string.Equals(mode, "none", StringComparison.OrdinalIgnoreCase))
        { result = JobDeliveryMode.None; return true; }
        result = default;
        return false;
    }

    internal static string StatusToString(JobStatus status) => status switch
    {
        JobStatus.Pending => "pending",
        JobStatus.Cancelled => "cancelled",
        JobStatus.Running => "running",
        JobStatus.Completed => "completed",
        JobStatus.Failed => "failed",
        JobStatus.Paused => "paused",
        _ => "unknown",
    };
}

public sealed class JobCreateArgs
{
    [ToolParameter(Description = "Job 显示名称，1-200 字符")]
    public required string Name { get; init; }

    [ToolParameter(Description = "Job 类型：one-shot / recurring / self-driven")]
    public required string Type { get; init; }

    [ToolParameter(Description = "Agent prompt 文本，1-50000 字符")]
    public required string Prompt { get; init; }

    [ToolParameter(Description = "Cron 表达式（5 字段），仅 type=recurring 时必填", Required = false)]
    public string? Cron { get; init; }

    [ToolParameter(Description = "下次执行时间 ISO 8601，one-shot/self-driven 必填", Required = false)]
    public string? NextRunAt { get; init; }

    [ToolParameter(Description = "兜底间隔（分钟），仅 self-driven 有效，5-10080", Required = false)]
    public int? FallbackIntervalMinutes { get; init; }

    [ToolParameter(Description = "推送渠道 BindingId 列表", Required = false)]
    public IReadOnlyList<string>? Channels { get; init; }

    [ToolParameter(Description = "推送模式：auto / approval / none", Required = false)]
    public string? DeliveryMode { get; init; }

    [ToolParameter(Description = "超时时间（分钟），1-1440", Required = false)]
    public int? TimeoutMinutes { get; init; }

    [ToolParameter(Description = "最大重试次数，0-5", Required = false)]
    public int? MaxRetries { get; init; }

    [ToolParameter(Description = "最大连续失败次数，1-100", Required = false)]
    public int? MaxConsecutiveFailures { get; init; }
}
