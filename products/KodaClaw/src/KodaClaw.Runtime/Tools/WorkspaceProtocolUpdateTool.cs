using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Workspace;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.Runtime.Tools;

/// <summary>
/// Tool that applies a section-level patch to a workspace protocol file.
/// The Agent decides which file and section to update based on semantic understanding.
/// </summary>
public sealed class WorkspaceProtocolUpdateTool : ToolBase<WorkspaceProtocolUpdateArgs>
{
    private static readonly IReadOnlyDictionary<string, string> TargetFileMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["identity"] = KodaClawWorkspaceLayout.IdentityFile,
            ["soul"] = KodaClawWorkspaceLayout.SoulFile,
            ["ontology"] = KodaClawWorkspaceLayout.OntologyFile,
            ["user"] = KodaClawWorkspaceLayout.UserFile,
            ["memory"] = KodaClawWorkspaceLayout.MemoryFile,
            ["agents"] = KodaClawWorkspaceLayout.AgentsFile,
            ["heartbeat"] = KodaClawWorkspaceLayout.HeartbeatFile,
        };

    private static readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly IWorkspaceService _workspaceService;
    private readonly IDiagnosticsService? _diagnosticsService;

    public WorkspaceProtocolUpdateTool(
        IWorkspaceService workspaceService,
        IDiagnosticsService? diagnosticsService = null)
    {
        ArgumentNullException.ThrowIfNull(workspaceService);
        _workspaceService = workspaceService;
        _diagnosticsService = diagnosticsService;
    }

    public override string Name => "workspace_protocol_update";

    public override string Description =>
        "Update a section of a workspace protocol file (identity, soul, ontology, user, memory, agents, or heartbeat). " +
        "Use this when the user shares information that should permanently update their profile, " +
        "preferences, behavioral rules, world-view/values, long-term memory, or scheduled automation rules. " +
        "Use target=ontology to update epistemology, methodology, values, or meta-cognition frameworks. " +
        "Use target=heartbeat to add or modify a ## SectionTitle automation rule in HEARTBEAT.md. " +
        "Changes to identity/soul/ontology/user/memory/agents take effect at the next session start. " +
        "Changes to heartbeat take effect immediately via the hot-sync pipeline.\n\n" +
        "HEARTBEAT.md section syntax (all fields are bullet items under a ## Title heading):\n" +
        "  Required: `- cron: \"<5-field-cron>\"` and `- prompt: <text>`\n" +
        "  Cron expressions use UTC time. Standard 5-field format: minute hour day month weekday\n" +
        "  Common patterns: `0 9 * * *` (09:00 UTC daily) | `0 9 * * 1-5` (weekdays 09:00 UTC) | `*/15 * * * *` (every 15 min)\n" +
        "  Legacy `- schedule:` is still accepted (e.g. `daily 09:00`) but `- cron:` is preferred.\n" +
        "  Optional: `- enabled: true|false` (default true)\n" +
        "  Optional: `- inputs:` followed by indented `- <workspace-relative-path>` bullets\n" +
        "  Optional: `- channels:` followed by indented `- <bindingId>` bullets (BindingId is copied from ChannelsDesk)\n" +
        "  Optional: `- delivery-mode: auto|approval|none` (default none)\n" +
        "    none     = Agent handles delivery itself (use when prompt instructs Agent to call channel_send)\n" +
        "    auto     = Scheduler pushes the Agent's final response text after success (use when prompt only asks Agent to generate content, NOT to send it — if Agent already calls channel_send the result will be sent twice)\n" +
        "    approval = Queue result to Inbox; user manually triggers push\n" +
        "  IMPORTANT: never combine a prompt that says 'push/send to Telegram' with delivery-mode: auto — that causes double-sending. Use auto only when the prompt is purely generative (e.g. 'summarize today\\'s news') and does not instruct the Agent to send anything.\n" +
        "  Example section content:\n" +
        "    - cron: \"0 9 * * *\"\n" +
        "    - prompt: Summarize yesterday's tasks and prepare today's plan.\n" +
        "    - channels:\n" +
        "      - tg-main-abc123\n" +
        "    - delivery-mode: auto";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<WorkspaceProtocolUpdateArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = false,
    };

    protected override async Task<ToolResult> ExecuteAsync(
        WorkspaceProtocolUpdateArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (!TargetFileMap.TryGetValue(args.Target, out var fileName))
        {
            var valid = string.Join(", ", TargetFileMap.Keys);
            return ToolResult.Fail($"Unknown target '{args.Target}'. Valid values: {valid}.");
        }

        var filePath = Path.Combine(
            _workspaceService.RootPath,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            fileName);

        var currentContent = File.Exists(filePath)
            ? await File.ReadAllTextAsync(filePath, cancellationToken)
            : GetDefaultContent(args.Target);

        var patched = ApplySectionPatch(currentContent, args.Section, args.Content);

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        // Pre-write validation for heartbeat target: reject if section count would decrease
        if (string.Equals(args.Target, "heartbeat", StringComparison.OrdinalIgnoreCase)
            && File.Exists(filePath))
        {
            var currentCount = CountSections(currentContent);
            var patchedCount = CountSections(patched);
            if (patchedCount < currentCount)
            {
                return ToolResult.Fail(
                    $"Section count would decrease ({currentCount} -> {patchedCount}). " +
                    "This may indicate a concurrent write conflict. Retrying may resolve the issue.");
            }
        }

        // Serialize file writes to prevent concurrent read-modify-write races
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var latest = File.Exists(filePath)
                ? await File.ReadAllTextAsync(filePath, cancellationToken)
                : GetDefaultContent(args.Target);
            patched = ApplySectionPatch(latest, args.Section, args.Content);

            // Atomic write: write to temp file first, then move to replace
            var tmpPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(tmpPath, patched, cancellationToken);
            File.Move(tmpPath, filePath, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }

        Emit(context, "workspace_protocol_updated", new
        {
            target = args.Target,
            section = args.Section,
            path = filePath,
            bytes = patched.Length,
        });

        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: "workspace",
            EventType: "workspace.protocol_updated",
            Level: "info",
            Message: $"Workspace protocol file updated: target={args.Target} section={args.Section ?? "(root)"} bytes={patched.Length}",
            Timestamp: DateTimeOffset.UtcNow));

        var sectionTag = string.IsNullOrWhiteSpace(args.Section) ? args.Target : $"{args.Target}/{args.Section}";
        var committed = await _workspaceService.TryCommitWorkspaceAsync(
            $"workspace({args.Target})[agent]: update {sectionTag}",
            cancellationToken);

        return ToolResult.Ok(new { ok = true, target = args.Target, section = args.Section, path = filePath, committed });
    }

    /// <summary>
    /// Applies a section-level patch to the given markdown content.
    /// </summary>
    public static string ApplySectionPatch(string currentContent, string? section, string newContent)
    {
        var trimmedNew = newContent.TrimEnd();

        // No section specified → replace everything after the first # title line.
        if (string.IsNullOrWhiteSpace(section))
        {
            var lines = currentContent.Split('\n');
            var titleLine = lines.FirstOrDefault(l => l.TrimStart().StartsWith("# ", StringComparison.Ordinal));
            if (titleLine is null)
            {
                return trimmedNew + "\n";
            }

            return titleLine.TrimEnd() + "\n\n" + trimmedNew + "\n";
        }

        // Section specified → locate ## {section}, replace its body.
        var sectionHeading = $"## {section.Trim()}";
        var allLines = currentContent.Split('\n').ToList();

        var sectionStart = allLines.FindIndex(l =>
            l.TrimEnd().Equals(sectionHeading, StringComparison.OrdinalIgnoreCase));

        if (sectionStart < 0)
        {
            // Section not found → append at end.
            var appended = currentContent.TrimEnd() + "\n\n" + sectionHeading + "\n" + trimmedNew + "\n";
            return appended;
        }

        // Find the end of this section (next ## heading or EOF).
        var sectionEnd = allLines.FindIndex(sectionStart + 1,
            l => l.TrimStart().StartsWith("## ", StringComparison.Ordinal));

        var before = allLines.Take(sectionStart + 1).ToList(); // include the ## heading line
        var after = sectionEnd >= 0 ? allLines.Skip(sectionEnd).ToList() : [];

        var result = string.Join("\n", before)
            + "\n"
            + trimmedNew
            + "\n"
            + (after.Count > 0 ? "\n" + string.Join("\n", after).TrimEnd() + "\n" : "");

        return result;
    }

    internal static int CountSections(string content)
    {
        var count = 0;
        foreach (var line in content.Split('\n'))
        {
            if (line.TrimStart().StartsWith("## ", StringComparison.Ordinal))
                count++;
        }
        return count;
    }

    private static string GetDefaultContent(string target) => target.ToLowerInvariant() switch
    {
        "identity" => "# Koda Identity\n\n",
        "soul" => "# Koda Soul\n\n",
        "ontology" => "# Koda Ontology\n\n",
        "user" => "# User Profile\n\n",
        "memory" => "# Long-Term Memory\n\n",
        "agents" => "# KodaClaw Workspace Rules\n\n",
        "heartbeat" => "# Heartbeat Automations\n\n",
        _ => "# Workspace\n\n",
    };
}

/// <summary>
/// Arguments for the workspace_protocol_update tool.
/// </summary>
public sealed class WorkspaceProtocolUpdateArgs
{
    [ToolParameter(Description = "The protocol file to update. One of: identity, soul, ontology, user, memory, agents, heartbeat.")]
    public required string Target { get; init; }

    [ToolParameter(Description = "The ## section heading to update. If omitted, replaces everything after the # title line.", Required = false)]
    public string? Section { get; init; }

    [ToolParameter(Description = "The new content for the section body (everything after the ## heading line). For heartbeat sections, use the bullet-item syntax described in the tool description. For other targets, use plain markdown prose.")]
    public required string Content { get; init; }
}
