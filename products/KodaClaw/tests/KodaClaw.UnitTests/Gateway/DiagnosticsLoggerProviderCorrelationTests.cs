using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Gateway;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.Gateway;

/// <summary>
/// Tests for DiagnosticsLoggerProvider with ICorrelationContextAccessor injection (KC-6103).
/// </summary>
public sealed class DiagnosticsLoggerProviderCorrelationTests
{
    [Fact]
    public void Log_WithCorrelationAccessor_RecordsCorrelationId()
    {
        DiagnosticEvent? recorded = null;
        var diagMock = new Mock<IDiagnosticsService>();
        diagMock.Setup(s => s.Record(It.IsAny<DiagnosticEvent>()))
            .Callback<DiagnosticEvent>(e => recorded = e);

        var accessorMock = new Mock<ICorrelationContextAccessor>();
        accessorMock.SetupGet(a => a.CorrelationId).Returns("abc123");

        var provider = new DiagnosticsLoggerProvider(diagMock.Object, accessorMock.Object);
        var logger = provider.CreateLogger("KodaClaw.Runtime.SomeService");
        logger.Log(LogLevel.Warning, new EventId(0), "test warning", null, (s, _) => s);

        Assert.NotNull(recorded);
        Assert.Equal("abc123", recorded!.CorrelationId);
    }

    [Fact]
    public void Log_WithoutCorrelationAccessor_RecordsNullCorrelationId()
    {
        DiagnosticEvent? recorded = null;
        var diagMock = new Mock<IDiagnosticsService>();
        diagMock.Setup(s => s.Record(It.IsAny<DiagnosticEvent>()))
            .Callback<DiagnosticEvent>(e => recorded = e);

        var provider = new DiagnosticsLoggerProvider(diagMock.Object);
        var logger = provider.CreateLogger("KodaClaw.Runtime.SomeService");
        logger.Log(LogLevel.Error, new EventId(0), "test error", null, (s, _) => s);

        Assert.NotNull(recorded);
        Assert.Null(recorded!.CorrelationId);
    }

    [Fact]
    public void Log_WithNullCorrelationId_RecordsNullCorrelationId()
    {
        DiagnosticEvent? recorded = null;
        var diagMock = new Mock<IDiagnosticsService>();
        diagMock.Setup(s => s.Record(It.IsAny<DiagnosticEvent>()))
            .Callback<DiagnosticEvent>(e => recorded = e);

        var accessorMock = new Mock<ICorrelationContextAccessor>();
        accessorMock.SetupGet(a => a.CorrelationId).Returns((string?)null);

        var provider = new DiagnosticsLoggerProvider(diagMock.Object, accessorMock.Object);
        var logger = provider.CreateLogger("KodaClaw.Gateway.Something");
        logger.Log(LogLevel.Warning, new EventId(0), "msg", null, (s, _) => s);

        Assert.NotNull(recorded);
        Assert.Null(recorded!.CorrelationId);
    }
}
