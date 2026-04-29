using KodaClaw.Contracts.Inbox;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.Runtime.Tools;

/// <summary>
/// Tool that reads Inbox items so the Agent can review what requires attention.
/// Used by automation sessions (e.g. Daily Inbox Digest) and main sessions.
/// </summary>
public sealed class InboxReadTool : ToolBase<InboxReadArgs>
{
    private const int MaxLimit = 50;
    private const int DefaultLimit = 20;

    private static readonly HashSet<string> ValidStatuses =
        new(["open", "acknowledged", "resolved", "archived"], StringComparer.OrdinalIgnoreCase);

    private readonly IInboxRepository _inboxRepository;

    public InboxReadTool(IInboxRepository inboxRepository)
    {
        ArgumentNullException.ThrowIfNull(inboxRepository);
        _inboxRepository = inboxRepository;
    }

    public override string Name => "inbox_read";

    public override string Description =>
        "Read Inbox items to review what requires attention. " +
        "Returns a list of items with title, summary, kind, status, and whether action is required. " +
        "Use status=open (default) to list items needing attention, or status=resolved to review completed items. " +
        "Use this in automation sessions to summarize the inbox, or in conversation to answer 'what's in my inbox?'.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<InboxReadArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = true,
        RequiresApproval = false,
    };

    protected override async Task<ToolResult> ExecuteAsync(
        InboxReadArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var statusFilter = ParseStatus(args.Status);
        var limit = args.Limit is > 0 ? Math.Min(args.Limit.Value, MaxLimit) : DefaultLimit;

        var query = new InboxQuery(
            Status: statusFilter,
            Limit: limit);

        var items = await _inboxRepository.ListAsync(query, cancellationToken);

        var result = items.Select(item => new
        {
            id = item.Id,
            title = item.Title,
            summary = item.Summary,
            kind = item.Kind.ToString(),
            status = item.Status.ToString(),
            requiresAction = item.RequiresAction,
            source = item.Source,
            route = item.Route,
            createdAt = item.CreatedAt.ToString("O"),
        }).ToList();

        return ToolResult.Ok(new { total = result.Count, items = result });
    }

    private static InboxItemStatus? ParseStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return InboxItemStatus.Open;
        }

        var normalized = raw.Trim().ToLowerInvariant();
        if (!ValidStatuses.Contains(normalized))
        {
            return InboxItemStatus.Open;
        }

        return normalized switch
        {
            "open" => InboxItemStatus.Open,
            "acknowledged" => InboxItemStatus.Acknowledged,
            "resolved" => InboxItemStatus.Resolved,
            "archived" => InboxItemStatus.Archived,
            _ => InboxItemStatus.Open,
        };
    }
}

/// <summary>
/// Arguments for the inbox_read tool.
/// </summary>
public sealed class InboxReadArgs
{
    [ToolParameter(Description = "Filter by status: open (default), acknowledged, resolved, or archived.", Required = false)]
    public string? Status { get; init; }

    [ToolParameter(Description = "Maximum number of items to return (1–50). Defaults to 20.", Required = false)]
    public int? Limit { get; init; }
}
