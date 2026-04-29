using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.Sessions;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.Runtime.Tools;

/// <summary>
/// Tool that creates an informational inbox item so the Agent can proactively notify the user.
/// </summary>
public sealed class InboxCreateTool : ToolBase<InboxCreateArgs>
{
    private readonly IInboxRepository _inboxRepository;
    private readonly ICorrelationContextAccessor? _correlationContextAccessor;
    private readonly IDiagnosticsService? _diagnosticsService;

    public InboxCreateTool(
        IInboxRepository inboxRepository,
        ICorrelationContextAccessor? correlationContextAccessor = null,
        IDiagnosticsService? diagnosticsService = null)
    {
        ArgumentNullException.ThrowIfNull(inboxRepository);
        _inboxRepository = inboxRepository;
        _correlationContextAccessor = correlationContextAccessor;
        _diagnosticsService = diagnosticsService;
    }

    public override string Name => "inbox_create";

    public override string Description =>
        "Create an informational inbox item to notify the user of a finding, decision, or follow-up item. " +
        "Use this when you have completed analysis or taken an action that the user should be aware of, " +
        "or when you want to surface a task that needs their attention.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<InboxCreateArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = false,
    };

    protected override async Task<ToolResult> ExecuteAsync(
        InboxCreateArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var id = $"agent-note-{Guid.NewGuid():N}"[..24];
        var now = DateTimeOffset.UtcNow;

        var item = new InboxItem(
            Id: id,
            Kind: InboxItemKind.Information,
            Status: InboxItemStatus.Open,
            Title: args.Title,
            Summary: args.Summary,
            Source: "agent",
            CreatedAt: now,
            UpdatedAt: now,
            RequiresAction: args.RequiresAction,
            Route: args.Route,
            SessionId: context.AgentId,
            CorrelationId: _correlationContextAccessor?.CorrelationId,
            ApprovalId: null,
            PayloadJson: null,
            ResolvedAt: null);

        await _inboxRepository.UpsertAsync(item, cancellationToken);

        Emit(context, "inbox_item_created", new
        {
            id,
            title = args.Title,
            requiresAction = args.RequiresAction,
        });

        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: "runtime",
            EventType: "inbox.item_created",
            Level: "info",
            Message: $"Inbox item created by agent: id={id} title=\"{args.Title}\" requiresAction={args.RequiresAction}",
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: _correlationContextAccessor?.CorrelationId));

        return ToolResult.Ok(new { ok = true, id });
    }
}

/// <summary>
/// Arguments for the inbox_create tool.
/// </summary>
public sealed class InboxCreateArgs
{
    [ToolParameter(Description = "Short title for the inbox notification.")]
    public required string Title { get; init; }

    [ToolParameter(Description = "Markdown summary of the finding, decision, or action taken.")]
    public required string Summary { get; init; }

    [ToolParameter(Description = "Set to true if the user needs to take action. Defaults to false.", Required = false)]
    public bool RequiresAction { get; init; } = false;

    [ToolParameter(Description = "Optional navigation route shown in the inbox item (e.g. /canvas/my-report).", Required = false)]
    public string? Route { get; init; }
}
