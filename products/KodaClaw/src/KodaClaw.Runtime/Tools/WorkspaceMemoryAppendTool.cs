using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Workspace;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.Runtime.Tools;

/// <summary>
/// Tool that appends a memory entry to the daily memory log in the workspace.
/// </summary>
public sealed class WorkspaceMemoryAppendTool : ToolBase<WorkspaceMemoryAppendArgs>
{
    private static readonly HashSet<string> ValidPriorities = new(StringComparer.OrdinalIgnoreCase)
    {
        "permanent", "lasting", "standard", "ephemeral"
    };

    private readonly IWorkspaceService _workspaceService;
    private readonly IDiagnosticsService? _diagnosticsService;

    public WorkspaceMemoryAppendTool(
        IWorkspaceService workspaceService,
        IDiagnosticsService? diagnosticsService = null)
    {
        ArgumentNullException.ThrowIfNull(workspaceService);
        _workspaceService = workspaceService;
        _diagnosticsService = diagnosticsService;
    }

    public override string Name => "workspace_memory_append";

    public override string Description =>
        "Append a memory entry to today's daily memory log in the workspace. " +
        "Use this to record important facts, decisions, or insights worth remembering across sessions. " +
        "Specify priority: permanent (core identity), lasting (important decisions), " +
        "standard (general, default), ephemeral (transient info). " +
        "Entries are stored in workspace/memory/YYYY-MM-DD.md and consolidated into MEMORY.md by nightly automation.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<WorkspaceMemoryAppendArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = false,
    };

    protected override async Task<ToolResult> ExecuteAsync(
        WorkspaceMemoryAppendArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var date = string.IsNullOrWhiteSpace(args.Date)
            ? DateTimeOffset.Now.ToString("yyyy-MM-dd")
            : args.Date;

        var priority = NormalizePriority(args.Priority);

        var memoryDirectory = Path.Combine(
            _workspaceService.RootPath,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            "memory");

        Directory.CreateDirectory(memoryDirectory);

        var filePath = Path.Combine(memoryDirectory, $"{date}.md");
        var timestamp = DateTimeOffset.Now.ToString("HH:mm");
        var entry = $"\n<!-- {timestamp} | {priority} -->\n{args.Content.TrimEnd()}\n";

        await File.AppendAllTextAsync(filePath, entry, cancellationToken);

        Emit(context, "workspace_memory_appended", new { date, priority, path = filePath });

        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: "workspace",
            EventType: "workspace.memory_appended",
            Level: "info",
            Message: $"Memory entry appended: date={date} priority={priority} path={filePath}",
            Timestamp: DateTimeOffset.UtcNow));

        await _workspaceService.TryCommitWorkspaceAsync(
            $"workspace(memory)[agent]: append daily {date}",
            cancellationToken);

        return ToolResult.Ok(new { ok = true, date, priority, path = filePath });
    }

    private static string NormalizePriority(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "standard";

        var trimmed = value.Trim().ToLowerInvariant();
        return ValidPriorities.Contains(trimmed) ? trimmed : "standard";
    }
}

/// <summary>
/// Arguments for the workspace_memory_append tool.
/// </summary>
public sealed class WorkspaceMemoryAppendArgs
{
    [ToolParameter(Description = "The memory entry to append. Markdown text describing a fact, decision, or insight worth remembering.")]
    public required string Content { get; init; }

    [ToolParameter(Description = "The date to write the memory for, in yyyy-MM-dd format. Defaults to today if omitted.", Required = false)]
    public string? Date { get; init; }

    [ToolParameter(Description = "Memory priority: permanent (core identity), lasting (important decisions), standard (general, default), ephemeral (transient). Defaults to standard.", Required = false)]
    public string? Priority { get; init; }
}
