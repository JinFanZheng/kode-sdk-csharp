using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kode.Agent.Sdk.Core.Context;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Events;
using Kode.Agent.Sdk.Core.Skills;
using Kode.Agent.Sdk.Core.Todo;
using Kode.Agent.Sdk.Core.Types;

namespace Kode.Agent.Store.Json;

/// <summary>
/// JSON file-based implementation of IAgentStore with WAL (Write-Ahead Logging) strategy.
/// </summary>
public sealed class JsonAgentStore : IAgentStore
{
    private readonly string _baseDir;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly JsonSerializerOptions _eventJsonOptions;

    public JsonAgentStore(string baseDir)
    {
        _baseDir = baseDir;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        _jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        _jsonOptions.Converters.Add(new AgentEventJsonConverter());

        // Events are stored as newline-delimited JSON (one Timeline per line); never indent.
        _eventJsonOptions = new JsonSerializerOptions(_jsonOptions)
        {
            WriteIndented = false
        };
    }

    #region Runtime State

    public async Task SaveMessagesAsync(string agentId, IReadOnlyList<Message> messages, CancellationToken cancellationToken = default)
    {
        var path = GetRuntimePath(agentId, "messages.json");
        await WriteWithWalAsync(path, messages, cancellationToken);
    }

    public async Task<IReadOnlyList<Message>> LoadMessagesAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var path = GetRuntimePath(agentId, "messages.json");
        return await ReadAsync<List<Message>>(path, cancellationToken) ?? [];
    }

    public async Task SaveToolCallRecordsAsync(string agentId, IReadOnlyList<ToolCallRecord> records, CancellationToken cancellationToken = default)
    {
        var path = GetRuntimePath(agentId, "tool-calls.json");
        await WriteWithWalAsync(path, records, cancellationToken);
    }

    public async Task<IReadOnlyList<ToolCallRecord>> LoadToolCallRecordsAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var path = GetRuntimePath(agentId, "tool-calls.json");
        try
        {
            return await ReadAsync<List<ToolCallRecord>>(path, cancellationToken) ?? [];
        }
        catch
        {
            // Back-compat: older tool-calls.json used { callId, toolName, arguments, state(int) ... }.
            try
            {
                var legacy = await ReadAsync<List<LegacyToolCallRecord>>(path, cancellationToken) ?? [];
                return legacy.Select(ConvertLegacy).ToList();
            }
            catch
            {
                return [];
            }
        }
    }

    private sealed record LegacyToolCallRecord
    {
        public string? CallId { get; init; }
        public string? ToolName { get; init; }
        public object? Arguments { get; init; }
        public int State { get; init; }
        public object? Result { get; init; }
        public string? Error { get; init; }
        public long? StartedAt { get; init; }
        public long? CompletedAt { get; init; }
    }

    private static ToolCallRecord ConvertLegacy(LegacyToolCallRecord legacy)
    {
        var id = legacy.CallId ?? Guid.NewGuid().ToString("N");
        var name = legacy.ToolName ?? "tool";
        var input = legacy.Arguments ?? new { };
        var state = (ToolCallState)legacy.State;

        var createdAt = legacy.StartedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var updatedAt = legacy.CompletedAt ?? legacy.StartedAt ?? createdAt;
        var durationMs = legacy.StartedAt != null && legacy.CompletedAt != null
            ? Math.Max(0, legacy.CompletedAt.Value - legacy.StartedAt.Value)
            : (long?)null;

        var approvalRequired = state == ToolCallState.ApprovalRequired;

        return new ToolCallRecord
        {
            Id = id,
            Name = name,
            Input = input,
            State = state,
            Approval = new ToolCallApproval
            {
                Required = approvalRequired
            },
            Result = legacy.Result,
            Error = legacy.Error,
            IsError = state is ToolCallState.Failed or ToolCallState.Denied or ToolCallState.Sealed,
            StartedAt = legacy.StartedAt,
            CompletedAt = legacy.CompletedAt,
            DurationMs = durationMs,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            AuditTrail =
            [
                new ToolCallAuditEntry
                {
                    State = state,
                    Timestamp = updatedAt,
                    Note = "migrated"
                }
            ]
        };
    }

    public async Task SaveTodosAsync(string agentId, TodoSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var path = GetRuntimePath(agentId, "todos.json");
        await WriteWithWalAsync(path, snapshot, cancellationToken);
    }

    public async Task<TodoSnapshot?> LoadTodosAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var path = GetRuntimePath(agentId, "todos.json");
        return await ReadAsync<TodoSnapshot>(path, cancellationToken);
    }

    #endregion

    #region Events

    public async Task AppendEventAsync(string agentId, Timeline timeline, CancellationToken cancellationToken = default)
    {
        var channel = (timeline.Event.Channel ?? "monitor").ToLowerInvariant();
        if (channel is not ("progress" or "control" or "monitor"))
        {
            channel = "monitor";
        }

        var path = GetEventsPath(agentId, $"{channel}.log");
        EnsureDirectoryExists(path);

        var line = JsonSerializer.Serialize(timeline, _eventJsonOptions);
        
        // Use FileStream with FileShare.ReadWrite to allow concurrent reads/writes
        // This prevents IOException when the file is being read by another process
        const int maxRetries = 3;
        const int retryDelayMs = 50;
        
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite, // Allow concurrent reads
                    bufferSize: 4096,
                    useAsync: true);
                
                using var writer = new StreamWriter(stream, leaveOpen: false);
                await writer.WriteLineAsync(line);
                await writer.FlushAsync();
                return; // Success
            }
            catch (IOException) when (attempt < maxRetries - 1)
            {
                // Retry on file lock, with exponential backoff
                await Task.Delay(retryDelayMs * (attempt + 1), cancellationToken);
            }
        }
        
        // If all retries failed, throw the last exception
        // This will be caught by EventBus and handled appropriately
        throw new IOException($"Failed to append event to {path} after {maxRetries} attempts");
    }

    public async IAsyncEnumerable<Timeline> ReadEventsAsync(
        string agentId,
        EventChannel? channel = null,
        Bookmark? since = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channels = channel.HasValue
            ? [GetChannelFileName(channel.Value)]
            : new[] { "progress.log", "control.log", "monitor.log" };

        foreach (var channelFile in channels)
        {
            var path = GetEventsPath(agentId, channelFile);
            if (!File.Exists(path)) continue;

            await foreach (var line in File.ReadLinesAsync(path, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                Timeline? timeline = null;
                try
                {
                    timeline = JsonSerializer.Deserialize<Timeline>(line, _jsonOptions);
                }
                catch
                {
                    // Back-compat: older events logs used { seq, timestamp, channel, event } and event may lack bookmark/channel.
                    try
                    {
                        var legacy = JsonSerializer.Deserialize<LegacyTimeline>(line, _jsonOptions);
                        if (legacy != null)
                        {
                            timeline = ConvertLegacy(legacy);
                        }
                    }
                    catch
                    {
                        // TS-aligned: skip corrupted / unknown lines
                        continue;
                    }
                }

                if (timeline == null) continue;

                if (since != null && timeline.Bookmark.Seq <= since.Seq) continue;

                yield return timeline;
            }
        }
    }

    private sealed record LegacyTimeline
    {
        public long Seq { get; init; }
        public long Timestamp { get; init; }
        public EventChannel Channel { get; init; }
        public required AgentEvent Event { get; init; }
    }

    private static string ToAgentChannel(EventChannel channel) =>
        channel switch
        {
            EventChannel.Progress => "progress",
            EventChannel.Control => "control",
            EventChannel.Monitor => "monitor",
            _ => "monitor"
        };

    private static Timeline ConvertLegacy(LegacyTimeline legacy)
    {
        var bookmark = legacy.Event.Bookmark ?? new Bookmark { Seq = legacy.Seq, Timestamp = legacy.Timestamp };
        var ev = legacy.Event;
        if (ev.Channel == null || string.IsNullOrWhiteSpace(ev.Channel))
        {
            ev = ev with { Channel = ToAgentChannel(legacy.Channel) };
        }
        if (ev.Bookmark == null)
        {
            ev = ev with { Bookmark = bookmark };
        }

        return new Timeline
        {
            Cursor = legacy.Seq,
            Bookmark = bookmark,
            Event = ev
        };
    }

    #endregion

    #region History / Compression

    public async Task SaveHistoryWindowAsync(string agentId, HistoryWindow window, CancellationToken cancellationToken = default)
    {
        var path = GetHistoryPath(agentId, "windows", $"{window.Timestamp}.json");
        await WriteAsync(path, window, cancellationToken);
    }

    public async Task<IReadOnlyList<HistoryWindow>> LoadHistoryWindowsAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var dir = GetHistoryDir(agentId, "windows");
        if (!Directory.Exists(dir)) return [];

        var files = Directory.GetFiles(dir, "*.json").OrderBy(f => f).ToList();
        var result = new List<HistoryWindow>();
        foreach (var file in files)
        {
            var item = await ReadAsync<HistoryWindow>(file, cancellationToken);
            if (item != null)
            {
                result.Add(item);
            }
        }
        return result;
    }

    public async Task SaveCompressionRecordAsync(string agentId, CompressionRecord record, CancellationToken cancellationToken = default)
    {
        var path = GetHistoryPath(agentId, "compressions", $"{record.Timestamp}.json");
        await WriteAsync(path, record, cancellationToken);
    }

    public async Task<IReadOnlyList<CompressionRecord>> LoadCompressionRecordsAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var dir = GetHistoryDir(agentId, "compressions");
        if (!Directory.Exists(dir)) return [];

        var files = Directory.GetFiles(dir, "*.json").OrderBy(f => f).ToList();
        var result = new List<CompressionRecord>();
        foreach (var file in files)
        {
            var item = await ReadAsync<CompressionRecord>(file, cancellationToken);
            if (item != null)
            {
                result.Add(item);
            }
        }
        return result;
    }

    public async Task SaveRecoveredFileAsync(string agentId, RecoveredFile file, CancellationToken cancellationToken = default)
    {
        var safeName = MakeSafeFileName(Path.GetFileName(file.Path));
        var path = GetHistoryPath(agentId, "recovered", $"{safeName}_{file.Timestamp}.json");
        await WriteAsync(path, file, cancellationToken);
    }

    public async Task<IReadOnlyList<RecoveredFile>> LoadRecoveredFilesAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var dir = GetHistoryDir(agentId, "recovered");
        if (!Directory.Exists(dir)) return [];

        var files = Directory.GetFiles(dir, "*.json").OrderBy(f => f).ToList();
        var result = new List<RecoveredFile>();
        foreach (var file in files)
        {
            var item = await ReadAsync<RecoveredFile>(file, cancellationToken);
            if (item != null)
            {
                result.Add(item);
            }
        }
        return result;
    }

    #endregion

    #region Snapshots

    public async Task SaveSnapshotAsync(string agentId, Snapshot snapshot, CancellationToken cancellationToken = default)
    {
        var path = GetSnapshotPath(agentId, snapshot.Id);
        await WriteAsync(path, snapshot, cancellationToken);
    }

    public async Task<Snapshot?> LoadSnapshotAsync(string agentId, string snapshotId, CancellationToken cancellationToken = default)
    {
        var path = GetSnapshotPath(agentId, snapshotId);
        return await ReadAsync<Snapshot>(path, cancellationToken);
    }

    public async Task<IReadOnlyList<Snapshot>> ListSnapshotsAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(GetAgentDir(agentId), "snapshots");
        if (!Directory.Exists(dir)) return [];

        var files = Directory.GetFiles(dir, "*.json")
            .Where(f => !string.Equals(Path.GetFileName(f), "index.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<Snapshot>();
        foreach (var file in files)
        {
            var snapshot = await ReadAsync<Snapshot>(file, cancellationToken);
            if (snapshot != null)
            {
                result.Add(snapshot);
            }
        }

        return result;
    }

    public Task DeleteSnapshotAsync(string agentId, string snapshotId, CancellationToken cancellationToken = default)
    {
        var path = GetSnapshotPath(agentId, snapshotId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    #endregion

    #region Meta

    public async Task SaveInfoAsync(string agentId, AgentInfo info, CancellationToken cancellationToken = default)
    {
        var path = GetMetaPath(agentId);
        await WriteWithWalAsync(path, info, cancellationToken);
    }

    public async Task<AgentInfo?> LoadInfoAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var path = GetMetaPath(agentId);
        return await ReadAsync<AgentInfo>(path, cancellationToken);
    }

    #endregion

    #region Skills State

    public async Task SaveSkillsStateAsync(string agentId, SkillsState state, CancellationToken cancellationToken = default)
    {
        var path = GetRuntimePath(agentId, "skills.json");
        await WriteWithWalAsync(path, state, cancellationToken);
    }

    public async Task<SkillsState?> LoadSkillsStateAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var path = GetRuntimePath(agentId, "skills.json");
        return await ReadAsync<SkillsState>(path, cancellationToken);
    }

    #endregion

    #region Agent Lifecycle

    public Task<bool> ExistsAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var agentDir = GetAgentDir(agentId);
        return Task.FromResult(Directory.Exists(agentDir));
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_baseDir))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var agents = Directory.GetDirectories(_baseDir)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Cast<string>()
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(agents);
    }

    public Task DeleteAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var agentDir = GetAgentDir(agentId);
        if (Directory.Exists(agentDir))
        {
            Directory.Delete(agentDir, true);
        }
        return Task.CompletedTask;
    }

    #endregion

    #region Helpers

    private string GetAgentDir(string agentId) => Path.Combine(_baseDir, agentId);
    private string GetRuntimePath(string agentId, string fileName) => Path.Combine(GetAgentDir(agentId), "runtime", fileName);
    private string GetEventsPath(string agentId, string fileName) => Path.Combine(GetAgentDir(agentId), "events", fileName);
    private string GetSnapshotPath(string agentId, string checkpointId) => Path.Combine(GetAgentDir(agentId), "snapshots", $"{checkpointId}.json");
    private string GetMetaPath(string agentId) => Path.Combine(GetAgentDir(agentId), "meta.json");
    private string GetHistoryDir(string agentId, string subdir) => Path.Combine(GetAgentDir(agentId), "history", subdir);
    private string GetHistoryPath(string agentId, string subdir, string fileName) => Path.Combine(GetHistoryDir(agentId, subdir), fileName);

    private static string GetChannelFileName(EventChannel channel) => channel switch
    {
        EventChannel.Progress => "progress.log",
        EventChannel.Control => "control.log",
        EventChannel.Monitor => "monitor.log",
        _ => "all.log"
    };

    private void EnsureDirectoryExists(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static string MakeSafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "file";
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }

    private async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        // Check for WAL file first (recovery)
        var walPath = path + ".wal";
        if (File.Exists(walPath))
        {
            // WAL exists, recover from it
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(walPath, path);
        }

        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<T>(json, _jsonOptions);
    }

    private async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        EnsureDirectoryExists(path);
        var json = JsonSerializer.Serialize(value, _jsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private async Task WriteWithWalAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        EnsureDirectoryExists(path);

        var walPath = path + ".wal";
        var json = JsonSerializer.Serialize(value, _jsonOptions);

        // Write to WAL first
        await File.WriteAllTextAsync(walPath, json, cancellationToken);

        // Then move to final location (atomic on most filesystems)
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        File.Move(walPath, path);
    }

    #endregion
}
