using Kode.Agent.Sdk.Core.Events;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Kode.Agent.Sdk.Core.Agent;

// State persistence surface. Owns the write side of the store contract:
//   - SaveStateAsync: messages + active tool-call records + AgentInfo (metadata snapshot)
//   - UpdateInfoAsync: refreshes AgentInfo only (cheap; runs on every step)
//   - SnapshotAsync / ForkAsync: public branch-point API
//   - BuildAgentMetadata: canonical metadata serializer (read back by Agent.StateRecovery)
//   - FindLastSfpIndex / TryReadInt: support helpers used by snapshot/fork/info paths
public sealed partial class Agent
{
    /// <inheritdoc />
    public async Task<string> SnapshotAsync(string? label = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var lastSfpIndex = FindLastSfpIndex();
        var snapshotId = string.IsNullOrWhiteSpace(label) ? $"sfp:{lastSfpIndex}" : label.Trim();
        var lastBookmark = _eventBus.LastBookmark ?? new Bookmark
        {
            Seq = -1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var clonedMessages = JsonSerializer.Deserialize<List<Message>>(
                                 JsonSerializer.Serialize(_messages, MetaJsonOptions),
                                 MetaJsonOptions)
                             ?? _messages.ToList();

        var metadata = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["stepCount"] = JsonSerializer.SerializeToElement(_stepCount, MetaJsonOptions)
        };

        var snapshot = new Snapshot
        {
            Id = snapshotId,
            Messages = clonedMessages,
            LastSfpIndex = lastSfpIndex,
            LastBookmark = lastBookmark,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            Metadata = metadata
        };

        await _dependencies.Store.SaveSnapshotAsync(AgentId, snapshot, cancellationToken);

        return snapshotId;
    }

    /// <inheritdoc />
    public async Task<IAgent> ForkAsync(string newAgentId, CancellationToken cancellationToken = default, string? snapshotId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // TS-aligned: fork from a snapshot; when omitted, create one first.
        snapshotId ??= await SnapshotAsync(label: null, cancellationToken);

        var snapshot = await _dependencies.Store.LoadSnapshotAsync(AgentId, snapshotId, cancellationToken);
        if (snapshot == null)
        {
            throw new InvalidOperationException($"Snapshot not found: {snapshotId}");
        }

        // Create new agent with the same config.
        var forkedAgent = new Agent(newAgentId, _config, _dependencies);
        forkedAgent._sandbox = await _dependencies.SandboxFactory.CreateAsync(_config.SandboxOptions, cancellationToken);
        forkedAgent.InitializeToolServices();

        // Copy message history + derive stepCount (TS: snapshot.metadata.stepCount, else count user turns).
        forkedAgent._messages.AddRange(snapshot.Messages);
        forkedAgent._stepCount = TryReadInt(snapshot.Metadata, "stepCount") ?? forkedAgent._messages.Count(m => m.Role == MessageRole.User);
        forkedAgent._lineage = [.._lineage, AgentId];

        // Persist forked messages to storage (TS: persistMessages()).
        await _dependencies.Store.SaveMessagesAsync(newAgentId, forkedAgent._messages, cancellationToken);

        _logger?.LogInformation("Forked agent {SourceId} to {TargetId}", AgentId, newAgentId);
        return forkedAgent;
    }

    private async Task SaveStateAsync()
    {
        try
        {
            await SaveStateAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save agent state");
        }
    }

    private async Task SaveStateAsync(CancellationToken cancellationToken)
    {
        await _dependencies.Store.SaveMessagesAsync(AgentId, _messages, cancellationToken);
        await _dependencies.Store.SaveToolCallRecordsAsync(AgentId, _toolRunner.ActiveToolCalls, cancellationToken);
        await UpdateInfoAsync(cancellationToken);
    }

    private async Task UpdateInfoAsync()
    {
        try
        {
            await UpdateInfoAsync(CancellationToken.None);
        }
        catch
        {
            // best-effort meta tracking; ignore failures
        }
    }

    private async Task UpdateInfoAsync(CancellationToken cancellationToken)
    {
        try
        {
            var existing = await _dependencies.Store.LoadInfoAsync(AgentId, cancellationToken);

            var info = (existing ?? new AgentInfo
            {
                AgentId = AgentId,
                CreatedAt = _createdAt,
                Lineage = []
            }) with
            {
                TemplateId = _config.TemplateId,
                ConfigVersion = typeof(Agent).Assembly.GetName().Version?.ToString(),
                MessageCount = _messages.Count,
                LastSfpIndex = FindLastSfpIndex(),
                LastBookmark = _eventBus.LastBookmark,
                Breakpoint = _breakpointManager.State,
                Lineage = _lineage,
                Metadata = BuildAgentMetadata(existing?.Metadata)
            };

            await _dependencies.Store.SaveInfoAsync(AgentId, info, cancellationToken);
        }
        catch
        {
            // best-effort meta tracking; ignore failures
        }
    }

    private int FindLastSfpIndex()
    {
        for (var i = _messages.Count - 1; i >= 0; i--)
        {
            var message = _messages[i];
            if (message.Role == MessageRole.User) return i;
            if (message.Role == MessageRole.Assistant && !message.Content.OfType<ToolUseContent>().Any()) return i;
        }

        return -1;
    }

    private static int? TryReadInt(IReadOnlyDictionary<string, JsonElement>? metadata, string key)
    {
        if (metadata == null || metadata.Count == 0) return null;
        if (!metadata.TryGetValue(key, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private IReadOnlyDictionary<string, JsonElement> BuildAgentMetadata(IReadOnlyDictionary<string, JsonElement>? existing)
    {
        var merged = existing != null
            ? new Dictionary<string, JsonElement>(existing, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        merged["model"] = JsonSerializer.SerializeToElement(_config.Model, MetaJsonOptions);
        // TS-aligned: store tool descriptors in metadata.tools, plus a convenience list of tool ids.
        merged["tools"] = JsonSerializer.SerializeToElement(_tools.Select(t => t.ToDescriptor()).ToList(), MetaJsonOptions);
        merged["toolIds"] = JsonSerializer.SerializeToElement(_config.Tools ?? [], MetaJsonOptions);
        merged["sandboxOptions"] = JsonSerializer.SerializeToElement(_config.SandboxOptions, MetaJsonOptions);
        merged["sandboxConfig"] = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["kind"] = "local",
            ["workDir"] = _config.SandboxOptions?.WorkingDirectory,
            ["enforceBoundary"] = _config.SandboxOptions?.EnforceBoundary,
            ["allowPaths"] = _config.SandboxOptions?.AllowPaths,
            ["watchFiles"] = _config.SandboxOptions?.WatchFiles
        }, MetaJsonOptions);
        merged["permission"] = JsonSerializer.SerializeToElement(_config.Permissions, MetaJsonOptions);
        merged["todo"] = JsonSerializer.SerializeToElement(_config.Todo, MetaJsonOptions);
        merged["subagents"] = JsonSerializer.SerializeToElement(_config.SubAgents, MetaJsonOptions);
        merged["context"] = JsonSerializer.SerializeToElement(_config.Context, MetaJsonOptions);
        merged["skills"] = JsonSerializer.SerializeToElement(_config.Skills, MetaJsonOptions);
        merged["exposeThinking"] = JsonSerializer.SerializeToElement(_config.ExposeThinking, MetaJsonOptions);
        merged["maxIterations"] = JsonSerializer.SerializeToElement(_config.MaxIterations, MetaJsonOptions);
        merged["maxTokens"] = JsonSerializer.SerializeToElement(_config.MaxTokens, MetaJsonOptions);
        merged["temperature"] = JsonSerializer.SerializeToElement(_config.Temperature, MetaJsonOptions);
        merged["enableThinking"] = JsonSerializer.SerializeToElement(_config.EnableThinking, MetaJsonOptions);
        merged["thinkingBudget"] = JsonSerializer.SerializeToElement(_config.ThinkingBudget, MetaJsonOptions);
        merged["maxToolConcurrency"] = JsonSerializer.SerializeToElement(_config.MaxToolConcurrency, MetaJsonOptions);
        merged["toolTimeoutMs"] = JsonSerializer.SerializeToElement((int)_config.ToolTimeout.TotalMilliseconds, MetaJsonOptions);

        foreach (var kv in _metadata)
        {
            merged[kv.Key] = kv.Value;
        }

        return merged;
    }
}
