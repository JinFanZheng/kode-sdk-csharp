using System.Runtime.CompilerServices;
using KodaClaw.Contracts.Chat;
using KodaClaw.Contracts.System;
using KodaClaw.Workspace;
using KodaClaw.Workspace.Media;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using AgentRuntime = Kode.Agent.Sdk.Core.Agent.Agent;

namespace KodaClaw.Runtime.Sessions;

public sealed class ChatSessionService : IChatSessionService
{
    private const string RuntimeErrorCode = "runtime.error";
    private readonly IMainSessionService _mainSessionService;
    private readonly IMediaStore? _mediaStore;

    public ChatSessionService(IMainSessionService mainSessionService, IMediaStore? mediaStore = null)
    {
        _mainSessionService = mainSessionService ?? throw new ArgumentNullException(nameof(mainSessionService));
        _mediaStore = mediaStore;
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamMainSessionAsync(
        ChatStreamRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Message) && request.MediaIds is not { Count: > 0 })
        {
            throw new ArgumentException("Message or media attachment is required.", nameof(request));
        }

        MainSessionHandle? handle;
        AgentRuntime agent;
        string? startupError = null;

        try
        {
            handle = await _mainSessionService.EnsureMainSessionAsync(cancellationToken);
            agent = handle.Agent as AgentRuntime
                ?? throw new InvalidOperationException(
                    $"Main session agent must be a {typeof(AgentRuntime).FullName} instance.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            startupError = ex.Message;
            handle = null;
            agent = null!;
        }

        if (startupError is not null)
        {
            yield return CreateErrorEvent(request.SessionId ?? "main-unavailable", startupError);
            yield break;
        }

        var sessionId = handle!.SessionId;

        // Notify the frontend that a workspace-triggered rotation occurred so it can show a
        // visual boundary between the old and new conversation context.
        if (handle.WasRotatedForWorkspace)
        {
            yield return new ChatStreamEvent(
                Type: "session_rotated",
                SessionId: sessionId,
                Reason: "workspace_updated");
        }

        // Build content blocks for multimodal messages
        var contentBlocks = await BuildContentBlocksAsync(request, cancellationToken);

        var stream = agent.Subscribe(
            channels: ["progress", "monitor", "control"],
            opts: new AgentRuntime.SubscribeOptions
            {
                Since = agent.EventBus.GetLastBookmark(),
                Kinds = ["text_chunk", "think_chunk_start", "think_chunk", "think_chunk_end",
                         "done", "error", "tool:start", "tool:end", "permission_required", "permission_decided",
                         "subagent.created", "subagent.tool_start", "subagent.tool_end", "model:retrying"],
            },
            cancellationToken: cancellationToken);

        await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);
        var moveNextTask = enumerator.MoveNextAsync().AsTask();

        var runOptions = (request.EnableThinking.HasValue || request.ThinkingBudget.HasValue)
            ? new AgentRunOptions
            {
                EnableThinking = request.EnableThinking,
                ThinkingBudget = request.ThinkingBudget,
                // 开启 thinking 就暴露给前端渲染（用户意图一致）
                ExposeThinking = request.EnableThinking,
            }
            : null;

        Task<AgentRunResult> runTask = (contentBlocks is { Count: > 1 })
            ? (runOptions is not null
                ? Task.Run(() => agent.RunAsync(contentBlocks, runOptions, cancellationToken), cancellationToken)
                : Task.Run(() => agent.RunAsync(contentBlocks, cancellationToken), cancellationToken))
            : (runOptions is not null
                ? Task.Run(() => agent.RunAsync(request.Message, runOptions, cancellationToken), cancellationToken)
                : Task.Run(() => agent.RunAsync(request.Message, cancellationToken), cancellationToken));

        _ = runTask.ContinueWith(
            t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        while (true)
        {
            bool hasNext;
            string? streamError = null;
            try
            {
                hasNext = await moveNextTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                hasNext = false;
                streamError = ex.Message;
            }

            if (streamError is not null)
            {
                yield return CreateErrorEvent(sessionId, streamError);
                yield break;
            }

            if (!hasNext)
            {
                yield break;
            }

            var envelope = enumerator.Current;
            switch (envelope.Event)
            {
                case TextChunkEvent textChunk:
                    yield return new ChatStreamEvent(
                        Type: "text_chunk",
                        SessionId: sessionId,
                        Step: textChunk.Step,
                        Sequence: envelope.Bookmark.Seq,
                        Timestamp: envelope.Bookmark.Timestamp,
                        Delta: textChunk.Delta);
                    break;

                case ThinkChunkStartEvent thinkStart:
                    yield return new ChatStreamEvent(
                        Type: "think_chunk_start",
                        SessionId: sessionId,
                        Step: thinkStart.Step,
                        Sequence: envelope.Bookmark.Seq,
                        Timestamp: envelope.Bookmark.Timestamp);
                    break;

                case ThinkChunkEvent thinkChunk:
                    yield return new ChatStreamEvent(
                        Type: "think_chunk",
                        SessionId: sessionId,
                        Step: thinkChunk.Step,
                        Sequence: envelope.Bookmark.Seq,
                        Timestamp: envelope.Bookmark.Timestamp,
                        ThinkingDelta: thinkChunk.Delta);
                    break;

                case ThinkChunkEndEvent thinkEnd:
                    yield return new ChatStreamEvent(
                        Type: "think_chunk_end",
                        SessionId: sessionId,
                        Step: thinkEnd.Step,
                        Sequence: envelope.Bookmark.Seq,
                        Timestamp: envelope.Bookmark.Timestamp);
                    break;

                case ToolStartEvent toolStart:
                    yield return new ChatStreamEvent(
                        Type: "agent_working",
                        SessionId: sessionId,
                        Timestamp: envelope.Bookmark.Timestamp,
                        ToolName: toolStart.Call.Name);
                    break;

                case ToolEndEvent toolEnd:
                {
                    var toolInputRaw = toolEnd.Call.InputPreview switch
                    {
                        string s => s,
                        System.Text.Json.JsonElement j => j.GetRawText(),
                        { } o => o.ToString(),
                        _ => null,
                    };
                    var toolInputPreview = toolInputRaw is { Length: > 2000 } ? toolInputRaw[..2000] : toolInputRaw;
                    yield return new ChatStreamEvent(
                        Type: "tool_activity",
                        SessionId: sessionId,
                        Timestamp: envelope.Bookmark.Timestamp,
                        ToolName: toolEnd.Call.Name,
                        CallId: toolEnd.Call.Id,
                        DurationMs: toolEnd.Call.DurationMs,
                        InputPreview: toolInputPreview);
                    break;
                }

                case PermissionRequiredEvent permRequired:
                {
                    var inputRaw = permRequired.Call.InputPreview switch
                    {
                        string s => s,
                        System.Text.Json.JsonElement j => j.GetRawText(),
                        { } o => o.ToString(),
                        _ => null,
                    };
                    var inputPreview = inputRaw is { Length: > 2000 } ? inputRaw[..2000] : inputRaw;
                    var approvalId = _mainSessionService.TryGetApprovalIdForCall(permRequired.Call.Id)
                        ?? $"approval-{permRequired.Call.Id}";
                    yield return new ChatStreamEvent(
                        Type: "approval_required",
                        SessionId: sessionId,
                        Timestamp: envelope.Bookmark.Timestamp,
                        ApprovalId: approvalId,
                        CallId: permRequired.Call.Id,
                        ToolName: permRequired.Call.Name,
                        InputPreview: inputPreview);
                    break;
                }

                case PermissionDecidedEvent permDecided:
                {
                    var approvalId = _mainSessionService.TryGetApprovalIdForCall(permDecided.CallId)
                        ?? $"approval-{permDecided.CallId}";
                    yield return new ChatStreamEvent(
                        Type: "approval_decided",
                        SessionId: sessionId,
                        Timestamp: envelope.Bookmark.Timestamp,
                        ApprovalId: approvalId,
                        CallId: permDecided.CallId,
                        Decision: permDecided.Decision);
                    break;
                }

                case SubAgentCreatedEvent subCreated:
                    yield return new ChatStreamEvent(
                        Type: "subagent_start",
                        SessionId: sessionId,
                        Timestamp: envelope.Bookmark.Timestamp,
                        SubAgentId: subCreated.AgentId,
                        Label: subCreated.TemplateId);
                    break;

                case SubAgentToolStartEvent subToolStart:
                    yield return new ChatStreamEvent(
                        Type: "subagent_working",
                        SessionId: sessionId,
                        Timestamp: envelope.Bookmark.Timestamp,
                        SubAgentId: subToolStart.SubAgentId,
                        SubAgentToolName: subToolStart.ToolName);
                    break;

                case SubAgentToolEndEvent subToolEnd:
                    yield return new ChatStreamEvent(
                        Type: "subagent_tool_done",
                        SessionId: sessionId,
                        Timestamp: envelope.Bookmark.Timestamp,
                        SubAgentId: subToolEnd.SubAgentId,
                        SubAgentToolName: subToolEnd.ToolName);
                    break;

                case DoneEvent done:
                    yield return new ChatStreamEvent(
                        Type: "done",
                        SessionId: sessionId,
                        Step: done.Step,
                        Sequence: envelope.Bookmark.Seq,
                        Timestamp: envelope.Bookmark.Timestamp,
                        Reason: done.Reason);
                    yield break;

                case ModelRetryingEvent retrying:
                    yield return new ChatStreamEvent(
                        Type: "model_retrying",
                        SessionId: sessionId,
                        Timestamp: envelope.Bookmark.Timestamp,
                        Reason: $"[{retrying.Provider}] attempt {retrying.Attempt}/{retrying.MaxRetries}, retry in {retrying.DelaySeconds:F1}s");
                    break;

                case ErrorEvent error
                    when string.Equals(error.Severity, "warn", StringComparison.Ordinal):
                    // Non-fatal: tool failure or processing restart — agent continues.
                    yield return new ChatStreamEvent(
                        Type: "tool_warning",
                        SessionId: sessionId,
                        Timestamp: envelope.Bookmark.Timestamp,
                        Reason: error.Message);
                    break;

                case ErrorEvent error:
                    yield return CreateErrorEvent(sessionId, error.Message);
                    yield break;
            }

            moveNextTask = enumerator.MoveNextAsync().AsTask();
        }
    }

    private async Task<IReadOnlyList<ContentBlock>?> BuildContentBlocksAsync(
        ChatStreamRequest request,
        CancellationToken cancellationToken)
    {
        if (_mediaStore is null || request.MediaIds is not { Count: > 0 })
            return null;

        var blocks = new List<ContentBlock>();

        foreach (var mediaId in request.MediaIds)
        {
            var meta = await _mediaStore.GetMetaAsync(mediaId, cancellationToken);
            if (meta is null)
                continue; // skip missing media, do not abort the whole message

            var stream = await _mediaStore.OpenReadAsync(mediaId, cancellationToken);
            if (stream is null)
                continue;

            await using (stream)
            {
                using var ms = new System.IO.MemoryStream();
                await stream.CopyToAsync(ms, cancellationToken);
                var base64 = Convert.ToBase64String(ms.ToArray());
                blocks.Add(ImageContent.FromBase64(meta.ContentType, base64));
            }
        }

        if (blocks.Count == 0)
            return null;

        blocks.Add(new TextContent { Text = request.Message });
        return blocks;
    }

    private static ChatStreamEvent CreateErrorEvent(string sessionId, string message)
    {
        return new ChatStreamEvent(
            Type: "error",
            SessionId: sessionId,
            Reason: message,
            Error: new ErrorResponse(
                Code: RuntimeErrorCode,
                Message: message));
    }
}
