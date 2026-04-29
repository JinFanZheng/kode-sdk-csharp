using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Canvas;
using KodaClaw.Storage.Json.Repositories;
using Xunit;

namespace KodaClaw.UnitTests.Storage;

public sealed class JsonCanvasArtifactRepositoryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private JsonCanvasArtifactRepository CreateRepository() => new(_tempDir);

    [Fact]
    public async Task Repository_should_round_trip_metadata()
    {
        var repository = CreateRepository();
        var createdAt = new DateTimeOffset(2026, 3, 18, 9, 30, 0, TimeSpan.Zero);
        var expected = new CanvasArtifact(
            Id: "canvas-report-001",
            Title: "Weekly Report",
            Kind: CanvasArtifactKind.Report,
            Summary: "Weekly KPI summary",
            Source: "automation.scheduler",
            EntryPath: "workspace/canvas/reports/weekly/index.html",
            AssetDirectory: "workspace/canvas/artifacts/reports/weekly",
            CreatedAt: createdAt,
            UpdatedAt: createdAt.AddMinutes(3),
            Route: "/canvas/reports/canvas-report-001",
            SessionId: "session-main",
            CorrelationId: "corr-001",
            MetadataJson: """{"layout":"2x2","widgets":4}""");

        await repository.UpsertAsync(expected);

        var actual = await repository.GetByIdAsync(expected.Id);

        actual.Should().Be(expected);
    }

    [Fact]
    public async Task List_should_filter_by_kind_source_session_id()
    {
        var repository = CreateRepository();
        var now = new DateTimeOffset(2026, 3, 18, 10, 0, 0, TimeSpan.Zero);

        await repository.UpsertAsync(BuildArtifact(
            id: "canvas-001",
            kind: CanvasArtifactKind.Dashboard,
            source: "automation.scheduler",
            sessionId: "session-a",
            now: now));
        await repository.UpsertAsync(BuildArtifact(
            id: "canvas-002",
            kind: CanvasArtifactKind.Report,
            source: "automation.scheduler",
            sessionId: "session-a",
            now: now.AddMinutes(1)));
        await repository.UpsertAsync(BuildArtifact(
            id: "canvas-003",
            kind: CanvasArtifactKind.Dashboard,
            source: "manual",
            sessionId: "session-b",
            now: now.AddMinutes(2)));

        var filtered = await repository.ListAsync(new CanvasArtifactQuery(
            Kind: CanvasArtifactKind.Dashboard,
            Source: "automation.scheduler",
            SessionId: "session-a",
            Limit: 20));

        filtered.Should().ContainSingle();
        filtered[0].Id.Should().Be("canvas-001");
    }

    [Fact]
    public async Task Repository_should_persist_canvas_artifacts_to_filesystem()
    {
        var repository = CreateRepository();
        await repository.UpsertAsync(BuildArtifact(
            id: "canvas-init-001",
            kind: CanvasArtifactKind.Board,
            source: "tests",
            sessionId: null,
            now: new DateTimeOffset(2026, 3, 18, 12, 0, 0, TimeSpan.Zero)));

        var stored = await repository.GetByIdAsync("canvas-init-001");
        stored.Should().NotBeNull();
        stored!.Id.Should().Be("canvas-init-001");
    }

    private static CanvasArtifact BuildArtifact(
        string id,
        CanvasArtifactKind kind,
        string source,
        string? sessionId,
        DateTimeOffset now)
    {
        return new CanvasArtifact(
            Id: id,
            Title: $"Artifact {id}",
            Kind: kind,
            Summary: "Canvas summary",
            Source: source,
            EntryPath: $"workspace/canvas/{id}/index.html",
            AssetDirectory: $"workspace/canvas/artifacts/{id}",
            CreatedAt: now,
            UpdatedAt: now,
            SessionId: sessionId,
            CorrelationId: null,
            MetadataJson: """{"ok":true}""");
    }
}
