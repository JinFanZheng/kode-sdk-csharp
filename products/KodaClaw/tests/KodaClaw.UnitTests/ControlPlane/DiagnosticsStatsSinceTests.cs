using KodaClaw.ControlPlane;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using Xunit;

namespace KodaClaw.UnitTests.ControlPlane;

/// <summary>
/// Tests for GetStats(since) — time-windowed stats query.
/// </summary>
public sealed class DiagnosticsStatsSinceTests
{
    private static DiagnosticEvent MakeEvent(string level, DateTimeOffset ts) =>
        new(Id: Guid.NewGuid().ToString("N"),
            Source: "test",
            EventType: "test.event",
            Level: level,
            Message: "test message",
            Timestamp: ts);

    // ── InMemoryDiagnosticsService ────────────────────────────────────────────

    [Fact]
    public void InMemory_GetStats_NoSince_ReturnsAll()
    {
        var svc = new InMemoryDiagnosticsService();
        var now = DateTimeOffset.UtcNow;
        svc.Record(MakeEvent("error",   now.AddHours(-2)));
        svc.Record(MakeEvent("warning", now.AddHours(-1)));
        svc.Record(MakeEvent("info",    now.AddMinutes(-30)));

        var stats = svc.GetStats();

        Assert.Equal(3, stats.TotalEvents);
        Assert.Equal(1, stats.ErrorCount);
        Assert.Equal(1, stats.WarningCount);
    }

    [Fact]
    public void InMemory_GetStats_Since_ExcludesOlderEvents()
    {
        var svc = new InMemoryDiagnosticsService();
        var now = DateTimeOffset.UtcNow;
        svc.Record(MakeEvent("error",   now.AddHours(-2)));  // older — excluded
        svc.Record(MakeEvent("warning", now.AddMinutes(-30))); // within window
        svc.Record(MakeEvent("info",    now.AddMinutes(-10))); // within window

        var since = now.AddHours(-1);
        var stats = svc.GetStats(since);

        Assert.Equal(2, stats.TotalEvents);
        Assert.Equal(0, stats.ErrorCount);
        Assert.Equal(1, stats.WarningCount);
    }

    [Fact]
    public void InMemory_GetStats_Since_NoMatchingEvents_ReturnsZeros()
    {
        var svc = new InMemoryDiagnosticsService();
        svc.Record(MakeEvent("error", DateTimeOffset.UtcNow.AddHours(-5)));

        var stats = svc.GetStats(DateTimeOffset.UtcNow.AddHours(-1));

        Assert.Equal(0, stats.TotalEvents);
        Assert.Equal(0, stats.ErrorCount);
        Assert.Equal(0, stats.WarningCount);
    }

    [Fact]
    public void InMemory_GetStats_Since_Empty_ReturnsZeros()
    {
        var svc = new InMemoryDiagnosticsService();

        var stats = svc.GetStats(DateTimeOffset.UtcNow.AddHours(-1));

        Assert.Equal(0, stats.TotalEvents);
        Assert.Null(stats.OldestEvent);
        Assert.Null(stats.NewestEvent);
    }

    // ── FileDiagnosticsService ────────────────────────────────────────────────

    [Fact]
    public void File_GetStats_Since_ExcludesOlderEvents()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var svc = new FileDiagnosticsService(dir);
            var now = DateTimeOffset.UtcNow;
            svc.Record(MakeEvent("error",   now.AddHours(-2)));  // excluded
            svc.Record(MakeEvent("warning", now.AddMinutes(-30))); // included
            svc.Record(MakeEvent("info",    now.AddMinutes(-5)));  // included

            var stats = svc.GetStats(now.AddHours(-1));

            Assert.Equal(2, stats.TotalEvents);
            Assert.Equal(0, stats.ErrorCount);
            Assert.Equal(1, stats.WarningCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void File_GetStats_NoSince_ReturnsAll()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var svc = new FileDiagnosticsService(dir);
            var now = DateTimeOffset.UtcNow;
            svc.Record(MakeEvent("error",   now.AddHours(-3)));
            svc.Record(MakeEvent("warning", now.AddHours(-1)));

            var stats = svc.GetStats();

            Assert.Equal(2, stats.TotalEvents);
            Assert.Equal(1, stats.ErrorCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
