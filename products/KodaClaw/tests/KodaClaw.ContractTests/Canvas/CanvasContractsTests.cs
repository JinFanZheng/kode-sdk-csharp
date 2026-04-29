using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Canvas;
using Xunit;

namespace KodaClaw.ContractTests.Canvas;

public sealed class CanvasContractsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Canvas_query_response_should_json_round_trip()
    {
        var payload = new CanvasQueryResponse(
            Items:
            [
                CreateArtifact("canvas-report", CanvasArtifactKind.Report, "workspace/canvas/reports/index.html"),
            ],
            DefaultEntryPath: "workspace/canvas/reports/index.html",
            DefaultArtifactId: "canvas-report");

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<CanvasQueryResponse>(json, JsonOptions);

        json.Should().Contain("\"defaultEntryPath\":\"workspace/canvas/reports/index.html\"");
        json.Should().Contain("\"kind\":\"Report\"");
        roundTrip.Should().BeEquivalentTo(payload);
    }

    [Fact]
    public void Canvas_entry_response_should_json_round_trip()
    {
        var payload = new CanvasEntryResponse(
            EntryUrl: "/api/canvas/fs/workspace/canvas/reports/index.html",
            EntryPath: "workspace/canvas/reports/index.html",
            ArtifactId: "canvas-report",
            Route: "/canvas/report",
            Title: "Launch Snapshot");

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<CanvasEntryResponse>(json, JsonOptions);

        json.Should().Contain("\"artifactId\":\"canvas-report\"");
        roundTrip.Should().Be(payload);
    }

    [Fact]
    public void Upsert_canvas_artifact_request_should_json_round_trip()
    {
        var payload = new UpsertCanvasArtifactRequest(
            Id: "canvas-dashboard",
            Title: "Ops Wallboard",
            Kind: CanvasArtifactKind.Dashboard,
            Summary: "Realtime operations board.",
            Source: "runtime.automation",
            EntryPath: "workspace/canvas/artifacts/ops/index.html",
            AssetDirectory: "workspace/canvas/artifacts/ops",
            Route: "/canvas/ops",
            SessionId: "auto-session-001",
            CorrelationId: "corr-ops-001",
            MetadataJson: "{\"theme\":\"amber\"}");

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<UpsertCanvasArtifactRequest>(json, JsonOptions);

        json.Should().Contain("\"kind\":\"Dashboard\"");
        roundTrip.Should().Be(payload);
    }

    private static CanvasArtifact CreateArtifact(string id, CanvasArtifactKind kind, string entryPath)
    {
        return new CanvasArtifact(
            Id: id,
            Title: "Launch Snapshot",
            Kind: kind,
            Summary: "Executive summary board.",
            Source: "runtime.main",
            EntryPath: entryPath,
            AssetDirectory: "workspace/canvas/reports",
            CreatedAt: new DateTimeOffset(2026, 3, 18, 8, 0, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 3, 18, 8, 30, 0, TimeSpan.Zero),
            Route: "/canvas/report",
            SessionId: "main-001",
            CorrelationId: "corr-canvas-001",
            MetadataJson: null);
    }
}
