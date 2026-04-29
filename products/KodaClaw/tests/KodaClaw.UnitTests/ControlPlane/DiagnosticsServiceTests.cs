using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.ControlPlane;
using Xunit;

namespace KodaClaw.UnitTests.ControlPlane;

public sealed class DiagnosticsServiceTests
{
    [Fact]
    public void In_memory_diagnostics_service_should_filter_by_correlation_and_return_newest_first()
    {
        var service = new InMemoryDiagnosticsService();
        service.Record(new DiagnosticEvent(
            Id: "diag-1",
            Source: "gateway.chat",
            EventType: "chat.started",
            Level: "info",
            Message: "first",
            Timestamp: new DateTimeOffset(2026, 3, 18, 10, 0, 0, TimeSpan.Zero),
            CorrelationId: "corr-a"));
        service.Record(new DiagnosticEvent(
            Id: "diag-2",
            Source: "gateway.chat",
            EventType: "chat.completed",
            Level: "info",
            Message: "second",
            Timestamp: new DateTimeOffset(2026, 3, 18, 10, 1, 0, TimeSpan.Zero),
            CorrelationId: "corr-a"));
        service.Record(new DiagnosticEvent(
            Id: "diag-3",
            Source: "gateway.chat",
            EventType: "chat.failed",
            Level: "error",
            Message: "third",
            Timestamp: new DateTimeOffset(2026, 3, 18, 10, 2, 0, TimeSpan.Zero),
            CorrelationId: "corr-b"));

        var filtered = service.GetRecent(limit: 10, correlationId: "corr-a");

        filtered.Select(item => item.Id).Should().Equal("diag-2", "diag-1");
    }

    [Fact]
    public void In_memory_diagnostics_service_query_should_filter_by_all_supported_fields()
    {
        var service = new InMemoryDiagnosticsService();
        service.Record(new DiagnosticEvent(
            Id: "diag-1",
            Source: "gateway.chat",
            EventType: "gateway.chat.requested",
            Level: "info",
            Message: "first",
            Timestamp: new DateTimeOffset(2026, 3, 18, 10, 0, 0, TimeSpan.Zero),
            CorrelationId: "corr-a",
            SessionId: "session-a"));
        service.Record(new DiagnosticEvent(
            Id: "diag-2",
            Source: "gateway.chat",
            EventType: "gateway.chat.failed",
            Level: "error",
            Message: "second",
            Timestamp: new DateTimeOffset(2026, 3, 18, 10, 1, 0, TimeSpan.Zero),
            CorrelationId: "corr-a",
            SessionId: "session-b"));
        service.Record(new DiagnosticEvent(
            Id: "diag-3",
            Source: "workspace.bootstrap",
            EventType: "workspace.bootstrap.completed",
            Level: "info",
            Message: "third",
            Timestamp: new DateTimeOffset(2026, 3, 18, 10, 2, 0, TimeSpan.Zero),
            CorrelationId: "corr-b",
            SessionId: "session-b"));

        var filtered = service.Query(new DiagnosticsQuery(
            Limit: 10,
            CorrelationId: "corr-a",
            SessionId: "session-b",
            Source: "GATEWAY.CHAT",
            EventType: "gateway.chat.failed",
            Levels: ["ERROR"]));

        filtered.Select(item => item.Id).Should().Equal("diag-2");
    }

    [Fact]
    public void Async_local_correlation_context_accessor_should_hold_current_correlation_id()
    {
        var accessor = new AsyncLocalCorrelationContextAccessor();

        accessor.CorrelationId = "corr-test";

        accessor.CorrelationId.Should().Be("corr-test");
    }
}
