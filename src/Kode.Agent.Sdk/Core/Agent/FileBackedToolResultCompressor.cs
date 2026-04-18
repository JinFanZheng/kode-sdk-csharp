using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Kode.Agent.Sdk.Core.Agent;

/// <summary>
/// Non-lossy compressor that offloads oversized tool results to an
/// <see cref="IArtifactStore"/> and returns a short placeholder referencing
/// the stored artifact. Unlike <see cref="LlmToolResultCompressor"/> this does
/// not involve an extra model call: the full payload remains retrievable
/// through <c>fs_read</c> on the returned artifact path.
/// <para>
/// Design rules:
/// <list type="bullet">
/// <item><description>Tools whose output is consumed verbatim by follow-up
/// calls (fs_read → fs_edit, fs_grep …) are excluded via
/// <see cref="VerbatimToolPolicy"/>.</description></item>
/// <item><description>Storage layout, retention and diagnostics are delegated
/// to <see cref="IArtifactStore"/>. The SDK does not know where bytes land.</description></item>
/// <item><description>The threshold scales with <c>contextPressure</c>: more
/// aggressive under pressure, more permissive when the window is mostly empty.</description></item>
/// <item><description>Preview is JSON-aware: structured results get a parseable
/// JSON fragment instead of a byte-sliced string that may end mid-token.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class FileBackedToolResultCompressor : IToolResultCompressor
{
    /// <summary>Bytes of payload kept inline as preview for the agent.</summary>
    public const int PreviewBytes = 2_048;

    // Indented intentionally: written artifacts are normally consumed via fs_read,
    // whose per-line limits only protect the context window when JSON has real line
    // breaks. Compact JSON would serialize to a single very-long line and defeat that.
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IArtifactStore _store;
    private readonly string _sessionId;
    private readonly ILogger<FileBackedToolResultCompressor>? _logger;

    public FileBackedToolResultCompressor(
        IArtifactStore store,
        string sessionId,
        ILogger<FileBackedToolResultCompressor>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _logger = logger;
    }

    public async Task<ToolResult> CompressIfNeededAsync(
        string toolName,
        ToolResult result,
        IReadOnlyList<Message> recentMessages,
        ToolResultCompressionOptions options,
        float contextPressure = 0f,
        CancellationToken cancellationToken = default)
    {
        _ = recentMessages;

        if (!result.Success)
            return result;

        if (VerbatimToolPolicy.IsVerbatim(toolName, options.VerbatimTools))
            return result;

        var serialized = Serialize(result.Value);
        var effectiveThreshold = ScaleThreshold(options.ThresholdBytes, contextPressure);
        if (serialized.Length <= effectiveThreshold)
            return result;

        // Defensive cap: a single runaway tool call should not produce a multi-megabyte
        // artifact. Inline-truncate instead; agent still sees a short, valid placeholder.
        if (options.MaxArtifactBytes > 0 && serialized.Length > options.MaxArtifactBytes)
        {
            if (_logger is not null && _logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    "'{Tool}' result ({Bytes:N0} bytes) exceeds MaxArtifactBytes ({Cap:N0}); inline-truncating instead of offloading",
                    toolName, serialized.Length, options.MaxArtifactBytes);
            }
            return TruncateFallback(toolName, serialized, effectiveThreshold);
        }

        try
        {
            var reference = await _store.WriteAsync(
                new ArtifactWriteRequest(
                    SessionId: _sessionId,
                    ToolName: toolName,
                    Payload: serialized,
                    ContextPressure: contextPressure,
                    EffectiveThreshold: effectiveThreshold),
                cancellationToken);

            var preview = BuildSemanticPreview(result.Value, serialized);

            if (_logger is not null && _logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Offloaded '{Tool}' result ({Bytes:N0} bytes) to {Path} (pressure={Pressure:F2}, threshold={Threshold:N0})",
                    toolName,
                    serialized.Length,
                    reference.RelativePath,
                    contextPressure,
                    effectiveThreshold);
            }

            return ToolResult.Ok(new
            {
                compressed = true,
                artifact = true,
                tool = toolName,
                originalBytes = serialized.Length,
                artifactPath = reference.RelativePath,
                agentId = _sessionId,
                preview,
                previewBytes = preview.Length,
                hint = BuildHint(reference.RelativePath, serialized.Length),
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger is not null && _logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    ex,
                    "File-backed compression failed for '{Tool}', falling back to inline truncation",
                    toolName);
            }
            return TruncateFallback(toolName, serialized, effectiveThreshold);
        }
    }

    /// <summary>
    /// Scales the configured threshold based on current context pressure.
    /// <list type="bullet">
    /// <item><description>pressure &lt; 0.5: threshold × 2 (permissive)</description></item>
    /// <item><description>pressure ∈ [0.5, 0.8]: threshold × 1 (as configured)</description></item>
    /// <item><description>pressure &gt; 0.8: threshold × 0.5, floored at 16 KB (aggressive)</description></item>
    /// </list>
    /// </summary>
    public static int ScaleThreshold(int configured, float pressure)
    {
        if (pressure <= 0f || float.IsNaN(pressure))
            return configured;
        if (pressure < 0.5f)
            return configured * 2;
        if (pressure > 0.8f)
            return Math.Max(16_384, configured / 2);
        return configured;
    }

    internal static string BuildSemanticPreview(object? value, string serialized)
    {
        if (value is string)
            return HeadSlice(serialized, PreviewBytes);

        try
        {
            using var doc = JsonDocument.Parse(serialized);
            return BuildJsonPreview(doc.RootElement);
        }
        catch
        {
            return HeadSlice(serialized, PreviewBytes);
        }
    }

    private static string BuildJsonPreview(JsonElement root)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.Object:
            {
                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
                {
                    w.WriteStartObject();
                    var budget = PreviewBytes - 64;
                    string? lastWritten = null;
                    var stopped = false;
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (ms.Position > budget)
                        {
                            w.WriteString("__truncated__",
                                $"...{CountRemaining(root, lastWritten)} more fields");
                            stopped = true;
                            break;
                        }
                        WriteTruncatedValue(w, prop);
                        lastWritten = prop.Name;
                    }
                    if (!stopped) { /* fully written */ }
                    w.WriteEndObject();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
            case JsonValueKind.Array:
            {
                var arrItems = new List<JsonElement>();
                foreach (var item in root.EnumerateArray())
                {
                    arrItems.Add(item);
                    if (arrItems.Count >= 3) break;
                }
                var arrCount = root.GetArrayLength();
                var sample = string.Join(",", arrItems.Select(i => HeadSlice(i.GetRawText(), 400)));
                return $"[{sample}{(arrCount > arrItems.Count ? $",\"...{arrCount - arrItems.Count} more items\"" : "")}]";
            }
            default:
                return HeadSlice(root.GetRawText(), PreviewBytes);
        }
    }

    private static void WriteTruncatedValue(Utf8JsonWriter w, JsonProperty prop)
    {
        var v = prop.Value;
        switch (v.ValueKind)
        {
            case JsonValueKind.String:
                var s = v.GetString() ?? "";
                w.WriteString(prop.Name, s.Length > 200 ? s[..200] + "…" : s);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                w.WritePropertyName(prop.Name);
                v.WriteTo(w);
                break;
            case JsonValueKind.Array:
                w.WriteString(prop.Name, $"[…{v.GetArrayLength()} items]");
                break;
            case JsonValueKind.Object:
                var objCount = 0;
                foreach (var _ in v.EnumerateObject()) objCount++;
                w.WriteString(prop.Name, $"{{…{objCount} fields}}");
                break;
            default:
                w.WriteString(prop.Name, "…");
                break;
        }
    }

    private static int CountRemaining(JsonElement root, string? lastSeenName)
    {
        if (lastSeenName is null)
        {
            var total = 0;
            foreach (var _ in root.EnumerateObject()) total++;
            return total;
        }

        var count = 0;
        var seenLast = false;
        foreach (var p in root.EnumerateObject())
        {
            if (seenLast) count++;
            else if (p.Name == lastSeenName) seenLast = true;
        }
        return count;
    }

    private static string HeadSlice(string s, int max) =>
        s.Length <= max ? s : s[..max];

    private static string BuildHint(string relativePath, int originalBytes) =>
        $"Full tool result ({originalBytes:N0} bytes) saved at \"{relativePath}\". "
        + "Read it with fs_read using startLine/endLine ranges (e.g. 1-200); "
        + "reading the entire file will be re-compressed into another artifact. "
        + "Use fs_grep on the path first if you need to locate something specific.";

    private static ToolResult TruncateFallback(string toolName, string serialized, int threshold)
    {
        var kept = serialized[..Math.Min(serialized.Length, threshold)];
        return ToolResult.Ok(new
        {
            compressed = true,
            truncated = true,
            tool = toolName,
            originalBytes = serialized.Length,
            summary = $"[Result truncated: {serialized.Length:N0} bytes exceeded threshold {threshold:N0}; artifact write failed]",
            partial = kept,
        });
    }

    private static string Serialize(object? value)
    {
        if (value is null) return "";
        if (value is string s) return s;
        try { return JsonSerializer.Serialize(value, SerializerOptions); }
        catch { return value.ToString() ?? ""; }
    }
}
