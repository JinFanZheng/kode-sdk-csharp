using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Gateway;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.Gateway;

public sealed class SessionRetentionExtensionTests
{
    // ── ExtractSessionIdFromSummaryFile ──────────────────────────────────────

    [Fact]
    public void ExtractSessionId_MainSession_ExtractsCorrectly()
    {
        var result = SessionRetentionService.ExtractSessionIdFromSummaryFile(
            "2026-03-25-main-20260325173802-abc123");

        result.Should().Be("main-20260325173802-abc123");
    }

    [Fact]
    public void ExtractSessionId_ChannelSession_ExtractsCorrectly()
    {
        var result = SessionRetentionService.ExtractSessionIdFromSummaryFile(
            "2026-03-25-channel-20260325180000-def456");

        result.Should().Be("channel-20260325180000-def456");
    }

    [Fact]
    public void ExtractSessionId_NoSessionPrefix_ReturnsNull()
    {
        var result = SessionRetentionService.ExtractSessionIdFromSummaryFile(
            "2026-03-25-random-notes");

        result.Should().BeNull();
    }

    // ── RunAsync with temp directories ──────────────────────────────────────

    [Fact]
    public async Task MainSession_WithSummary_OlderThan30Days_Deleted()
    {
        using var fixture = new RetentionFixture();

        // Create a main session folder with meta.json dated > 30 days ago
        var sessionDir = Path.Combine(fixture.SessionsDir, "main-20260101120000-old1");
        Directory.CreateDirectory(sessionDir);
        File.WriteAllText(Path.Combine(sessionDir, "meta.json"),
            """{"createdAt":"2026-01-01T12:00:00Z"}""");

        // Create a matching summary file in memory/sessions/
        File.WriteAllText(
            Path.Combine(fixture.SummariesDir, "2026-01-01-main-20260101120000-old1.md"),
            "# Summary");

        var service = fixture.BuildService();
        await service.RunAsync();

        Directory.Exists(sessionDir).Should().BeFalse("session with summary older than 30 days should be deleted");
    }

    [Fact]
    public async Task MainSession_ActiveSession_NeverDeleted()
    {
        using var fixture = new RetentionFixture(activeMainSessionId: "main-20260101120000-active");

        // Create the active session folder with old date
        var sessionDir = Path.Combine(fixture.SessionsDir, "main-20260101120000-active");
        Directory.CreateDirectory(sessionDir);
        File.WriteAllText(Path.Combine(sessionDir, "meta.json"),
            """{"createdAt":"2026-01-01T12:00:00Z"}""");

        // Create a matching summary file
        File.WriteAllText(
            Path.Combine(fixture.SummariesDir, "2026-01-01-main-20260101120000-active.md"),
            "# Summary");

        var service = fixture.BuildService();
        await service.RunAsync();

        Directory.Exists(sessionDir).Should().BeTrue("active session must never be deleted");
    }

    [Fact]
    public async Task MainSession_NoSummary_NotDeleted()
    {
        using var fixture = new RetentionFixture();

        // Create a main session folder with old date, but no summary
        var sessionDir = Path.Combine(fixture.SessionsDir, "main-20260101120000-nosummary");
        Directory.CreateDirectory(sessionDir);
        File.WriteAllText(Path.Combine(sessionDir, "meta.json"),
            """{"createdAt":"2026-01-01T12:00:00Z"}""");

        var service = fixture.BuildService();
        await service.RunAsync();

        Directory.Exists(sessionDir).Should().BeTrue("session without summary should not be deleted");
    }

    // ── Fixture ─────────────────────────────────────────────────────────────

    private sealed class RetentionFixture : IDisposable
    {
        private readonly string _rootPath;
        private readonly string? _activeMainSessionId;

        public RetentionFixture(string? activeMainSessionId = null)
        {
            _activeMainSessionId = activeMainSessionId;
            _rootPath = Path.Combine(Path.GetTempPath(), $"retention-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_rootPath);

            SessionsDir = Path.Combine(_rootPath, "sessions");
            Directory.CreateDirectory(SessionsDir);

            SummariesDir = Path.Combine(_rootPath, "workspace", "memory", "sessions");
            Directory.CreateDirectory(SummariesDir);
        }

        public string SessionsDir { get; }
        public string SummariesDir { get; }

        public SessionRetentionService BuildService()
        {
            var workspaceMock = new Mock<IWorkspaceService>();
            workspaceMock.SetupGet(w => w.RootPath).Returns(_rootPath);
            workspaceMock.Setup(w => w.LoadAppConfigAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WorkspaceAppConfig
                {
                    WorkspaceVersion = 1,
                    BootstrapCompleted = true,
                    ActiveMainSessionId = _activeMainSessionId,
                    AutoSessionRetentionDays = 7,
                    AutoSessionRetentionMaxPerTask = 3,
                });

            return new SessionRetentionService(workspaceMock.Object);
        }

        public void Dispose()
        {
            try { Directory.Delete(_rootPath, recursive: true); } catch { /* ignore */ }
        }
    }
}
