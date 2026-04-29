using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Timers;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.Runtime.Tools;

/// <summary>
/// Tool that schedules a one-shot reminder to run an agent session at a specific future time.
/// </summary>
public sealed class ScheduleReminderTool : ToolBase<ScheduleReminderArgs>
{
    private readonly IOneShotTimerRepository _timerRepository;
    private readonly IDiagnosticsService? _diagnosticsService;

    public ScheduleReminderTool(
        IOneShotTimerRepository timerRepository,
        IDiagnosticsService? diagnosticsService = null)
    {
        ArgumentNullException.ThrowIfNull(timerRepository);
        _timerRepository = timerRepository;
        _diagnosticsService = diagnosticsService;
    }

    public override string Name => "schedule_reminder";

    public override string Description =>
        "Schedule a one-shot reminder to run an automation at a specific future time. " +
        "The agent will execute the given prompt at the specified time. " +
        "Use ISO 8601 format for fireAt (e.g. '2026-03-28T15:30:00+08:00'). " +
        "Optionally specify channels (BindingId list) to push the result to.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<ScheduleReminderArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = false,
    };

    protected override async Task<ToolResult> ExecuteAsync(
        ScheduleReminderArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (!DateTimeOffset.TryParse(args.FireAt, out var fireAt))
        {
            return ToolResult.Fail($"Invalid fireAt format: '{args.FireAt}'. Use ISO 8601, e.g. '2026-03-28T15:30:00+08:00'.");
        }

        if (fireAt <= DateTimeOffset.UtcNow)
        {
            return ToolResult.Fail($"fireAt must be in the future. Got: {fireAt:O}");
        }

        var timerId = Guid.NewGuid().ToString("N")[..16];
        var now = DateTimeOffset.UtcNow;

        var record = new OneShotTimerRecord(
            Id: timerId,
            Title: args.Title,
            Prompt: args.Prompt,
            FireAt: fireAt,
            Status: OneShotTimerStatus.Pending,
            Channels: args.Channels,
            CreatedAt: now,
            FiredAt: null,
            ErrorMessage: null);

        await _timerRepository.AddAsync(record, cancellationToken);

        Emit(context, "reminder_scheduled", new
        {
            timerId,
            title = args.Title,
            fireAt = fireAt.ToString("O"),
        });

        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: "runtime",
            EventType: "oneshot_timer.scheduled",
            Level: "info",
            Message: $"Reminder scheduled: id={timerId} fireAt={fireAt:O} title=\"{args.Title}\"",
            Timestamp: now,
            CorrelationId: null));

        return ToolResult.Ok(new { ok = true, timerId, fireAt = fireAt.ToString("O") });
    }
}

/// <summary>
/// Arguments for the schedule_reminder tool.
/// </summary>
public sealed class ScheduleReminderArgs
{
    [ToolParameter(Description = "The prompt the agent will execute when the timer fires.")]
    public required string Prompt { get; init; }

    [ToolParameter(Description = "ISO 8601 datetime when the reminder should fire (e.g. '2026-03-28T15:30:00+08:00').")]
    public required string FireAt { get; init; }

    [ToolParameter(Description = "Optional short title for this reminder.", Required = false)]
    public string? Title { get; init; }

    [ToolParameter(Description = "Optional list of channel BindingIds to push the result to (e.g. [\"tg-12345\"]).", Required = false)]
    public IReadOnlyList<string>? Channels { get; init; }
}
