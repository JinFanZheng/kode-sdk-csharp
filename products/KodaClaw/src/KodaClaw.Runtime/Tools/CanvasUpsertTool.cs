using KodaClaw.Contracts.Canvas;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.Workspace;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.Runtime.Tools;

/// <summary>
/// Tool that publishes content to the Canvas artifact store.
/// The Agent uses this to present reports, task boards, or structured results the user can view in Canvas.
/// </summary>
public sealed class CanvasUpsertTool : ToolBase<CanvasUpsertArgs>
{
    private static readonly HashSet<string> ValidKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "report", "html", "board", "tasklist", "dashboard", "pluginpanel",
    };

    private readonly IWorkspaceService _workspaceService;
    private readonly ICanvasArtifactRepository _canvasRepository;
    private readonly ICorrelationContextAccessor? _correlationContextAccessor;
    private readonly IDiagnosticsService? _diagnosticsService;

    public CanvasUpsertTool(
        IWorkspaceService workspaceService,
        ICanvasArtifactRepository canvasRepository,
        ICorrelationContextAccessor? correlationContextAccessor = null,
        IDiagnosticsService? diagnosticsService = null)
    {
        ArgumentNullException.ThrowIfNull(workspaceService);
        ArgumentNullException.ThrowIfNull(canvasRepository);
        _workspaceService = workspaceService;
        _canvasRepository = canvasRepository;
        _correlationContextAccessor = correlationContextAccessor;
        _diagnosticsService = diagnosticsService;
    }

    public override string Name => "canvas_upsert";

    public override string Description =>
        "Publish content to the Canvas. Use this to present reports, summaries, task boards, or any " +
        "structured output the user should be able to view and revisit. " +
        "Supports markdown and html content types. " +
        "If an artifact with the same id already exists, its content is updated in place.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<CanvasUpsertArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = false,
    };

    protected override async Task<ToolResult> ExecuteAsync(
        CanvasUpsertArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (!ValidKinds.Contains(args.Kind))
        {
            var valid = string.Join(", ", ValidKinds);
            return ToolResult.Fail($"Unknown kind '{args.Kind}'. Valid values: {valid}.");
        }

        var contentType = string.IsNullOrWhiteSpace(args.ContentType)
            ? "markdown"
            : args.ContentType.Trim().ToLowerInvariant();

        if (contentType != "markdown" && contentType != "html")
        {
            return ToolResult.Fail($"Unknown content_type '{args.ContentType}'. Valid values: markdown, html.");
        }

        var id = string.IsNullOrWhiteSpace(args.Id)
            ? $"canvas-{Guid.NewGuid():N}"[..15]
            : args.Id.Trim();

        var ext = contentType == "html" ? "html" : "md";
        var relativeEntryPath = $"{KodaClawWorkspaceLayout.WorkspaceDirectory}/canvas/{id}/index.{ext}";
        var absoluteDir = Path.Combine(
            _workspaceService.RootPath,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            "canvas",
            id);
        var absoluteFilePath = Path.Combine(absoluteDir, $"index.{ext}");

        Directory.CreateDirectory(absoluteDir);
        await File.WriteAllTextAsync(absoluteFilePath, args.Content, cancellationToken);

        var kind = ParseKind(args.Kind);
        var now = DateTimeOffset.UtcNow;
        var existing = await _canvasRepository.GetByIdAsync(id, cancellationToken);

        var artifact = new CanvasArtifact(
            Id: id,
            Title: args.Title,
            Kind: kind,
            Summary: args.Summary ?? args.Title,
            Source: "agent",
            EntryPath: relativeEntryPath,
            AssetDirectory: $"{KodaClawWorkspaceLayout.WorkspaceDirectory}/canvas/{id}",
            CreatedAt: existing?.CreatedAt ?? now,
            UpdatedAt: now,
            Route: $"/canvas/{id}",
            SessionId: args.SessionId,
            CorrelationId: _correlationContextAccessor?.CorrelationId,
            MetadataJson: null);

        await _canvasRepository.UpsertAsync(artifact, cancellationToken);

        Emit(context, "canvas_upserted", new
        {
            id,
            kind = args.Kind,
            entryPath = relativeEntryPath,
            bytes = args.Content.Length,
        });

        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: "runtime",
            EventType: "canvas.artifact_upserted",
            Level: "info",
            Message: $"Canvas artifact upserted: id={id} kind={args.Kind} bytes={args.Content.Length}",
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: _correlationContextAccessor?.CorrelationId));

        return ToolResult.Ok(new { ok = true, id, kind = args.Kind, entryPath = relativeEntryPath });
    }

    private static CanvasArtifactKind ParseKind(string kind) => kind.ToLowerInvariant() switch
    {
        "report" => CanvasArtifactKind.Report,
        "html" => CanvasArtifactKind.Html,
        "board" => CanvasArtifactKind.Board,
        "tasklist" => CanvasArtifactKind.TaskList,
        "dashboard" => CanvasArtifactKind.Dashboard,
        "pluginpanel" => CanvasArtifactKind.PluginPanel,
        _ => CanvasArtifactKind.Report,
    };
}

/// <summary>
/// Arguments for the canvas_upsert tool.
/// </summary>
public sealed class CanvasUpsertArgs
{
    [ToolParameter(Description = "Unique artifact id. Auto-generated if omitted.", Required = false)]
    public string? Id { get; init; }

    [ToolParameter(Description = "Display title for the artifact.")]
    public required string Title { get; init; }

    [ToolParameter(Description = "Artifact kind: report, html, board, tasklist, dashboard, or pluginpanel.")]
    public required string Kind { get; init; }

    [ToolParameter(Description = "Content to publish. Markdown or HTML string depending on content_type.")]
    public required string Content { get; init; }

    [ToolParameter(Description = "Content type: markdown or html. Defaults to markdown.", Required = false)]
    public string? ContentType { get; init; }

    [ToolParameter(Description = "Short description of the artifact. Defaults to title if omitted.", Required = false)]
    public string? Summary { get; init; }

    [ToolParameter(Description = "Session id to associate with the artifact.", Required = false)]
    public string? SessionId { get; init; }
}
