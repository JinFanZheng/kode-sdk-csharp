namespace KodaClaw.Contracts.Jobs;

public sealed record JobDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public JobType Type { get; init; }
    public string Prompt { get; init; } = "";
    public JobStatus Status { get; init; } = JobStatus.Pending;
    public int SchemaVersion { get; init; } = 1;
    public string? Cron { get; init; }
    public DateTimeOffset? NextRunAt { get; init; }
    public int? FallbackIntervalMinutes { get; init; }
    public string ConcurrencyKey { get; init; } = "";
    public IReadOnlyList<JobRunRecord> Runs { get; init; } = [];
    public int RunCount { get; init; } = 0;
    public IReadOnlyList<string> Channels { get; init; } = [];
    public JobDeliveryMode DeliveryMode { get; init; } = JobDeliveryMode.None;
    public int TimeoutMinutes { get; init; } = 10;
    public int MaxRetries { get; init; } = 1;
    public int MaxConsecutiveFailures { get; init; } = 10;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// 创建校验：仅允许 Pending 状态（用户只能创建 pending Job）。
    /// </summary>
    public IReadOnlyList<string> ValidateForCreate(DateTimeOffset now)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
            errors.Add("name 不能为空");
        else if (Name.Length > 200)
            errors.Add("name 不能超过 200 字符");

        if (Type != JobType.OneShot && Type != JobType.Recurring && Type != JobType.SelfDriven)
            errors.Add("type 必须是 one-shot / recurring / self-driven");

        if (string.IsNullOrWhiteSpace(Prompt))
            errors.Add("prompt 不能为空");
        else if (Prompt.Length > 50000)
            errors.Add("prompt 不能超过 50000 字符");

        if (Status != JobStatus.Pending)
            errors.Add("创建时 status 必须是 pending");

        if (Type == JobType.Recurring)
        {
            if (string.IsNullOrWhiteSpace(Cron))
                errors.Add("type 为 recurring 时必须提供 cron");
            else if (!IsValidCronFormat(Cron))
                errors.Add("cron 格式不合法，需要 5 字段 cron 表达式");
        }

        if (Type == JobType.OneShot || Type == JobType.SelfDriven)
        {
            if (NextRunAt == null)
                errors.Add($"type 为 {Type.ToString().ToLowerInvariant()} 时必须提供 next_run_at");
            else if (NextRunAt.Value < now)
                errors.Add("next_run_at 不能是过去时间");
        }

        if (FallbackIntervalMinutes.HasValue)
        {
            if (Type != JobType.SelfDriven)
                errors.Add("fallback_interval_minutes 仅 self-driven 类型有效");
            else if (FallbackIntervalMinutes.Value < 5 || FallbackIntervalMinutes.Value > 10080)
                errors.Add("fallback_interval_minutes 必须在 5-10080 之间");
        }

        if (TimeoutMinutes < 1 || TimeoutMinutes > 1440)
            errors.Add("timeout_minutes 必须在 1-1440 之间");

        if (MaxRetries < 0 || MaxRetries > 5)
            errors.Add("max_retries 必须在 0-5 之间");

        if (MaxConsecutiveFailures < 1 || MaxConsecutiveFailures > 100)
            errors.Add("max_consecutive_failures 必须在 1-100 之间");

        if (DeliveryMode != JobDeliveryMode.Auto &&
            DeliveryMode != JobDeliveryMode.Approval &&
            DeliveryMode != JobDeliveryMode.None)
            errors.Add("delivery_mode 必须是 auto / approval / none");

        return errors.AsReadOnly();
    }

    /// <summary>
    /// 更新校验：允许所有状态（调度器和工具内部更新用）。
    /// </summary>
    public IReadOnlyList<string> ValidateForUpdate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
            errors.Add("name 不能为空");
        else if (Name.Length > 200)
            errors.Add("name 不能超过 200 字符");

        if (Type != JobType.OneShot && Type != JobType.Recurring && Type != JobType.SelfDriven)
            errors.Add("type 必须是 one-shot / recurring / self-driven");

        if (string.IsNullOrWhiteSpace(Prompt))
            errors.Add("prompt 不能为空");
        else if (Prompt.Length > 50000)
            errors.Add("prompt 不能超过 50000 字符");

        if (Type == JobType.Recurring)
        {
            if (string.IsNullOrWhiteSpace(Cron))
                errors.Add("type 为 recurring 时必须提供 cron");
            else if (!IsValidCronFormat(Cron))
                errors.Add("cron 格式不合法，需要 5 字段 cron 表达式");
        }

        if (Type == JobType.SelfDriven && FallbackIntervalMinutes.HasValue)
        {
            if (FallbackIntervalMinutes.Value < 5 || FallbackIntervalMinutes.Value > 10080)
                errors.Add("fallback_interval_minutes 必须在 5-10080 之间");
        }

        if (TimeoutMinutes < 1 || TimeoutMinutes > 1440)
            errors.Add("timeout_minutes 必须在 1-1440 之间");

        if (MaxRetries < 0 || MaxRetries > 5)
            errors.Add("max_retries 必须在 0-5 之间");

        if (MaxConsecutiveFailures < 1 || MaxConsecutiveFailures > 100)
            errors.Add("max_consecutive_failures 必须在 1-100 之间");

        if (DeliveryMode != JobDeliveryMode.Auto &&
            DeliveryMode != JobDeliveryMode.Approval &&
            DeliveryMode != JobDeliveryMode.None)
            errors.Add("delivery_mode 必须是 auto / approval / none");

        return errors.AsReadOnly();
    }

    /// <summary>
    /// 向后兼容：委托到 ValidateForCreate。新代码应直接调用 ValidateForCreate 或 ValidateForUpdate。
    /// </summary>
    public IReadOnlyList<string> Validate(DateTimeOffset now) => ValidateForCreate(now);

    public static bool IsValidCronFormat(string cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) return false;
        var parts = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 5;
    }
}
