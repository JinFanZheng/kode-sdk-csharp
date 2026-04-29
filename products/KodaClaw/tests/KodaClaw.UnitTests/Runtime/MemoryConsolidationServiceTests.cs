using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Runtime;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.Runtime;

public sealed class MemoryConsolidationServiceTests
{
    private readonly Mock<IWorkspaceService> _workspaceMock;
    private readonly Mock<IDiagnosticsService> _diagnosticsMock;
    private readonly MemoryConsolidationService _service;

    public MemoryConsolidationServiceTests()
    {
        _workspaceMock = new Mock<IWorkspaceService>();
        _workspaceMock
            .Setup(w => w.TryCommitWorkspaceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _diagnosticsMock = new Mock<IDiagnosticsService>();

        _service = new MemoryConsolidationService(
            _workspaceMock.Object,
            _diagnosticsMock.Object);
    }

    [Fact]
    public async Task PostConsolidation_CommitsWorkspace()
    {
        await _service.PostConsolidationAsync();

        _workspaceMock.Verify(
            w => w.TryCommitWorkspaceAsync(
                It.Is<string>(s => s.Contains("post-consolidation")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PostConsolidation_RecordsDiagnosticEvent()
    {
        await _service.PostConsolidationAsync();

        _diagnosticsMock.Verify(
            d => d.Record(It.Is<DiagnosticEvent>(e =>
                e.EventType == "memory.post_consolidation_committed" &&
                e.Level == "info")),
            Times.Once);
    }

    [Fact]
    public async Task PostConsolidation_NullDiagnostics_DoesNotThrow()
    {
        var service = new MemoryConsolidationService(
            _workspaceMock.Object,
            diagnosticsService: null);

        var act = () => service.PostConsolidationAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PostConsolidation_UpdatesLastConsolidationAt()
    {
        WorkspaceAppConfig? savedConfig = null;
        _workspaceMock
            .Setup(w => w.LoadAppConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkspaceAppConfig());
        _workspaceMock
            .Setup(w => w.SaveAppConfigAsync(It.IsAny<WorkspaceAppConfig>(), It.IsAny<CancellationToken>()))
            .Callback<WorkspaceAppConfig, CancellationToken>((c, _) => savedConfig = c)
            .Returns(Task.CompletedTask);

        await _service.PostConsolidationAsync();

        savedConfig.Should().NotBeNull();
        savedConfig!.LastConsolidationAt.Should().NotBeNull();
        savedConfig.LastConsolidationAt!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Constructor_NullWorkspace_Throws()
    {
        var act = () => new MemoryConsolidationService(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
