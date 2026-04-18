namespace Kode.Agent.Sdk.Core.Agent;

/// <summary>
/// Describes a single artifact write requested by a compressor.
/// </summary>
/// <param name="SessionId">Owning agent/session id, used for scoping and cleanup.</param>
/// <param name="ToolName">Tool whose output is being offloaded (used for filenames).</param>
/// <param name="Payload">Serialized payload (typically JSON produced by the compressor).</param>
/// <param name="ContextPressure">
/// Pressure reading the compressor used when deciding to offload; implementations may forward
/// it to structured diagnostics.
/// </param>
/// <param name="EffectiveThreshold">
/// Threshold in bytes after the compressor's pressure-aware scaling.
/// </param>
public readonly record struct ArtifactWriteRequest(
    string SessionId,
    string ToolName,
    string Payload,
    float ContextPressure,
    int EffectiveThreshold);

/// <summary>
/// Handle returned after a successful artifact write. Returned to the agent as part of the
/// compressed <c>ToolResult</c> and used to build follow-up <c>fs_read</c> references.
/// </summary>
/// <param name="RelativePath">
/// Storage-relative path in forward-slash form. Must remain valid after workspace resume on a
/// different machine — absolute paths are disallowed.
/// </param>
/// <param name="SizeBytes">Size of the stored payload in bytes.</param>
public readonly record struct ArtifactReference(string RelativePath, long SizeBytes);

/// <summary>
/// Persists oversized tool results outside the model context window. Implementations own
/// storage layout, retention, and diagnostics. The SDK calls <see cref="WriteAsync"/>;
/// everything else (paths, sinks, quotas) is application-defined.
/// </summary>
public interface IArtifactStore
{
    Task<ArtifactReference> WriteAsync(
        ArtifactWriteRequest request,
        CancellationToken cancellationToken = default);
}
