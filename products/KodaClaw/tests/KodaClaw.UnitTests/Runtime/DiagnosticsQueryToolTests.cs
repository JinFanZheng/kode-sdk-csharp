using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Tools;
using Kode.Agent.Sdk.Core.Abstractions;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.Runtime;

public sealed class DiagnosticsQueryToolTests
{
    private static DiagnosticEvent MakeEvent(
        string level,
        string source = "runtime",
        string? correlationId = null,
        DateTimeOffset? ts = null) =>
        new(Id: Guid.NewGuid().ToString("N"),
            Source: source,
            EventType: $"{source}.event",
            Level: level,
            Message: $"{level} message from {source}",
            Timestamp: ts ?? DateTimeOffset.UtcNow.AddMinutes(-5),
            CorrelationId: correlationId);

    private static Mock<IDiagnosticsService> BuildMock(IReadOnlyList<DiagnosticEvent> events)
    {
        var mock = new Mock<IDiagnosticsService>();
        mock.Setup(s => s.Query(It.IsAny<DiagnosticsQuery>()))
            .Returns<DiagnosticsQuery>(q =>
            {
                IEnumerable<DiagnosticEvent> f = events;
                if (!string.IsNullOrWhiteSpace(q.Level))
                    f = f.Where(e => string.Equals(e.Level, q.Level, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(q.Source))
                    f = f.Where(e => string.Equals(e.Source, q.Source, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(q.CorrelationId))
                    f = f.Where(e => string.Equals(e.CorrelationId, q.CorrelationId, StringComparison.Ordinal));
                return f.Take(q.Limit).ToArray();
            });
        return mock;
    }

    private static ToolContext MakeContext() => new()
    {
        AgentId = "test",
        CallId = "test-call",
        Sandbox = new Mock<ISandbox>().Object,
    };

    [Fact]
    public async Task Execute_NoFilter_ReturnsEvents()
    {
        var events = new[]
        {
            MakeEvent("error"),
            MakeEvent("info"),
        };
        var mock = BuildMock(events);
        var tool = new DiagnosticsQueryTool(mock.Object);

        var result = await tool.ExecuteAsync(new DiagnosticsQueryArgs(), MakeContext(), CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Execute_LevelFilter_PassedToQuery()
    {
        var events = new[] { MakeEvent("error") };
        var mock = BuildMock(events);
        var tool = new DiagnosticsQueryTool(mock.Object);

        await tool.ExecuteAsync(new DiagnosticsQueryArgs { Level = "error" }, MakeContext(), CancellationToken.None);

        mock.Verify(s => s.Query(It.Is<DiagnosticsQuery>(q =>
            string.Equals(q.Level, "error", StringComparison.OrdinalIgnoreCase))), Times.Once);
    }

    [Fact]
    public async Task Execute_CorrelationIdFilter_PassedToQuery()
    {
        var corrId = Guid.NewGuid().ToString("N");
        var events = new[] { MakeEvent("info", correlationId: corrId) };
        var mock = BuildMock(events);
        var tool = new DiagnosticsQueryTool(mock.Object);

        await tool.ExecuteAsync(new DiagnosticsQueryArgs { CorrelationId = corrId }, MakeContext(), CancellationToken.None);

        mock.Verify(s => s.Query(It.Is<DiagnosticsQuery>(q =>
            q.CorrelationId == corrId)), Times.Once);
    }

    [Fact]
    public async Task Execute_SinceMinutesClamped_Max1440()
    {
        var mock = BuildMock([]);
        var tool = new DiagnosticsQueryTool(mock.Object);

        await tool.ExecuteAsync(new DiagnosticsQueryArgs { SinceMinutes = 99999 }, MakeContext(), CancellationToken.None);

        mock.Verify(s => s.Query(It.Is<DiagnosticsQuery>(q =>
            q.DateFrom.HasValue &&
            q.DateFrom.Value >= DateTimeOffset.UtcNow.AddMinutes(-1441) &&
            q.DateFrom.Value <= DateTimeOffset.UtcNow.AddMinutes(-1439))), Times.Once);
    }

    [Fact]
    public async Task Execute_LimitClamped_Max50()
    {
        var mock = BuildMock([]);
        var tool = new DiagnosticsQueryTool(mock.Object);

        await tool.ExecuteAsync(new DiagnosticsQueryArgs { Limit = 999 }, MakeContext(), CancellationToken.None);

        mock.Verify(s => s.Query(It.Is<DiagnosticsQuery>(q => q.Limit == 50)), Times.Once);
    }

    [Fact]
    public void ToolName_IsDiagnosticsQuery()
    {
        var mock = new Mock<IDiagnosticsService>();
        var tool = new DiagnosticsQueryTool(mock.Object);
        Assert.Equal("diagnostics_query", tool.Name);
    }

    [Fact]
    public void Attributes_IsReadOnly()
    {
        var mock = new Mock<IDiagnosticsService>();
        var tool = new DiagnosticsQueryTool(mock.Object);
        Assert.True(tool.Attributes.ReadOnly);
    }
}
