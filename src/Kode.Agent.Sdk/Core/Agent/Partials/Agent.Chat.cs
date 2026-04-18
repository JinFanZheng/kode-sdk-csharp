using Kode.Agent.Sdk.Core.Events;

namespace Kode.Agent.Sdk.Core.Agent;

// TS-aligned chat-shape API. These methods are thin orchestrators over
// Send() + EventBus.SubscribeProgressAsync: ChatStream yields progress envelopes
// until a DoneEvent lands, Chat collects those events into a CompleteResult
// (status + merged assistant text + PermissionIds for paused approvals), and
// Stream / CompleteAsync / SendAsync are name-compat shims matching the JS SDK.
// No state mutation beyond what Send() already performs.
public sealed partial class Agent
{
    public sealed record StreamOptions
    {
        public Bookmark? Since { get; init; }
        public IReadOnlyCollection<string>? Kinds { get; init; }
    }

    public sealed record CompleteResult
    {
        /// <summary>
        /// ok | paused
        /// </summary>
        public required string Status { get; init; }
        public required string Text { get; init; }
        public Bookmark? Last { get; init; }
        public required IReadOnlyList<string> PermissionIds { get; init; }
    }

    /// <summary>
    /// TS-aligned: <c>agent.chatStream(input, { since?, kinds? })</c>.
    /// Streams progress envelopes until a <c>done</c> event is observed.
    /// </summary>
    public IAsyncEnumerable<EventEnvelope<ProgressEvent>> ChatStream(
        string input,
        StreamOptions? opts = null,
        CancellationToken cancellationToken = default)
    {
        return ChatStreamAsync(input, opts, cancellationToken);
    }

    /// <summary>
    /// C# alias for <see cref="ChatStream"/> (kept for existing naming conventions).
    /// </summary>
    public async IAsyncEnumerable<EventEnvelope<ProgressEvent>> ChatStreamAsync(
        string input,
        StreamOptions? opts = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var since = opts?.Since ?? _eventBus.GetLastBookmark();
        Send(input, new SendOptions { Kind = PendingKind.User });

        await foreach (var envelope in _eventBus.SubscribeProgressAsync(since, opts?.Kinds, cancellationToken))
        {
            yield return envelope;
            if (envelope.Event is DoneEvent)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// TS-aligned: <c>agent.chat(input, { since?, kinds? })</c>.
    /// </summary>
    public async Task<CompleteResult> ChatAsync(
        string input,
        StreamOptions? opts = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var streamedText = new System.Text.StringBuilder();
        Bookmark? last = null;

        await foreach (var envelope in ChatStreamAsync(input, opts, cancellationToken))
        {
            if (envelope.Event is TextChunkEvent textChunk)
            {
                if (!string.IsNullOrEmpty(textChunk.Delta))
                {
                    streamedText.Append(textChunk.Delta);
                }
            }
            else if (envelope.Event is DoneEvent)
            {
                last = envelope.Bookmark;
            }
        }

        var pending = _permissionManager.GetPendingApprovalIds();

        var finalText = streamedText.ToString();
        var lastAssistant = _messages.LastOrDefault(m => m.Role == MessageRole.Assistant);
        if (lastAssistant != null)
        {
            var combined = string.Join(
                "\n",
                lastAssistant.Content.OfType<TextContent>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
            if (combined.Trim().Length > 0)
            {
                finalText = combined;
            }
        }

        return new CompleteResult
        {
            Status = pending.Count > 0 ? "paused" : "ok",
            Text = finalText,
            Last = last,
            PermissionIds = pending
        };
    }

    /// <summary>
    /// TS-aligned alias: <c>agent.complete(...)</c>.
    /// </summary>
    public Task<CompleteResult> CompleteAsync(
        string input,
        StreamOptions? opts = null,
        CancellationToken cancellationToken = default)
    {
        return ChatAsync(input, opts, cancellationToken);
    }

    /// <summary>
    /// TS-aligned alias: <c>agent.stream(...)</c>.
    /// </summary>
    public IAsyncEnumerable<EventEnvelope<ProgressEvent>> Stream(
        string input,
        StreamOptions? opts = null,
        CancellationToken cancellationToken = default)
    {
        return ChatStreamAsync(input, opts, cancellationToken);
    }

    /// <summary>
    /// TS-aligned: <c>await agent.send(...)</c> returns messageId.
    /// </summary>
    public Task<string> SendAsync(string text, SendOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Send(text, options));
    }
}
