using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Tools;
using Kode.Agent.Sdk.Core.Abstractions;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.Runtime;

public sealed class InboxCreateToolTests
{
    private readonly Mock<IInboxRepository> _repoMock;
    private readonly InboxCreateTool _tool;

    public InboxCreateToolTests()
    {
        _repoMock = new Mock<IInboxRepository>();
        _tool = new InboxCreateTool(_repoMock.Object);
    }

    // ── Metadata ──────────────────────────────────────────────────────────────

    [Fact]
    public void Tool_name_is_inbox_create()
        => _tool.Name.Should().Be("inbox_create");

    [Fact]
    public void Tool_does_not_require_approval()
        => _tool.Attributes.RequiresApproval.Should().BeFalse();

    [Fact]
    public void Tool_is_not_readonly()
        => _tool.Attributes.ReadOnly.Should().BeFalse();

    // ── Upsert with correct kind ──────────────────────────────────────────────

    [Fact]
    public async Task Execute_creates_information_kind_inbox_item()
    {
        InboxItem? captured = null;
        _repoMock
            .Setup(r => r.UpsertAsync(It.IsAny<InboxItem>(), It.IsAny<CancellationToken>()))
            .Callback<InboxItem, CancellationToken>((item, _) => captured = item)
            .Returns(Task.CompletedTask);

        var result = await ExecuteAsync(new InboxCreateArgs
        {
            Title = "Analysis complete",
            Summary = "I reviewed the codebase and found 3 areas that need attention.",
        });

        result.Success.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Kind.Should().Be(InboxItemKind.Information);
        captured.Source.Should().Be("agent");
        captured.Status.Should().Be(InboxItemStatus.Open);
        captured.Title.Should().Be("Analysis complete");
        captured.Summary.Should().Contain("3 areas");
    }

    // ── RequiresAction flag ───────────────────────────────────────────────────

    [Fact]
    public async Task Execute_sets_requires_action_when_specified()
    {
        InboxItem? captured = null;
        _repoMock
            .Setup(r => r.UpsertAsync(It.IsAny<InboxItem>(), It.IsAny<CancellationToken>()))
            .Callback<InboxItem, CancellationToken>((item, _) => captured = item)
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new InboxCreateArgs
        {
            Title = "Action required",
            Summary = "Please review and approve the changes.",
            RequiresAction = true,
        });

        captured!.RequiresAction.Should().BeTrue();
    }

    // ── Route ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_stores_route_when_provided()
    {
        InboxItem? captured = null;
        _repoMock
            .Setup(r => r.UpsertAsync(It.IsAny<InboxItem>(), It.IsAny<CancellationToken>()))
            .Callback<InboxItem, CancellationToken>((item, _) => captured = item)
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new InboxCreateArgs
        {
            Title = "Report ready",
            Summary = "Your weekly report is ready.",
            Route = "/canvas/weekly-report",
        });

        captured!.Route.Should().Be("/canvas/weekly-report");
    }

    // ── ID format ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_generates_agent_note_id()
    {
        InboxItem? captured = null;
        _repoMock
            .Setup(r => r.UpsertAsync(It.IsAny<InboxItem>(), It.IsAny<CancellationToken>()))
            .Callback<InboxItem, CancellationToken>((item, _) => captured = item)
            .Returns(Task.CompletedTask);

        await ExecuteAsync(new InboxCreateArgs
        {
            Title = "Test",
            Summary = "Test summary",
        });

        captured!.Id.Should().StartWith("agent-note-");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Task<ToolResult> ExecuteAsync(InboxCreateArgs args)
    {
        var context = new ToolContext
        {
            AgentId = "test-agent",
            CallId = "test-call",
            Sandbox = new Mock<ISandbox>().Object,
        };
        return _tool.ExecuteAsync((object)args, context, CancellationToken.None);
    }
}
