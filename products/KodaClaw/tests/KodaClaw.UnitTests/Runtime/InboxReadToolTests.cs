using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Tools;
using Kode.Agent.Sdk.Core.Abstractions;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.Runtime;

public sealed class InboxReadToolTests
{
    private readonly Mock<IInboxRepository> _repoMock;
    private readonly InboxReadTool _tool;

    public InboxReadToolTests()
    {
        _repoMock = new Mock<IInboxRepository>();
        _repoMock
            .Setup(r => r.ListAsync(It.IsAny<InboxQuery?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<InboxItem>());
        _tool = new InboxReadTool(_repoMock.Object);
    }

    // ── Metadata ──────────────────────────────────────────────────────────────

    [Fact]
    public void Tool_name_is_inbox_read()
        => _tool.Name.Should().Be("inbox_read");

    [Fact]
    public void Tool_does_not_require_approval()
        => _tool.Attributes.RequiresApproval.Should().BeFalse();

    [Fact]
    public void Tool_is_readonly()
        => _tool.Attributes.ReadOnly.Should().BeTrue();

    // ── Default status = open ─────────────────────────────────────────────────

    [Fact]
    public async Task Execute_defaults_to_open_status_when_status_not_provided()
    {
        InboxQuery? captured = null;
        _repoMock
            .Setup(r => r.ListAsync(It.IsAny<InboxQuery?>(), It.IsAny<CancellationToken>()))
            .Callback<InboxQuery?, CancellationToken>((q, _) => captured = q)
            .ReturnsAsync(Array.Empty<InboxItem>());

        await ExecuteAsync(new InboxReadArgs());

        captured.Should().NotBeNull();
        captured!.Status.Should().Be(InboxItemStatus.Open);
    }

    [Fact]
    public async Task Execute_passes_status_filter_to_repository()
    {
        InboxQuery? captured = null;
        _repoMock
            .Setup(r => r.ListAsync(It.IsAny<InboxQuery?>(), It.IsAny<CancellationToken>()))
            .Callback<InboxQuery?, CancellationToken>((q, _) => captured = q)
            .ReturnsAsync(Array.Empty<InboxItem>());

        await ExecuteAsync(new InboxReadArgs { Status = "resolved" });

        captured!.Status.Should().Be(InboxItemStatus.Resolved);
    }

    // ── Limit capping ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_caps_limit_at_50()
    {
        InboxQuery? captured = null;
        _repoMock
            .Setup(r => r.ListAsync(It.IsAny<InboxQuery?>(), It.IsAny<CancellationToken>()))
            .Callback<InboxQuery?, CancellationToken>((q, _) => captured = q)
            .ReturnsAsync(Array.Empty<InboxItem>());

        await ExecuteAsync(new InboxReadArgs { Limit = 999 });

        captured!.Limit.Should().Be(50);
    }

    [Fact]
    public async Task Execute_defaults_limit_to_20_when_not_provided()
    {
        InboxQuery? captured = null;
        _repoMock
            .Setup(r => r.ListAsync(It.IsAny<InboxQuery?>(), It.IsAny<CancellationToken>()))
            .Callback<InboxQuery?, CancellationToken>((q, _) => captured = q)
            .ReturnsAsync(Array.Empty<InboxItem>());

        await ExecuteAsync(new InboxReadArgs());

        captured!.Limit.Should().Be(20);
    }

    // ── Result shape ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_returns_items_with_total_count()
    {
        var items = new[]
        {
            new InboxItem(
                Id: "agent-note-abc123",
                Kind: InboxItemKind.Information,
                Status: InboxItemStatus.Open,
                Title: "Test finding",
                Summary: "Found something important.",
                Source: "agent",
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                RequiresAction: false),
        };

        _repoMock
            .Setup(r => r.ListAsync(It.IsAny<InboxQuery?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);

        var result = await ExecuteAsync(new InboxReadArgs());

        result.Success.Should().BeTrue();

        var json = JsonSerializer.Serialize(result.Value);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("total").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("items").GetArrayLength().Should().Be(1);

        var first = doc.RootElement.GetProperty("items")[0];
        first.GetProperty("title").GetString().Should().Be("Test finding");
        first.GetProperty("requiresAction").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Execute_returns_success_with_empty_list_when_no_items()
    {
        var result = await ExecuteAsync(new InboxReadArgs());

        result.Success.Should().BeTrue();
        var json = JsonSerializer.Serialize(result.Value);
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("total").GetInt32().Should().Be(0);
    }

    // ── Invalid status falls back to open ─────────────────────────────────────

    [Fact]
    public async Task Execute_falls_back_to_open_for_unknown_status()
    {
        InboxQuery? captured = null;
        _repoMock
            .Setup(r => r.ListAsync(It.IsAny<InboxQuery?>(), It.IsAny<CancellationToken>()))
            .Callback<InboxQuery?, CancellationToken>((q, _) => captured = q)
            .ReturnsAsync(Array.Empty<InboxItem>());

        await ExecuteAsync(new InboxReadArgs { Status = "garbage" });

        captured!.Status.Should().Be(InboxItemStatus.Open);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Task<ToolResult> ExecuteAsync(InboxReadArgs args)
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
