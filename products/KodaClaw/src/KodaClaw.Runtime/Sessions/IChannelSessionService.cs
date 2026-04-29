using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Sessions;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;

namespace KodaClaw.Runtime.Sessions;

public interface IChannelSessionService
{
    Task<ChannelSessionHandle> EnsureChannelSessionAsync(
        ThreadBinding binding,
        ChannelPolicy policy,
        CancellationToken cancellationToken = default);

    Task<ChannelTurnExecutionResult> RunInboundTurnAsync(
        ThreadBinding binding,
        ChannelPolicy policy,
        ChannelEventEnvelope envelope,
        bool hasExplicitMention,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evicts the current agent for the given binding and rotates the binding's session ID
    /// so the next inbound turn starts a fresh session (new directory, clean history).
    /// Returns the newly assigned session ID.
    /// </summary>
    Task<string> RotateSessionAsync(
        ThreadBinding binding,
        string? modelOverride = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the ModelId currently used by the given session, or null if the session
    /// has not been created yet.
    /// </summary>
    Task<string?> GetSessionModelAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to interrupt the currently running agent turn for the given session.
    /// Returns a user-facing status message describing the result.
    /// </summary>
    Task<string> StopCurrentTurnAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Expose lock acquisition for Orchestrator to coordinate concurrent access.
    /// </summary>
    Task<IDisposable> AcquireSessionLockAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Expose prompt building so Orchestrator can construct the inbound turn prompt.
    /// </summary>
    string BuildPrompt(ThreadBinding binding, ChannelEventEnvelope envelope, bool hasExplicitMention);

    /// <summary>
    /// Returns a lightweight snapshot of the agent's current runtime state for the given session.
    /// Returns null if no agent is loaded for the session (session not yet created or already evicted).
    /// </summary>
    /// <param name="cancellationToken">Reserved for future asynchronous state retrieval; currently unused in the synchronous path.</param>
    Task<AgentSessionState?> GetSessionStateAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the list of tool names available in the specified session.
    /// Returns an empty list if the session does not exist.
    /// </summary>
    Task<IReadOnlyList<string>> GetSessionToolNamesAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forces an immediate context compression for the given session.
    /// Returns a user-facing status message describing the result.
    /// </summary>
    Task<string> CompressSessionContextAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the message history for the given session from disk.
    /// Safe to call concurrently while the session is actively running (WAL write strategy).
    /// Returns an empty list if the session has not been created yet or messages cannot be read.
    /// Used by the /btw context-fork path.
    /// </summary>
    Task<IReadOnlyList<Message>> GetSessionMessagesAsync(string sessionId, CancellationToken cancellationToken = default);
}

public sealed record ChannelSessionHandle(
    string BindingId,
    string SessionId,
    SessionKind SessionKind,
    string SessionDirectory,
    bool ResumedFromStore,
    string? ResumeFailureMessage,
    IAgent Agent);
