using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.ControlPlane;
using Xunit;

namespace KodaClaw.UnitTests.ControlPlane;

public sealed class FileDiagnosticsServiceTests : IDisposable
{
    private readonly string _tmpDir;

    public FileDiagnosticsServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "kc-diag-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    private FileDiagnosticsService CreateService() => new(_tmpDir);

    private static DiagnosticEvent MakeEvent(string level = "info", string source = "test") =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            Source: source,
            EventType: $"{source}.event",
            Level: level,
            Message: "test message",
            Timestamp: DateTimeOffset.UtcNow);

    [Fact]
    public void Record_StoresInCache_AndJournalFile()
    {
        var svc = CreateService();
        svc.Record(MakeEvent());

        svc.GetRecent(10).Should().HaveCount(1);
        var journalPath = Path.Combine(_tmpDir, "logs", "diagnostics.jsonl");
        File.Exists(journalPath).Should().BeTrue();
        File.ReadAllLines(journalPath).Should().HaveCount(1);
    }

    [Fact]
    public void ReloadFromFile_OnNewInstance_RestoresCache()
    {
        var svc1 = CreateService();
        svc1.Record(MakeEvent(level: "error", source: "gateway"));
        svc1.Record(MakeEvent(level: "warning", source: "runtime"));

        // 新实例应从文件回填
        var svc2 = new FileDiagnosticsService(_tmpDir);
        svc2.GetRecent(50).Should().HaveCount(2);
    }

    [Fact]
    public async Task ClearAsync_NoBefore_DeletesAll()
    {
        var svc = CreateService();
        svc.Record(MakeEvent());
        svc.Record(MakeEvent());

        await svc.ClearAsync();

        svc.GetRecent(10).Should().BeEmpty();
        var journalPath = Path.Combine(_tmpDir, "logs", "diagnostics.jsonl");
        File.Exists(journalPath).Should().BeFalse();
    }

    [Fact]
    public async Task ClearAsync_WithBefore_KeepsNewerEvents()
    {
        var svc = CreateService();
        var cutoff = DateTimeOffset.UtcNow;

        var old = MakeEvent() with { Timestamp = cutoff.AddMinutes(-5) };
        var recent = MakeEvent() with { Timestamp = cutoff.AddMinutes(5) };

        svc.Record(old);
        svc.Record(recent);

        await svc.ClearAsync(before: cutoff);

        var remaining = svc.GetRecent(10);
        remaining.Should().HaveCount(1);
        remaining[0].Id.Should().Be(recent.Id);
    }

    [Fact]
    public void GetStats_ReturnsCorrectCounts()
    {
        var svc = CreateService();
        svc.Record(MakeEvent("info", "gateway"));
        svc.Record(MakeEvent("warning", "gateway"));
        svc.Record(MakeEvent("error", "runtime"));

        var stats = svc.GetStats();

        stats.TotalEvents.Should().Be(3);
        stats.ErrorCount.Should().Be(1);
        stats.WarningCount.Should().Be(1);
        stats.BySource.Should().HaveCount(2);
    }

    [Fact]
    public void Query_DateRange_FiltersCorrectly()
    {
        var svc = CreateService();
        var cutoff = DateTimeOffset.UtcNow;
        svc.Record(MakeEvent() with { Timestamp = cutoff.AddHours(-2) });
        svc.Record(MakeEvent() with { Timestamp = cutoff.AddHours(-1) });
        svc.Record(MakeEvent() with { Timestamp = cutoff.AddHours(1) });

        var results = svc.Query(new DiagnosticsQuery(
            Limit: 50,
            DateFrom: cutoff.AddMinutes(-90),
            DateTo: cutoff.AddMinutes(30)));

        results.Should().HaveCount(1); // 只有 -1h 那条在范围内
    }

    [Fact]
    public async Task SubscribeAsync_ReceivesNewEvents()
    {
        var svc = CreateService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var received = new List<DiagnosticEvent>();

        var consumeTask = Task.Run(async () =>
        {
            await foreach (var evt in svc.SubscribeAsync(cts.Token))
            {
                received.Add(evt);
                if (received.Count >= 2) cts.Cancel();
            }
        });

        await Task.Delay(50);
        svc.Record(MakeEvent("info", "test-a"));
        svc.Record(MakeEvent("error", "test-b"));

        try { await consumeTask; } catch (OperationCanceledException) { }

        received.Should().HaveCount(2);
        received.Select(e => e.Source).Should().BeEquivalentTo(["test-a", "test-b"]);
    }
}
