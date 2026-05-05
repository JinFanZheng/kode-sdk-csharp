using KodaClaw.Contracts.Jobs;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.Runtime.Tools;

public sealed class JobUpdateTool : ToolBase<JobUpdateArgs>
{
    private readonly IJobRepository _jobRepository;

    public JobUpdateTool(IJobRepository jobRepository)
    {
        _jobRepository = jobRepository;
    }

    public override string Name => "job_update";

    public override string Description =>
        "修改现有 Job 的配置。只能修改非系统管理的字段（name、prompt、cron、next_run_at、channels 等）。" +
        "不传的字段保持原值不变。";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<JobUpdateArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = true,
    };

    protected override async Task<ToolResult> ExecuteAsync(
        JobUpdateArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var existing = await _jobRepository.GetByIdAsync(args.JobId, cancellationToken);
        if (existing == null)
            return ToolResult.Fail($"not_found: job_id 为 '{args.JobId}' 的 Job 不存在");

        var now = DateTimeOffset.UtcNow;
        var updatedFields = new List<string>();

        // Type-specific field guards
        if (args.Cron != null && existing.Type != JobType.Recurring)
            return ToolResult.Fail("validation_failed: cron 仅 type=recurring 的 Job 可修改");

        if (args.NextRunAt != null && existing.Type != JobType.SelfDriven)
            return ToolResult.Fail("validation_failed: next_run_at 仅 type=self-driven 的 Job 可修改");

        if (args.FallbackIntervalMinutes.HasValue && existing.Type != JobType.SelfDriven)
            return ToolResult.Fail("validation_failed: fallback_interval_minutes 仅 type=self-driven 的 Job 可修改");

        // Parse next_run_at
        DateTimeOffset? nextRunAt = existing.NextRunAt;
        if (args.NextRunAt != null)
        {
            if (!DateTimeOffset.TryParse(args.NextRunAt, out var parsed))
                return ToolResult.Fail($"无效的 next_run_at 格式: '{args.NextRunAt}'，需使用 ISO 8601 格式");
            if (parsed < now)
                return ToolResult.Fail("validation_failed: next_run_at 不能是过去时间");
            if (parsed > now.AddDays(30))
                return ToolResult.Fail("validation_failed: next_run_at 不能超过当前时间 30 天");
            nextRunAt = parsed;
            updatedFields.Add("next_run_at");
        }

        // Parse delivery_mode
        JobDeliveryMode? deliveryMode = null;
        if (args.DeliveryMode != null)
        {
            if (!JobCreateTool.TryParseJobDeliveryMode(args.DeliveryMode, out var dm))
                return ToolResult.Fail($"无效的 delivery_mode: '{args.DeliveryMode}'，必须是 auto / approval / none");
            deliveryMode = dm;
            updatedFields.Add("delivery_mode");
        }

        // Validate individual field constraints
        if (args.Name != null)
        {
            if (string.IsNullOrWhiteSpace(args.Name) || args.Name.Length > 200)
                return ToolResult.Fail("validation_failed: name 必须为 1-200 字符");
            updatedFields.Add("name");
        }

        if (args.Prompt != null)
        {
            if (string.IsNullOrWhiteSpace(args.Prompt) || args.Prompt.Length > 50000)
                return ToolResult.Fail("validation_failed: prompt 必须为 1-50000 字符");
            updatedFields.Add("prompt");
        }

        if (args.Cron != null)
        {
            if (!JobDefinition.IsValidCronFormat(args.Cron))
                return ToolResult.Fail("validation_failed: cron 格式不合法，需要 5 字段 cron 表达式");
            updatedFields.Add("cron");
        }

        if (args.FallbackIntervalMinutes.HasValue)
        {
            if (args.FallbackIntervalMinutes.Value < 5 || args.FallbackIntervalMinutes.Value > 10080)
                return ToolResult.Fail("validation_failed: fallback_interval_minutes 必须在 5-10080 之间");
            updatedFields.Add("fallback_interval_minutes");
        }

        if (args.TimeoutMinutes.HasValue)
        {
            if (args.TimeoutMinutes.Value < 1 || args.TimeoutMinutes.Value > 1440)
                return ToolResult.Fail("validation_failed: timeout_minutes 必须在 1-1440 之间");
            updatedFields.Add("timeout_minutes");
        }

        if (args.MaxRetries.HasValue)
        {
            if (args.MaxRetries.Value < 0 || args.MaxRetries.Value > 5)
                return ToolResult.Fail("validation_failed: max_retries 必须在 0-5 之间");
            updatedFields.Add("max_retries");
        }

        if (args.MaxConsecutiveFailures.HasValue)
        {
            if (args.MaxConsecutiveFailures.Value < 1 || args.MaxConsecutiveFailures.Value > 100)
                return ToolResult.Fail("validation_failed: max_consecutive_failures 必须在 1-100 之间");
            updatedFields.Add("max_consecutive_failures");
        }

        if (args.Channels != null)
            updatedFields.Add("channels");

        // Merge: only overwrite non-null fields
        var merged = existing with
        {
            Name = args.Name ?? existing.Name,
            Prompt = args.Prompt ?? existing.Prompt,
            Cron = args.Cron ?? existing.Cron,
            NextRunAt = nextRunAt,
            FallbackIntervalMinutes = args.FallbackIntervalMinutes ?? existing.FallbackIntervalMinutes,
            Channels = args.Channels ?? existing.Channels,
            DeliveryMode = deliveryMode ?? existing.DeliveryMode,
            TimeoutMinutes = args.TimeoutMinutes ?? existing.TimeoutMinutes,
            MaxRetries = args.MaxRetries ?? existing.MaxRetries,
            MaxConsecutiveFailures = args.MaxConsecutiveFailures ?? existing.MaxConsecutiveFailures,
        };

        // Full validation
        var errors = merged.Validate(now);
        if (errors.Count > 0)
            return ToolResult.Fail($"validation_failed: {errors[0]}");

        await _jobRepository.UpdateAsync(merged, cancellationToken);

        return ToolResult.Ok(new
        {
            job_id = args.JobId,
            updated_fields = updatedFields,
        });
    }
}

public sealed class JobUpdateArgs
{
    [ToolParameter(Description = "Job ID")]
    public required string JobId { get; init; }

    [ToolParameter(Description = "新的显示名称，1-200 字符", Required = false)]
    public string? Name { get; init; }

    [ToolParameter(Description = "新的 Agent prompt，1-50000 字符", Required = false)]
    public string? Prompt { get; init; }

    [ToolParameter(Description = "新的 5 字段 cron 表达式，仅 type=recurring 可修改", Required = false)]
    public string? Cron { get; init; }

    [ToolParameter(Description = "新的 next_run_at ISO 8601，仅 type=self-driven 可修改", Required = false)]
    public string? NextRunAt { get; init; }

    [ToolParameter(Description = "新的兜底间隔（分钟），5-10080，仅 self-driven 有效", Required = false)]
    public int? FallbackIntervalMinutes { get; init; }

    [ToolParameter(Description = "新的推送渠道 BindingId 列表", Required = false)]
    public IReadOnlyList<string>? Channels { get; init; }

    [ToolParameter(Description = "新的推送模式：auto / approval / none", Required = false)]
    public string? DeliveryMode { get; init; }

    [ToolParameter(Description = "新的超时时间（分钟），1-1440", Required = false)]
    public int? TimeoutMinutes { get; init; }

    [ToolParameter(Description = "新的最大重试次数，0-5", Required = false)]
    public int? MaxRetries { get; init; }

    [ToolParameter(Description = "新的最大连续失败次数，1-100", Required = false)]
    public int? MaxConsecutiveFailures { get; init; }
}
