using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Sessions;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.Runtime.Tools;

/// <summary>
/// Tool that lets the Agent query its own diagnostics log for self-inspection.
/// Returns a concise snapshot (no attributes) to avoid leaking sensitive data into context.
/// </summary>
public sealed class DiagnosticsQueryTool : ToolBase<DiagnosticsQueryArgs>
{
    private const int DefaultSinceMinutes = 60;
    private const int MaxSinceMinutes = 1440;
    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    private readonly IDiagnosticsService _diagnosticsService;
    private readonly ICorrelationContextAccessor? _correlationContextAccessor;

    public DiagnosticsQueryTool(IDiagnosticsService diagnosticsService, ICorrelationContextAccessor? correlationContextAccessor = null)
    {
        ArgumentNullException.ThrowIfNull(diagnosticsService);
        _diagnosticsService = diagnosticsService;
        _correlationContextAccessor = correlationContextAccessor;
    }

    public override string Name => "diagnostics_query";

    public override string Description =>
        "Query recent diagnostic events from the KodaClaw runtime log. " +
        "Use this to inspect errors, warnings, or correlated events for self-diagnosis. " +
        "Returns concise event records without sensitive attribute values.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<DiagnosticsQueryArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = true,
        RequiresApproval = false,
    };

    protected override Task<ToolResult> ExecuteAsync(
        DiagnosticsQueryArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var sinceMinutes = Math.Clamp(args.SinceMinutes ?? DefaultSinceMinutes, 1, MaxSinceMinutes);
        var limit = Math.Clamp(args.Limit ?? DefaultLimit, 1, MaxLimit);
        var since = DateTimeOffset.UtcNow.AddMinutes(-sinceMinutes);

        var query = new DiagnosticsQuery(
            Limit: limit,
            CorrelationId: args.CorrelationId,
            SessionId: null,
            Source: args.Source,
            EventType: null,
            Levels: args.Level is not null ? [args.Level] : null,
            DateFrom: since,
            DateTo: null);

        var events = _diagnosticsService.Query(query);

        var result = events.Select(e => new
        {
            ts = e.Timestamp.ToString("HH:mm:ss"),
            level = e.Level,
            source = e.Source,
            eventType = e.EventType,
            message = e.Message,
            correlationId = e.CorrelationId,
            sessionId = e.SessionId,
        }).ToArray();

        _diagnosticsService.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: "runtime",
            EventType: "diagnostics.query",
            Level: "info",
            Message: $"Agent queried diagnostics: sinceMinutes={sinceMinutes} level={args.Level ?? "*"} source={args.Source ?? "*"} results={result.Length}",
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: _correlationContextAccessor?.CorrelationId));

        return Task.FromResult(ToolResult.Ok(result));
    }
}

public sealed class DiagnosticsQueryArgs
{
    [ToolParameter(Description = "Filter by level: info, warning, or error. Omit to include all levels.", Required = false)]
    public string? Level { get; init; }

    [ToolParameter(Description = "Filter by source (e.g. 'gateway', 'runtime', 'automation'). Omit to include all sources.", Required = false)]
    public string? Source { get; init; }

    [ToolParameter(Description = "Filter by correlation ID (exact match). Omit to include all.", Required = false)]
    public string? CorrelationId { get; init; }

    [ToolParameter(Description = "How many minutes back to query. Defaults to 60, maximum 1440 (24 hours).", Required = false)]
    public int? SinceMinutes { get; init; }

    [ToolParameter(Description = "Maximum number of events to return. Defaults to 20, maximum 50.", Required = false)]
    public int? Limit { get; init; }
}
