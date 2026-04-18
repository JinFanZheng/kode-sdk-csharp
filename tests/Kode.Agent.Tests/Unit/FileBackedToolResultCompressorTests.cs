using System.Text.Json;
using FluentAssertions;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Agent;
using Xunit;

namespace Kode.Agent.Tests.Unit;

public sealed class FileBackedToolResultCompressorTests
{
    private sealed class InMemoryArtifactStore : IArtifactStore
    {
        public List<ArtifactWriteRequest> Writes { get; } = new();
        public Exception? ThrowOnWrite { get; set; }

        public Task<ArtifactReference> WriteAsync(
            ArtifactWriteRequest request, CancellationToken cancellationToken = default)
        {
            if (ThrowOnWrite is not null) throw ThrowOnWrite;
            Writes.Add(request);
            return Task.FromResult(new ArtifactReference(
                RelativePath: $"cache/artifacts/{request.SessionId}/fake-{Writes.Count}.json",
                SizeBytes: request.Payload.Length));
        }
    }

    private static ToolResultCompressionOptions Options(int threshold = 1_024) =>
        new() { Enabled = true, ThresholdBytes = threshold };

    // ---------------------------------------------------------------------
    // ScaleThreshold: boundary behaviour
    // ---------------------------------------------------------------------

    [Fact]
    public void ScaleThreshold_ZeroOrNegativePressure_ReturnsConfigured()
    {
        FileBackedToolResultCompressor.ScaleThreshold(80_000, 0f).Should().Be(80_000);
        FileBackedToolResultCompressor.ScaleThreshold(80_000, -1f).Should().Be(80_000);
        FileBackedToolResultCompressor.ScaleThreshold(80_000, float.NaN).Should().Be(80_000);
    }

    [Fact]
    public void ScaleThreshold_LowPressure_DoublesThreshold()
    {
        FileBackedToolResultCompressor.ScaleThreshold(80_000, 0.2f).Should().Be(160_000);
        FileBackedToolResultCompressor.ScaleThreshold(80_000, 0.499f).Should().Be(160_000);
    }

    [Fact]
    public void ScaleThreshold_MidPressure_KeepsConfigured()
    {
        FileBackedToolResultCompressor.ScaleThreshold(80_000, 0.5f).Should().Be(80_000);
        FileBackedToolResultCompressor.ScaleThreshold(80_000, 0.8f).Should().Be(80_000);
    }

    [Fact]
    public void ScaleThreshold_HighPressure_HalvesWithFloor()
    {
        FileBackedToolResultCompressor.ScaleThreshold(80_000, 0.9f).Should().Be(40_000);
        // 20_000 / 2 = 10_000, but the floor is 16_384
        FileBackedToolResultCompressor.ScaleThreshold(20_000, 0.9f).Should().Be(16_384);
    }

    // ---------------------------------------------------------------------
    // CompressIfNeededAsync: early-exit paths
    // ---------------------------------------------------------------------

    [Fact]
    public async Task CompressIfNeeded_SkipsWhenResultFailed()
    {
        var store = new InMemoryArtifactStore();
        var compressor = new FileBackedToolResultCompressor(store, "session-1");
        var input = ToolResult.Fail("boom");

        var result = await compressor.CompressIfNeededAsync("bash_run", input, [], Options());

        result.Should().BeSameAs(input);
        store.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task CompressIfNeeded_SkipsVerbatimToolEvenWhenLarge()
    {
        var store = new InMemoryArtifactStore();
        var compressor = new FileBackedToolResultCompressor(store, "session-1");
        var payload = new { content = new string('x', 500_000) };
        var input = ToolResult.Ok(payload);

        var result = await compressor.CompressIfNeededAsync("fs_read", input, [], Options());

        result.Should().BeSameAs(input);
        store.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task CompressIfNeeded_SkipsWhenBelowThreshold()
    {
        var store = new InMemoryArtifactStore();
        var compressor = new FileBackedToolResultCompressor(store, "session-1");
        var input = ToolResult.Ok(new { stdout = "short" });

        var result = await compressor.CompressIfNeededAsync(
            "bash_run", input, [], Options(threshold: 100_000));

        result.Should().BeSameAs(input);
        store.Writes.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------
    // CompressIfNeededAsync: offload / fallback / cap paths
    // ---------------------------------------------------------------------

    [Fact]
    public async Task CompressIfNeeded_OffloadsAndReturnsArtifactReference()
    {
        var store = new InMemoryArtifactStore();
        var compressor = new FileBackedToolResultCompressor(store, "sess-xyz");
        var big = new string('y', 5_000);
        var input = ToolResult.Ok(new { stdout = big });

        var result = await compressor.CompressIfNeededAsync(
            "bash_run", input, [], Options(threshold: 1_000));

        result.Success.Should().BeTrue();
        store.Writes.Should().HaveCount(1);
        var writeRequest = store.Writes[0];
        writeRequest.SessionId.Should().Be("sess-xyz");
        writeRequest.ToolName.Should().Be("bash_run");

        // The placeholder must carry artifactPath and preview fields.
        var json = JsonSerializer.Serialize(result.Value);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("artifactPath", out var artifactPath).Should().BeTrue();
        artifactPath.GetString().Should().Contain("cache/artifacts/sess-xyz/");
        doc.RootElement.TryGetProperty("preview", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("hint", out _).Should().BeTrue();
    }

    [Fact]
    public async Task CompressIfNeeded_FallsBackToTruncateWhenStoreFails()
    {
        var store = new InMemoryArtifactStore { ThrowOnWrite = new IOException("disk full") };
        var compressor = new FileBackedToolResultCompressor(store, "sess-xyz");
        var big = new string('y', 5_000);
        var input = ToolResult.Ok(new { stdout = big });

        var result = await compressor.CompressIfNeededAsync(
            "bash_run", input, [], Options(threshold: 1_000));

        result.Success.Should().BeTrue();
        var json = JsonSerializer.Serialize(result.Value);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("truncated", out var truncated).Should().BeTrue();
        truncated.GetBoolean().Should().BeTrue();
        doc.RootElement.TryGetProperty("artifactPath", out _).Should().BeFalse();
    }

    [Fact]
    public async Task CompressIfNeeded_InlineTruncatesWhenExceedsMaxArtifactBytes()
    {
        var store = new InMemoryArtifactStore();
        var compressor = new FileBackedToolResultCompressor(store, "sess-xyz");
        var big = new string('y', 10_000);
        var input = ToolResult.Ok(new { blob = big });

        var opts = new ToolResultCompressionOptions
        {
            Enabled = true,
            ThresholdBytes = 1_000,
            MaxArtifactBytes = 2_000,   // smaller than payload → must not offload
        };

        var result = await compressor.CompressIfNeededAsync("bash_run", input, [], opts);

        store.Writes.Should().BeEmpty("payload exceeds MaxArtifactBytes");
        var json = JsonSerializer.Serialize(result.Value);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    // ---------------------------------------------------------------------
    // BuildSemanticPreview: regression tests for WriteTruncatedValue bug
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Preview_ObjectWithTopLevelNumberAndBoolFields_IsValidJson()
    {
        // Regression: earlier WriteTruncatedValue wrote value before propertyName for
        // Number/True/False/Null, producing invalid JSON and throwing at Flush.
        var store = new InMemoryArtifactStore();
        var compressor = new FileBackedToolResultCompressor(store, "sess-1");
        var payload = new
        {
            statusCode = 200,           // Number
            ok = true,                  // True
            cancelled = false,          // False
            reason = (string?)null,     // Null
            body = new string('b', 3_000),
        };
        var input = ToolResult.Ok(payload);

        var result = await compressor.CompressIfNeededAsync(
            "bash_run", input, [], Options(threshold: 500));

        var json = JsonSerializer.Serialize(result.Value);
        using var doc = JsonDocument.Parse(json);
        var preview = doc.RootElement.GetProperty("preview").GetString();
        preview.Should().NotBeNullOrEmpty();

        // Preview must itself be parseable JSON (that's the whole point).
        using var previewDoc = JsonDocument.Parse(preview!);
        previewDoc.RootElement.GetProperty("statusCode").GetInt32().Should().Be(200);
        previewDoc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        previewDoc.RootElement.GetProperty("cancelled").GetBoolean().Should().BeFalse();
        previewDoc.RootElement.GetProperty("reason").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
