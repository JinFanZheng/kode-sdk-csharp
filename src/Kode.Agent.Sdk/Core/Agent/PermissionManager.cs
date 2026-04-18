using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kode.Agent.Sdk.Core.Agent;

/// <summary>
/// Manages tool execution permissions.
/// </summary>
public sealed class PermissionManager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Shell metacharacters that always require approval regardless of command whitelist.
    private static readonly string[] ShellMetachars = [";", "&&", "||", "`", "$(", " | ", ">", ">>", "<"];

    private readonly IEventBus _eventBus;
    private readonly PermissionConfig _config;
    private readonly Dictionary<string, ToolDescriptor> _descriptors;
    private readonly ToolRunner? _toolRunner;
    private readonly Func<CancellationToken, Task>? _persistAsync;
    private readonly Dictionary<string, TaskCompletionSource<bool>> _pendingApprovals = [];
    private readonly System.Threading.Lock _lock = new();
    private readonly HashSet<string>? _allowTools;
    private readonly HashSet<string>? _denyTools;
    private readonly HashSet<string>? _requireApprovalTools;
    private readonly HashSet<string> _schemaHiddenTools;
    // Per-tool command prefix constraints granted by skills. null list = no constraint (all allowed).
    private readonly Dictionary<string, HashSet<string>> _commandConstraints = new(StringComparer.OrdinalIgnoreCase);

    public PermissionManager(
        IEventBus eventBus,
        PermissionConfig? config,
        IEnumerable<ToolDescriptor> toolDescriptors,
        ToolRunner? toolRunner = null,
        Func<CancellationToken, Task>? persistAsync = null)
    {
        _eventBus = eventBus;
        _config = config ?? new PermissionConfig();
        _descriptors = toolDescriptors.ToDictionary(d => d.Name, d => d, StringComparer.OrdinalIgnoreCase);
        _toolRunner = toolRunner;
        _persistAsync = persistAsync;
        _allowTools = BuildToolSet(_config.AllowTools);
        _denyTools = BuildToolSet(_config.DenyTools);
        _requireApprovalTools = BuildToolSet(_config.RequireApprovalTools);
        _schemaHiddenTools = new HashSet<string>(
            _config.SchemaHiddenTools ?? [],
            StringComparer.OrdinalIgnoreCase);

        // Listen for permission decisions
        _eventBus.OnControl<PermissionDecidedEvent>(OnPermissionDecided);
    }

    /// <summary>
    /// TS-aligned permission evaluation: returns "allow" | "deny" | "ask".
    /// </summary>
    public string Evaluate(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return PermissionModes.DecisionDeny;

        if (_denyTools?.Contains(toolName) == true)
        {
            return PermissionModes.DecisionDeny;
        }

        if (_allowTools is { Count: > 0 } &&
            !_allowTools.Contains("*") &&
            !_allowTools.Contains(toolName))
        {
            return PermissionModes.DecisionDeny;
        }

        if (_requireApprovalTools?.Contains(toolName) == true)
        {
            return PermissionModes.DecisionAsk;
        }

        var handler = PermissionModes.Registry.Get(_config.Mode) ?? PermissionModes.Registry.Get("auto");
        if (handler == null)
        {
            return PermissionModes.DecisionAllow;
        }

        _descriptors.TryGetValue(toolName, out var descriptor);

        return handler(new PermissionModes.PermissionEvaluationContext
        {
            ToolName = toolName,
            Descriptor = descriptor,
            Config = _config
        });
    }

    /// <summary>
    /// Returns true if the tool call should be denied immediately (e.g. deny list / not in allowlist).
    /// </summary>
    public bool IsDenied(string toolName, out string reason)
    {
        var decision = Evaluate(toolName);
        if (!string.Equals(decision, PermissionModes.DecisionDeny, StringComparison.OrdinalIgnoreCase))
        {
            reason = "";
            return false;
        }

        if (_denyTools?.Contains(toolName) == true)
        {
            reason = "Tool denied by policy";
            return true;
        }

        if (_allowTools is { Count: > 0 } && !_allowTools.Contains("*"))
        {
            reason = "Tool denied by allowlist";
            return true;
        }

        reason = "Tool denied";
        return true;
    }

    /// <summary>
    /// Checks if a tool call requires approval.
    /// Optionally accepts tool input to evaluate command-level constraints (e.g. bash_run whitelist).
    /// </summary>
    public bool RequiresApproval(string toolName, object? input = null)
    {
        var decision = Evaluate(toolName);

        // Denied tools should be handled separately (do not pause for approval).
        if (string.Equals(decision, PermissionModes.DecisionDeny, StringComparison.OrdinalIgnoreCase))
            return false;

        // Command-level check for bash_run: if command matches skill-granted prefix whitelist
        // AND contains no shell metacharacters → bypass approval.
        if (input != null && string.Equals(toolName, "bash_run", StringComparison.OrdinalIgnoreCase))
        {
            var command = ExtractBashCommand(input);
            if (command != null && IsCommandWhitelisted(toolName, command))
                return false;
        }

        return string.Equals(decision, PermissionModes.DecisionAsk, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true if the given shell command matches the skill-granted prefix whitelist
    /// for this tool AND contains no shell metacharacters.
    /// </summary>
    public bool IsCommandWhitelisted(string toolName, string command)
    {
        if (ContainsShellMetachar(command)) return false;

        lock (_lock)
        {
            if (!_commandConstraints.TryGetValue(toolName, out var prefixes)) return false;
            var trimmed = command.TrimStart();
            var firstToken = Path.GetFileName(trimmed.Split(' ', 2)[0]);
            foreach (var prefix in prefixes)
            {
                if (prefix.Contains(' '))
                {
                    // Multi-word prefix (e.g. "npx agent-browser"): command must start with the full phrase.
                    if (trimmed.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                        || trimmed.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (firstToken.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Returns true if the command contains shell metacharacters that could allow injection.
    /// </summary>
    public static bool ContainsShellMetachar(string command)
    {
        foreach (var meta in ShellMetachars)
        {
            if (command.Contains(meta, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Requests approval for a tool call.
    /// </summary>
    /// <returns>True if approved, false if denied.</returns>
    public async Task<bool> RequestApprovalAsync(
        string callId,
        string toolName,
        object arguments,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var inputPreview = BuildInputPreview(arguments, 1200);

        // Hard deny: denied by policy (do not emit control events; align with TS policy deny).
        if (IsDenied(toolName, out _)) return false;

        // Create completion source for this approval
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _pendingApprovals[callId] = tcs;
        }

        try
        {
            var meta = BuildApprovalMeta(toolName, arguments, reason);

            // TS-aligned: persist pending approval state on the tool record (so resume can reconstruct if needed).
            _toolRunner?.MarkApprovalRequired(callId, meta, reason);
            var snapshot = _toolRunner?.GetSnapshot(callId);
            snapshot ??= new ToolCallSnapshot
            {
                Id = callId,
                Name = toolName,
                State = ToolCallState.ApprovalRequired,
                Approval = new ToolCallApproval { Required = true, Meta = meta },
                InputPreview = inputPreview
            };

            // Emit permission required event
            _eventBus.EmitControl(new PermissionRequiredEvent
            {
                Type = "permission_required",
                Call = snapshot,
                Respond = async (decision, opts) =>
                {
                    if (string.Equals(decision, "allow", StringComparison.OrdinalIgnoreCase))
                    {
                        Approve(callId);
                        await Task.CompletedTask;
                        return;
                    }

                    Deny(callId, opts?.Note);
                    await Task.CompletedTask;
                }
            });

            // Persist the paused/awaiting-approval state (TS-aligned: tool records + meta are durable during approval).
            try
            {
                if (_persistAsync != null)
                {
                    await _persistAsync(cancellationToken);
                }
            }
            catch
            {
                // best-effort persistence; ignore failures
            }

            // Wait for decision. Cancel the sentinel delay on every exit path so the
            // WhenAny-backing Task.Delay doesn't linger as an orphan until GC.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delayTask = Task.Delay(Timeout.Infinite, cts.Token);
            try
            {
                var completedTask = await Task.WhenAny(tcs.Task, delayTask);

                if (completedTask == tcs.Task)
                {
                    return await tcs.Task;
                }

                throw new OperationCanceledException(cancellationToken);
            }
            finally
            {
                cts.Cancel();
            }
        }
        finally
        {
            lock (_lock)
            {
                _pendingApprovals.Remove(callId);
            }
        }
    }

    /// <summary>
    /// Approves a pending tool call.
    /// </summary>
    public void Approve(string callId)
    {
        _eventBus.EmitControl(new PermissionDecidedEvent
        {
            Type = "permission_decided",
            CallId = callId,
            Decision = "allow",
            DecidedBy = "api"
        });
    }

    /// <summary>
    /// Denies a pending tool call.
    /// </summary>
    public void Deny(string callId, string? reason = null)
    {
        _eventBus.EmitControl(new PermissionDecidedEvent
        {
            Type = "permission_decided",
            CallId = callId,
            Decision = "deny",
            DecidedBy = "api",
            Note = reason
        });
    }

    private void OnPermissionDecided(PermissionDecidedEvent evt)
    {
        TaskCompletionSource<bool>? tcs;
        lock (_lock)
        {
            _pendingApprovals.TryGetValue(evt.CallId, out tcs);
        }

        try
        {
            var approved = string.Equals(evt.Decision, "allow", StringComparison.OrdinalIgnoreCase);
            _toolRunner?.MarkApprovalDecision(evt.CallId, approved, evt.DecidedBy, evt.Note);
        }
        catch
        {
            // best-effort: do not block approval flow on persistence/audit
        }

        tcs?.TrySetResult(string.Equals(evt.Decision, "allow", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Grants additional tools at runtime (e.g. from activated skills).
    /// Supports plain tool names ("fs_read") and command-constraint specs ("bash_run[kc]").
    /// No-op on allowlist when no allowlist is configured (all tools already permitted).
    /// Thread-safe.
    /// </summary>
    public void GrantTools(IEnumerable<string> toolNames)
    {
        lock (_lock)
        {
            foreach (var tool in toolNames)
            {
                if (string.IsNullOrWhiteSpace(tool)) continue;
                var spec = tool.Trim();

                // Parse constraint spec: "bash_run[kc]" → toolName="bash_run", prefix="kc"
                var bracketOpen = spec.IndexOf('[');
                if (bracketOpen > 0 && spec.EndsWith(']'))
                {
                    var toolName = spec[..bracketOpen];
                    var prefix = spec[(bracketOpen + 1)..^1];
                    _allowTools?.Add(toolName);
                    _schemaHiddenTools.Remove(toolName);
                    if (!string.IsNullOrEmpty(prefix))
                    {
                        if (!_commandConstraints.TryGetValue(toolName, out var set))
                        {
                            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            _commandConstraints[toolName] = set;
                        }
                        set.Add(prefix);
                    }
                }
                else
                {
                    _allowTools?.Add(spec);
                    _schemaHiddenTools.Remove(spec);
                }
            }
        }
    }

    /// <summary>
    /// Returns true if the tool's schema should be included in the model request.
    /// Tools hidden via SchemaHiddenTools are excluded until revealed by GrantTools().
    /// </summary>
    public bool IsSchemaVisible(string toolName) =>
        !_schemaHiddenTools.Contains(toolName);

    /// <summary>
    /// Gets current pending approval call IDs.
    /// </summary>
    public IReadOnlyList<string> GetPendingApprovalIds()
    {
        lock (_lock)
        {
            return _pendingApprovals.Keys.ToList();
        }
    }

    private static string? ExtractBashCommand(object input)
    {
        try
        {
            if (input is JsonElement element)
            {
                if (element.TryGetProperty("command", out var cmd))
                    return cmd.GetString();
                if (element.TryGetProperty("Command", out cmd))
                    return cmd.GetString();
            }
            // Fallback: serialize and extract
            var json = JsonSerializer.SerializeToElement(input, JsonOptions);
            if (json.TryGetProperty("command", out var c) || json.TryGetProperty("Command", out c))
                return c.GetString();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PermissionManager] Failed to extract bash command: {ex.Message}");
        }
        return null;
    }

    private static HashSet<string>? BuildToolSet(IReadOnlyList<string>? tools)
    {
        if (tools == null || tools.Count == 0) return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tools)
        {
            if (!string.IsNullOrWhiteSpace(t))
            {
                set.Add(t.Trim());
            }
        }
        return set.Count > 0 ? set : null;
    }

    private static string? BuildInputPreview(object arguments, int maxLen)
    {
        try
        {
            var json = arguments is JsonElement element
                ? element.GetRawText()
                : JsonSerializer.Serialize(arguments, JsonOptions);

            if (json.Length <= maxLen) return json;
            return json[..maxLen] + "…";
        }
        catch
        {
            return null;
        }
    }

    private JsonElement? BuildApprovalMeta(string toolName, object arguments, string? reason)
    {
        try
        {
            var meta = new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["mode"] = _config.Mode,
                ["tool"] = toolName,
            };

            return JsonSerializer.SerializeToElement(meta, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
