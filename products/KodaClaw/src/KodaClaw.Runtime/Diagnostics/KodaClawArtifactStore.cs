using Kode.Agent.Sdk.Core.Agent;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Workspace;
using Microsoft.Extensions.Logging;

namespace KodaClaw.Runtime.Diagnostics;

/// <summary>
/// KodaClaw-flavoured <see cref="IArtifactStore"/>: writes offloaded tool-result payloads to
/// <c>cache/artifacts/{sessionId}/</c> (git-excluded, cleaned up together with the session
/// folder by <c>SessionRetentionService</c>) and emits a <c>tool_result.offloaded</c>
/// diagnostic event so Gateway observers can spot runaway offloads.
/// </summary>
internal sealed class KodaClawArtifactStore : IArtifactStore
{
    internal const string ArtifactsCacheSubdirectory = "artifacts";

    private readonly string _workspaceRootPath;
    private readonly IDiagnosticsService? _diagnostics;
    private readonly ILogger<KodaClawArtifactStore>? _logger;

    public KodaClawArtifactStore(
        string workspaceRootPath,
        IDiagnosticsService? diagnostics = null,
        ILogger<KodaClawArtifactStore>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(workspaceRootPath))
            throw new ArgumentException("Workspace root path is required.", nameof(workspaceRootPath));

        _workspaceRootPath = workspaceRootPath;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    public async Task<ArtifactReference> WriteAsync(
        ArtifactWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        var relativePath = Path.Combine(
            KodaClawWorkspaceLayout.CacheDirectory,
            ArtifactsCacheSubdirectory,
            request.SessionId,
            BuildFileName(request.ToolName));
        var absolutePath = Path.Combine(_workspaceRootPath, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllTextAsync(absolutePath, request.Payload, cancellationToken);

        var normalized = NormalizePath(relativePath);
        EmitDiagnostic(request, normalized);

        return new ArtifactReference(normalized, request.Payload.Length);
    }

    private static string BuildFileName(string toolName)
    {
        var safeTool = Sanitize(toolName);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff");
        var suffix = Guid.NewGuid().ToString("N").AsSpan(0, 8).ToString();
        return $"{timestamp}-{safeTool}-{suffix}.json";
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }

    private static string NormalizePath(string p) => p.Replace('\\', '/');

    private void EmitDiagnostic(ArtifactWriteRequest request, string relativePath)
    {
        if (_diagnostics is null) return;
        try
        {
            _diagnostics.Record(new DiagnosticEvent(
                Id: Guid.NewGuid().ToString("N"),
                Source: "koda.runtime.tool_result_compressor",
                EventType: "tool_result.offloaded",
                Level: "info",
                Message: $"Offloaded '{request.ToolName}' result ({request.Payload.Length:N0} bytes) to artifact",
                Timestamp: DateTimeOffset.UtcNow,
                SessionId: request.SessionId,
                Attributes: new Dictionary<string, string?>
                {
                    ["tool"] = request.ToolName,
                    ["bytes"] = request.Payload.Length.ToString(),
                    ["artifactPath"] = relativePath,
                    ["threshold"] = request.EffectiveThreshold.ToString(),
                    ["contextPressure"] = request.ContextPressure.ToString("F3"),
                }));
        }
        catch (Exception ex)
        {
            // Diagnostics must never break tool execution.
            _logger?.LogDebug(ex, "Failed to record tool_result.offloaded diagnostic");
        }
    }
}
