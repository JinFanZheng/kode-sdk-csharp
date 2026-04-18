using System.Text.Json;

namespace Kode.Agent.Sdk.Core.Agent;

/// <summary>
/// Manages concurrent tool execution with rate limiting.
/// </summary>
public sealed class ToolRunner : IAsyncDisposable
{
    private static readonly JsonSerializerOptions PreviewJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IToolRegistry _toolRegistry;
    private readonly int _maxConcurrency;
    private readonly SemaphoreSlim _semaphore;
    private readonly Dictionary<string, ToolCallRecord> _activeToolCalls = [];
    private readonly System.Threading.Lock _lock = new();

    public ToolRunner(IToolRegistry toolRegistry, int maxConcurrency = 3)
    {
        _toolRegistry = toolRegistry;
        _maxConcurrency = maxConcurrency;
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    /// <summary>
    /// Gets the active tool call records.
    /// </summary>
    public IReadOnlyList<ToolCallRecord> ActiveToolCalls
    {
        get
        {
            lock (_lock)
            {
                return _activeToolCalls.Values.ToList();
            }
        }
    }

    /// <summary>
    /// Loads tool call records into the runner (used when resuming from store).
    /// </summary>
    public void LoadToolCallRecords(IEnumerable<ToolCallRecord> records)
    {
        lock (_lock)
        {
            _activeToolCalls.Clear();
            foreach (var record in records)
            {
                _activeToolCalls[record.Id] = Normalize(record);
            }
        }
    }

    /// <summary>
    /// Registers a tool call record if it doesn't already exist.
    /// </summary>
    public void RegisterToolCall(string callId, string toolName, object arguments)
    {
        lock (_lock)
        {
            if (_activeToolCalls.ContainsKey(callId))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _activeToolCalls[callId] = new ToolCallRecord
            {
                Id = callId,
                Name = toolName,
                Input = arguments,
                State = ToolCallState.Pending,
                Approval = new ToolCallApproval { Required = false },
                IsError = false,
                CreatedAt = now,
                UpdatedAt = now,
                AuditTrail =
                [
                    new ToolCallAuditEntry { State = ToolCallState.Pending, Timestamp = now, Note = "created" }
                ]
            };
        }
    }

    /// <summary>
    /// Updates the state for a tool call record (TS-aligned state machine).
    /// </summary>
    public void SetToolCallState(string callId, ToolCallState state, string? error = null, string? note = null)
    {
        lock (_lock)
        {
            if (!_activeToolCalls.TryGetValue(callId, out var r))
            {
                return;
            }

            // TS-aligned: once a call is sealed/denied, treat it as immutable (avoid races with late tool completions).
            if (r.State is ToolCallState.Sealed or ToolCallState.Denied)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var startedAt = state == ToolCallState.Executing ? (r.StartedAt ?? now) : r.StartedAt;

            var completedAt = state is ToolCallState.Completed or ToolCallState.Failed or ToolCallState.Sealed or ToolCallState.Denied
                ? (r.CompletedAt ?? now)
                : r.CompletedAt;

            var durationMs = completedAt != null && startedAt != null
                ? Math.Max(0, completedAt.Value - startedAt.Value)
                : r.DurationMs;

            var audit = r.AuditTrail?.ToList() ?? [];
            audit.Add(new ToolCallAuditEntry
            {
                State = state,
                Timestamp = now,
                Note = note
            });

            _activeToolCalls[callId] = r with
            {
                State = state,
                Error = error ?? r.Error,
                IsError = state is ToolCallState.Failed or ToolCallState.Denied or ToolCallState.Sealed ? true : r.IsError,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                DurationMs = durationMs,
                UpdatedAt = now,
                AuditTrail = audit
            };
        }
    }

    /// <summary>
    /// Marks a tool call as denied (without executing).
    /// </summary>
    public void DenyToolCall(string callId, string reason)
    {
        SetToolCallState(callId, ToolCallState.Denied, reason, note: "denied");
    }

    /// <summary>
    /// Overrides the final result/state for a tool call record (e.g. after PostToolUse hooks).
    /// </summary>
    public void UpdateFinalResult(string callId, ToolResult result)
    {
        lock (_lock)
        {
            if (_activeToolCalls.TryGetValue(callId, out var r))
            {
                // TS-aligned: do not override sealed/denied calls (e.g. after interrupt/crash recovery).
                if (r.State is ToolCallState.Sealed or ToolCallState.Denied)
                {
                    return;
                }

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var completedAt = r.CompletedAt ?? now;
                var startedAt = r.StartedAt;
                var durationMs = startedAt != null
                    ? Math.Max(0, completedAt - startedAt.Value)
                    : r.DurationMs;
                var audit = r.AuditTrail?.ToList() ?? [];
                audit.Add(new ToolCallAuditEntry
                {
                    State = result.Success ? ToolCallState.Completed : ToolCallState.Failed,
                    Timestamp = now,
                    Note = "final_result"
                });

                _activeToolCalls[callId] = r with
                {
                    State = result.Success ? ToolCallState.Completed : ToolCallState.Failed,
                    Result = result.Value,
                    Error = result.Error,
                    IsError = !result.Success,
                    CompletedAt = completedAt,
                    DurationMs = durationMs,
                    UpdatedAt = now,
                    AuditTrail = audit
                };
            }
        }
    }

    public void MarkApprovalRequired(string callId, JsonElement? meta = null, string? note = null)
    {
        lock (_lock)
        {
            if (!_activeToolCalls.TryGetValue(callId, out var r))
            {
                return;
            }

            if (r.State is ToolCallState.Sealed or ToolCallState.Denied)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var approval = r.Approval with
            {
                Required = true,
                Meta = meta ?? r.Approval.Meta
            };

            var audit = r.AuditTrail?.ToList() ?? [];
            audit.Add(new ToolCallAuditEntry
            {
                State = ToolCallState.ApprovalRequired,
                Timestamp = now,
                Note = note ?? "awaiting approval"
            });

            _activeToolCalls[callId] = r with
            {
                State = ToolCallState.ApprovalRequired,
                Approval = approval,
                UpdatedAt = now,
                AuditTrail = audit
            };
        }
    }

    public void MarkApprovalDecision(string callId, bool allow, string decidedBy, string? note = null)
    {
        lock (_lock)
        {
            if (!_activeToolCalls.TryGetValue(callId, out var r))
            {
                return;
            }

            if (r.State is ToolCallState.Sealed)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var decision = allow ? "allow" : "deny";
            var approval = r.Approval with
            {
                Required = true,
                Decision = decision,
                DecidedBy = decidedBy,
                DecidedAt = now,
                Note = note
            };

            var state = allow ? ToolCallState.Approved : ToolCallState.Denied;
            var audit = r.AuditTrail?.ToList() ?? [];
            audit.Add(new ToolCallAuditEntry
            {
                State = state,
                Timestamp = now,
                Note = allow ? "approval granted" : "approval denied"
            });

            _activeToolCalls[callId] = r with
            {
                State = state,
                Approval = approval,
                Error = allow ? r.Error : (note ?? r.Error),
                IsError = allow ? r.IsError : true,
                CompletedAt = allow ? r.CompletedAt : (r.CompletedAt ?? now),
                UpdatedAt = now,
                AuditTrail = audit
            };
        }
    }

    /// <summary>
    /// Executes a tool call.
    /// </summary>
    public async Task<ToolResult> ExecuteAsync(
        string callId,
        string toolName,
        object arguments,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var tool = _toolRegistry.Get(toolName)
            ?? throw new ToolNotFoundException(toolName);

        RegisterToolCall(callId, toolName, arguments);

        try
        {
            // Wait for semaphore slot
            await _semaphore.WaitAsync(cancellationToken);

            try
            {
                // Update state to executing
                SetToolCallState(callId, ToolCallState.Executing, note: "execution start");

                // Execute the tool
                var result = await tool.ExecuteAsync(arguments, context, cancellationToken);

                // Update record with result
                lock (_lock)
                {
                    if (_activeToolCalls.TryGetValue(callId, out var r))
                    {
                        if (r.State is ToolCallState.Sealed or ToolCallState.Denied)
                        {
                            return result;
                        }

                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var completedAt = now;
                        var startedAt = r.StartedAt;
                        var durationMs = completedAt != 0 && startedAt != null
                            ? Math.Max(0, completedAt - startedAt.Value)
                            : r.DurationMs;
                        var audit = r.AuditTrail?.ToList() ?? [];
                        audit.Add(new ToolCallAuditEntry
                        {
                            State = result.Success ? ToolCallState.Completed : ToolCallState.Failed,
                            Timestamp = now,
                            Note = "execution end"
                        });

                        _activeToolCalls[callId] = r with
                        {
                            State = result.Success ? ToolCallState.Completed : ToolCallState.Failed,
                            Result = result.Value,
                            Error = result.Error,
                            IsError = !result.Success,
                            CompletedAt = completedAt,
                            DurationMs = durationMs,
                            UpdatedAt = now,
                            AuditTrail = audit
                        };
                    }
                }

                return result;
            }
            finally
            {
                _semaphore.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var errorResult = ToolResult.Fail(ex.Message);

            lock (_lock)
            {
                if (_activeToolCalls.TryGetValue(callId, out var r))
                {
                    if (r.State is ToolCallState.Sealed or ToolCallState.Denied)
                    {
                        return errorResult;
                    }

                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var completedAt = now;
                    var startedAt = r.StartedAt;
                    var durationMs = completedAt != 0 && startedAt != null
                        ? Math.Max(0, completedAt - startedAt.Value)
                        : r.DurationMs;
                    var audit = r.AuditTrail?.ToList() ?? [];
                    audit.Add(new ToolCallAuditEntry
                    {
                        State = ToolCallState.Failed,
                        Timestamp = now,
                        Note = "execution exception"
                    });

                    _activeToolCalls[callId] = r with
                    {
                        State = ToolCallState.Failed,
                        Error = ex.Message,
                        IsError = true,
                        CompletedAt = completedAt,
                        DurationMs = durationMs,
                        UpdatedAt = now,
                        AuditTrail = audit
                    };
                }
            }

            return errorResult;
        }
    }

    /// <summary>
    /// Executes multiple tool calls in parallel (up to max concurrency).
    /// </summary>
    public async Task<IReadOnlyList<(string CallId, ToolResult Result)>> ExecuteParallelAsync(
        IEnumerable<(string CallId, string ToolName, object Arguments)> toolCalls,
        ToolContext baseContext,
        CancellationToken cancellationToken = default)
    {
        var tasks = toolCalls.Select(async tc =>
        {
            var context = baseContext with { CallId = tc.CallId };
            var result = await ExecuteAsync(tc.CallId, tc.ToolName, tc.Arguments, context, cancellationToken);
            return (tc.CallId, result);
        });

        var results = await Task.WhenAll(tasks);
        return results;
    }

    /// <summary>
    /// Seals a tool call as failed (for crash recovery / interrupt).
    /// TS-aligned: also persists a structured <c>result</c> payload when provided.
    /// </summary>
    public void SealToolCall(string callId, string reason, object? result = null)
    {
        lock (_lock)
        {
            if (_activeToolCalls.TryGetValue(callId, out var record))
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var audit = record.AuditTrail?.ToList() ?? [];
                audit.Add(new ToolCallAuditEntry
                {
                    State = ToolCallState.Sealed,
                    Timestamp = now,
                    Note = "sealed"
                });

                _activeToolCalls[callId] = record with
                {
                    State = ToolCallState.Sealed,
                    Result = result ?? record.Result,
                    Error = reason,
                    IsError = true,
                    CompletedAt = record.CompletedAt ?? now,
                    UpdatedAt = now,
                    AuditTrail = audit
                };
            }
        }
    }

    /// <summary>
    /// Gets a tool call record by ID.
    /// </summary>
    public ToolCallRecord? GetToolCall(string callId)
    {
        lock (_lock)
        {
            return _activeToolCalls.GetValueOrDefault(callId);
        }
    }

    public ToolCallSnapshot? GetSnapshot(string callId, int inputPreviewLimit = 200)
    {
        ToolCallRecord? record;
        lock (_lock)
        {
            record = _activeToolCalls.GetValueOrDefault(callId);
        }

        if (record == null) return null;

        return new ToolCallSnapshot
        {
            Id = record.Id,
            Name = record.Name,
            State = record.State,
            Approval = record.Approval,
            Result = record.Result,
            Error = record.Error,
            IsError = record.IsError,
            DurationMs = record.DurationMs,
            StartedAt = record.StartedAt,
            CompletedAt = record.CompletedAt,
            InputPreview = Preview(record.Input, inputPreviewLimit),
            AuditTrail = record.AuditTrail?.ToList()
        };
    }

    /// <summary>
    /// Clears completed tool calls.
    /// </summary>
    public void ClearCompleted()
    {
        lock (_lock)
        {
            var completed = _activeToolCalls
                .Where(kv => kv.Value.State is ToolCallState.Completed or ToolCallState.Failed or ToolCallState.Sealed or ToolCallState.Denied)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var id in completed)
            {
                _activeToolCalls.Remove(id);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _semaphore.Dispose();
        return ValueTask.CompletedTask;
    }

    private static ToolCallRecord Normalize(ToolCallRecord record)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var createdAt = record.CreatedAt != 0 ? record.CreatedAt : (record.StartedAt ?? now);
        var updatedAt = record.UpdatedAt != 0 ? record.UpdatedAt : (record.CompletedAt ?? record.StartedAt ?? createdAt);

        var audit = record.AuditTrail?.ToList() ?? [];
        if (audit.Count == 0)
        {
            audit.Add(new ToolCallAuditEntry
            {
                State = record.State,
                Timestamp = updatedAt,
                Note = "normalized"
            });
        }

        var durationMs = record.DurationMs;
        if (durationMs == null && record.StartedAt != null && record.CompletedAt != null)
        {
            durationMs = Math.Max(0, record.CompletedAt.Value - record.StartedAt.Value);
        }

        return record with
        {
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            DurationMs = durationMs,
            AuditTrail = audit
        };
    }

    private static string Preview(object value, int limit)
    {
        try
        {
            var text = value is string s ? s : JsonSerializer.Serialize(value, PreviewJsonOptions);
            if (text.Length <= limit) return text;
            return text[..limit] + "…";
        }
        catch
        {
            return "[unavailable]";
        }
    }
}
