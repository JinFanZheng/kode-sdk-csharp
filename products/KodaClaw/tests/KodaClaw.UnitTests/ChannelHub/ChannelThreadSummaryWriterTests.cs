using FluentAssertions;
using KodaClaw.ChannelHub;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Runtime.Sessions;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.ChannelHub;

public sealed class ChannelThreadSummaryWriterTests : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly Mock<IWorkspaceService> _workspaceMock;
    private readonly ChannelThreadSummaryWriter _writer;

    public ChannelThreadSummaryWriterTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), $"ctsw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceRoot);

        _workspaceMock = new Mock<IWorkspaceService>();
        _workspaceMock.SetupGet(w => w.RootPath).Returns(_workspaceRoot);

        _writer = new ChannelThreadSummaryWriter(_workspaceMock.Object);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Write_should_create_summary_file_under_workspace_channels()
    {
        var binding = BuildBinding("binding-001");
        var outcome = BuildOutcome(ChannelTurnOutcomeKind.Delivered, "Reply sent.");

        await _writer.WriteAsync(binding, outcome);

        var expectedPath = Path.Combine(
            _workspaceRoot, "workspace", "channels", "binding-001", "SUMMARY.md");
        File.Exists(expectedPath).Should().BeTrue();
    }

    [Fact]
    public async Task Write_should_append_entry_with_kind_and_summary()
    {
        var binding = BuildBinding("binding-002");
        var outcome = BuildOutcome(ChannelTurnOutcomeKind.Delivered, "Hello back.");

        await _writer.WriteAsync(binding, outcome);

        var filePath = Path.Combine(
            _workspaceRoot, "workspace", "channels", "binding-002", "SUMMARY.md");
        var content = await File.ReadAllTextAsync(filePath);

        content.Should().Contain("delivered");
        content.Should().Contain("Hello back.");
    }

    [Fact]
    public async Task Write_should_append_multiple_entries()
    {
        var binding = BuildBinding("binding-003");
        var outcome1 = BuildOutcome(ChannelTurnOutcomeKind.Delivered, "First reply.");
        var outcome2 = BuildOutcome(ChannelTurnOutcomeKind.DraftCreated, "Draft for approval.");

        await _writer.WriteAsync(binding, outcome1);
        await _writer.WriteAsync(binding, outcome2);

        var filePath = Path.Combine(
            _workspaceRoot, "workspace", "channels", "binding-003", "SUMMARY.md");
        var content = await File.ReadAllTextAsync(filePath);

        content.Should().Contain("delivered");
        content.Should().Contain("draft_created");
        content.Should().Contain("First reply.");
        content.Should().Contain("Draft for approval.");
    }

    [Fact]
    public async Task Write_should_truncate_long_summary_preview()
    {
        var binding = BuildBinding("binding-004");
        var longSummary = new string('x', 200);
        var outcome = BuildOutcome(ChannelTurnOutcomeKind.Delivered, longSummary);

        await _writer.WriteAsync(binding, outcome);

        var filePath = Path.Combine(
            _workspaceRoot, "workspace", "channels", "binding-004", "SUMMARY.md");
        var content = await File.ReadAllTextAsync(filePath);

        content.Should().Contain("...");
        var line = content.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).First();
        line.Length.Should().BeLessThan(200);
    }

    [Fact]
    public async Task Write_should_keep_at_most_60_lines_when_file_exceeds_window()
    {
        var binding = BuildBinding("binding-window-1");
        var filePath = Path.Combine(
            _workspaceRoot, "workspace", "channels", "binding-window-1", "SUMMARY.md");

        // Write 65 entries
        for (var i = 0; i < 65; i++)
        {
            await _writer.WriteAsync(binding, BuildOutcome(ChannelTurnOutcomeKind.Delivered, $"msg-{i}"));
        }

        var lines = await File.ReadAllLinesAsync(filePath);
        lines.Where(l => l.Length > 0).Should().HaveCount(60);
    }

    [Fact]
    public async Task Write_should_retain_tail_when_trimming()
    {
        var binding = BuildBinding("binding-window-2");
        var filePath = Path.Combine(
            _workspaceRoot, "workspace", "channels", "binding-window-2", "SUMMARY.md");

        for (var i = 0; i < 62; i++)
        {
            await _writer.WriteAsync(binding, BuildOutcome(ChannelTurnOutcomeKind.Delivered, $"entry-{i}"));
        }

        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Contain("entry-61", because: "most recent entry must be retained after trim");
        content.Should().NotContain("entry-0", because: "oldest entry must be removed after trim");
    }

    [Fact]
    public async Task Write_should_not_trim_when_exactly_at_window_limit()
    {
        var binding = BuildBinding("binding-window-3");
        var filePath = Path.Combine(
            _workspaceRoot, "workspace", "channels", "binding-window-3", "SUMMARY.md");

        for (var i = 0; i < 60; i++)
        {
            await _writer.WriteAsync(binding, BuildOutcome(ChannelTurnOutcomeKind.Delivered, $"msg-{i}"));
        }

        var lines = await File.ReadAllLinesAsync(filePath);
        lines.Where(l => l.Length > 0).Should().HaveCount(60);
        (await File.ReadAllTextAsync(filePath)).Should().Contain("msg-0");
    }

    // -----------------------------------------------------------------------
    // KC-2206: Compression threshold tests (no LLM provider → fallback truncation)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Write_should_truncate_to_target_lines_when_exceeds_compression_threshold_without_llm()
    {
        // Arrange: use custom options with low threshold so we can trigger without 80+ entries.
        var options = new ChannelSessionOptions
        {
            SummaryCompressionThreshold = 10,
            SummaryCompressionTargetLines = 5,
        };
        // No IModelProvider injected → LLM path is skipped, falls back to truncation.
        var writer = new ChannelThreadSummaryWriter(_workspaceMock.Object, options: options);

        var binding = BuildBinding("binding-compress-1");
        var filePath = Path.Combine(
            _workspaceRoot, "workspace", "channels", "binding-compress-1", "SUMMARY.md");

        // Write exactly threshold+1 entries so one compression pass fires and the file ends at targetLines.
        for (var i = 0; i < 11; i++)
        {
            await writer.WriteAsync(binding, BuildOutcome(ChannelTurnOutcomeKind.Delivered, $"compress-msg-{i}"));
        }

        var lines = await File.ReadAllLinesAsync(filePath);
        lines.Where(l => l.Length > 0).Should().HaveCount(5,
            because: "when threshold is exceeded and no LLM provider is set, writer falls back to SummaryCompressionTargetLines truncation");
    }

    [Fact]
    public async Task Write_should_retain_most_recent_entries_after_compression_fallback()
    {
        var options = new ChannelSessionOptions
        {
            SummaryCompressionThreshold = 8,
            SummaryCompressionTargetLines = 4,
        };
        var writer = new ChannelThreadSummaryWriter(_workspaceMock.Object, options: options);

        var binding = BuildBinding("binding-compress-2");
        var filePath = Path.Combine(
            _workspaceRoot, "workspace", "channels", "binding-compress-2", "SUMMARY.md");

        // Write exactly threshold+1 (9) entries so one compression pass fires on the last write.
        for (var i = 0; i < 9; i++)
        {
            await writer.WriteAsync(binding, BuildOutcome(ChannelTurnOutcomeKind.Delivered, $"compress-entry-{i}"));
        }

        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Contain("compress-entry-8", because: "most recent entry must survive compression fallback");
        content.Should().NotContain("compress-entry-0", because: "oldest entry must be dropped after compression fallback");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static ThreadBinding BuildBinding(string id)
    {
        var now = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);
        return new ThreadBinding(
            Id: id,
            ConnectorKind: ChannelConnectorKind.Telegram,
            AccountId: "account-001",
            ExternalThreadId: "ext-thread-001",
            ThreadType: ChannelThreadType.DirectMessage,
            SessionId: $"session-{id}",
            SessionKind: SessionKind.ChannelDirectMessage,
            ChannelIdentity: new ChannelIdentity("tg-001", "TestBot", null),
            PolicyId: "policy-001",
            DeliveryRuleId: "rule-001",
            CreatedAt: now,
            UpdatedAt: now);
    }

    private static ChannelTurnOutcome BuildOutcome(ChannelTurnOutcomeKind kind, string summary)
    {
        return new ChannelTurnOutcome(
            Kind: kind,
            Summary: summary,
            OccurredAt: new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero));
    }
}
