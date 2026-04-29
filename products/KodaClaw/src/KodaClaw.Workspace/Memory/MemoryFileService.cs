using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Memory;
using KodaClaw.Contracts.Workspace;

namespace KodaClaw.Workspace.Memory;

/// <summary>
/// File-based memory service that scans workspace directories
/// to list, stat, and promote memory entries — replacing SQLite queries.
/// </summary>
public sealed class MemoryFileService : IMemoryFileService
{
    private const string DiagnosticSource = "koda.memory";

    private readonly IWorkspaceService _workspaceService;
    private readonly IDiagnosticsService? _diagnosticsService;

    public MemoryFileService(
        IWorkspaceService workspaceService,
        IDiagnosticsService? diagnosticsService = null)
    {
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _diagnosticsService = diagnosticsService;
    }

    public Task<MemoryFileStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var root = _workspaceService.RootPath;

        var activeCount = CountMemorySections();
        var dormantCount = CountFilesInDir(Path.Combine(root, KodaClawWorkspaceLayout.MemoryDormantDirectory));
        var archivedCount = CountFilesInDir(Path.Combine(root, KodaClawWorkspaceLayout.MemoryArchiveDirectory));
        var topicsCount = CountFilesInDir(Path.Combine(root, KodaClawWorkspaceLayout.MemoryTopicsDirectory));
        var sessionsCount = CountFilesInDir(Path.Combine(root, KodaClawWorkspaceLayout.MemorySessionsDirectory));

        return Task.FromResult(new MemoryFileStats(
            activeCount, dormantCount, archivedCount, topicsCount, sessionsCount));
    }

    public async Task<IReadOnlyList<MemoryFileEntry>> ListEntriesAsync(
        string? statusFilter = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<MemoryFileEntry>();

        if (statusFilter is null or "active")
        {
            entries.AddRange(await ScanActiveEntriesAsync(cancellationToken));
        }

        if (statusFilter is null or "dormant")
        {
            entries.AddRange(ScanDirectoryEntries(
                KodaClawWorkspaceLayout.MemoryDormantDirectory, "dormant"));
        }

        if (statusFilter is null or "archived")
        {
            entries.AddRange(ScanDirectoryEntries(
                KodaClawWorkspaceLayout.MemoryArchiveDirectory, "archived"));
        }

        return entries
            .OrderBy(e => PrioritySortOrder(e.Priority))
            .ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    public async Task<bool> PromoteEntryAsync(string key, CancellationToken cancellationToken = default)
    {
        var root = _workspaceService.RootPath;

        // Search in dormant and archive directories
        var sourcePath = FindEntryFile(key,
            Path.Combine(root, KodaClawWorkspaceLayout.MemoryDormantDirectory),
            Path.Combine(root, KodaClawWorkspaceLayout.MemoryArchiveDirectory));

        if (sourcePath is null)
            return false;

        // Read file, update frontmatter status to active
        var content = await File.ReadAllTextAsync(sourcePath, cancellationToken);
        var updated = UpdateFrontmatterStatus(content, "active");

        // Move to MEMORY.md as a new section
        var memoryFilePath = Path.Combine(
            root,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            KodaClawWorkspaceLayout.MemoryFile);

        var title = ExtractTitle(content, key);
        var body = ExtractBody(updated);

        var sectionContent = $"## {title}\n{body}";

        var currentMemory = File.Exists(memoryFilePath)
            ? await File.ReadAllTextAsync(memoryFilePath, cancellationToken)
            : "# Long-Term Memory\n";

        var newMemory = currentMemory.TrimEnd() + "\n\n" + sectionContent.TrimEnd() + "\n";
        await File.WriteAllTextAsync(memoryFilePath, newMemory, cancellationToken);

        // Remove source file
        File.Delete(sourcePath);

        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: $"diag-{Guid.NewGuid():N}",
            Source: DiagnosticSource,
            EventType: "memory.entry.promoted",
            Level: "info",
            Message: $"Memory entry '{key}' promoted to active.",
            Timestamp: DateTimeOffset.UtcNow,
            Attributes: new Dictionary<string, string?> { ["key"] = key }));

        return true;
    }

    private int CountMemorySections()
    {
        var memoryFile = Path.Combine(
            _workspaceService.RootPath,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            KodaClawWorkspaceLayout.MemoryFile);

        if (!File.Exists(memoryFile))
            return 0;

        var content = File.ReadAllText(memoryFile);
        return MemoryMarkdownParser.ParseSections(content).Count;
    }

    private static int CountFilesInDir(string dirPath)
    {
        return Directory.Exists(dirPath)
            ? Directory.GetFiles(dirPath, "*.md").Length
            : 0;
    }

    private async Task<IReadOnlyList<MemoryFileEntry>> ScanActiveEntriesAsync(CancellationToken cancellationToken)
    {
        var memoryFile = Path.Combine(
            _workspaceService.RootPath,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            KodaClawWorkspaceLayout.MemoryFile);

        if (!File.Exists(memoryFile))
            return [];

        var content = await File.ReadAllTextAsync(memoryFile, cancellationToken);
        var sections = MemoryMarkdownParser.ParseSections(content);

        return sections.Select(s =>
        {
            var key = MemoryMarkdownParser.NormalizeKey(s.Title);
            var fm = MemoryFrontmatterParser.Parse(s.Body);
            return new MemoryFileEntry(
                Key: key,
                Title: s.Title,
                Priority: fm.Priority,
                Status: "active",
                Created: fm.Created,
                SourcePath: $"workspace/{KodaClawWorkspaceLayout.MemoryFile}",
                Tags: fm.Tags);
        }).ToList();
    }

    private IReadOnlyList<MemoryFileEntry> ScanDirectoryEntries(string relativeDir, string status)
    {
        var dirPath = Path.Combine(_workspaceService.RootPath, relativeDir);
        if (!Directory.Exists(dirPath))
            return [];

        var files = Directory.GetFiles(dirPath, "*.md");
        var entries = new List<MemoryFileEntry>(files.Length);

        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            var fm = MemoryFrontmatterParser.Parse(content);
            var fileName = Path.GetFileNameWithoutExtension(file);

            entries.Add(new MemoryFileEntry(
                Key: fileName,
                Title: fm.Title ?? fileName,
                Priority: fm.Priority,
                Status: status,
                Created: fm.Created,
                SourcePath: $"{relativeDir}/{Path.GetFileName(file)}",
                Tags: fm.Tags));
        }

        return entries;
    }

    private static int PrioritySortOrder(string priority) => priority switch
    {
        "permanent" => 0,
        "lasting" => 1,
        "standard" => 2,
        "ephemeral" => 3,
        _ => 2,
    };

    private static string? FindEntryFile(string key, params string[] directories)
    {
        foreach (var dir in directories)
        {
            if (!Directory.Exists(dir)) continue;
            var path = Path.Combine(dir, $"{key}.md");
            if (File.Exists(path)) return path;
        }

        return null;
    }

    private static string UpdateFrontmatterStatus(string content, string newStatus)
    {
        var lines = content.Split('\n').ToList();
        if (lines.Count < 2 || lines[0].Trim() != "---")
            return content;

        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i].Trim() == "---") break;
            if (lines[i].TrimStart().StartsWith("status:", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"status: {newStatus}";
                return string.Join('\n', lines);
            }
        }

        return content;
    }

    private static string ExtractTitle(string content, string fallbackKey)
    {
        var fm = MemoryFrontmatterParser.Parse(content);
        if (!string.IsNullOrWhiteSpace(fm.Title))
            return fm.Title;

        // Try first # heading
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                return trimmed[2..].Trim();
        }

        return fallbackKey;
    }

    private static string ExtractBody(string content)
    {
        var fm = MemoryFrontmatterParser.Parse(content);
        if (fm.BodyStartLine <= 0)
            return content;

        var lines = content.Split('\n');
        if (fm.BodyStartLine >= lines.Length)
            return string.Empty;

        return string.Join('\n', lines[fm.BodyStartLine..]).Trim();
    }
}
