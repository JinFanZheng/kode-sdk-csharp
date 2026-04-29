using KodaClaw.Contracts.Sessions;
using Kode.Agent.Sdk.Core.Abstractions;

namespace KodaClaw.Runtime.Sessions;

public interface IMainSessionService
{
    Task<MainSessionHandle> EnsureMainSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disposes the current main session and clears the active session pointer,
    /// so the next call to <see cref="EnsureMainSessionAsync"/> creates a fresh session.
    /// Workspace memory files (MEMORY.md, daily logs, etc.) are not affected.
    /// </summary>
    /// <returns>The session ID of the session that was rotated out, or null if no active session existed.</returns>
    Task<string?> RotateMainSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disposes the current main session and sets the active session pointer to the given session ID,
    /// so the next call to <see cref="EnsureMainSessionAsync"/> resumes from the target session store.
    /// </summary>
    /// <returns>Response containing the resumed session ID.</returns>
    Task<ResumeSessionResponse> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks that the workspace was updated during the current turn.
    /// The next call to <see cref="EnsureMainSessionAsync"/> will auto-rotate to reload the workspace.
    /// </summary>
    void RequestWorkspaceRotation();

    /// <summary>
    /// Resolves the approvalId for the given tool call ID, or null if not found.
    /// Used by <see cref="ChatSessionService"/> to emit inline approval SSE events.
    /// </summary>
    string? TryGetApprovalIdForCall(string callId);

    Task<ApprovalDecisionDispatchResult> ApproveApprovalAsync(
        string approvalId,
        CancellationToken cancellationToken = default);

    Task<ApprovalDecisionDispatchResult> RejectApprovalAsync(
        string approvalId,
        string? note = null,
        CancellationToken cancellationToken = default);
}

public sealed record MainSessionHandle(
    string SessionId,
    SessionKind SessionKind,
    string SessionDirectory,
    bool ResumedFromStore,
    string? ResumeFailureMessage,
    IAgent Agent,
    bool WasRotatedForWorkspace = false);
