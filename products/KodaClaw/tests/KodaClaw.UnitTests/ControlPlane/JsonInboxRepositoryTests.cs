using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Storage.Json.Repositories;
using Xunit;

namespace KodaClaw.UnitTests.ControlPlane;

public sealed class JsonInboxRepositoryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private JsonInboxRepository CreateRepository() => new(_tempDir);

    [Fact]
    public async Task Repository_should_round_trip_inbox_item()
    {
        var repository = CreateRepository();
        var createdAt = new DateTimeOffset(2026, 3, 18, 12, 0, 0, TimeSpan.Zero);
        var item = new InboxItem(
            Id: "inbox-001",
            Kind: InboxItemKind.Approval,
            Status: InboxItemStatus.Open,
            Title: "Review outbound reply",
            Summary: "A channel reply is waiting for approval.",
            Source: "channel.telegram",
            CreatedAt: createdAt,
            UpdatedAt: createdAt,
            RequiresAction: true,
            Route: "/approvals/approval-001",
            SessionId: "session-main",
            CorrelationId: "corr-001",
            ApprovalId: "approval-001",
            PayloadJson: """{"channel":"telegram"}""");

        await repository.UpsertAsync(item);

        var reloaded = await repository.GetByIdAsync(item.Id);
        var listed = await repository.ListAsync(new InboxQuery(Status: InboxItemStatus.Open, Limit: 10));

        reloaded.Should().Be(item);
        listed.Should().ContainSingle().Which.Should().Be(item);
    }

    [Fact]
    public async Task Repository_should_filter_items_and_persist_status_transitions()
    {
        var repository = CreateRepository();
        var baseTime = new DateTimeOffset(2026, 3, 18, 13, 0, 0, TimeSpan.Zero);

        await repository.UpsertAsync(new InboxItem(
            Id: "inbox-open",
            Kind: InboxItemKind.Alert,
            Status: InboxItemStatus.Open,
            Title: "Automation failed",
            Summary: "Daily job needs attention.",
            Source: "automation.scheduler",
            CreatedAt: baseTime,
            UpdatedAt: baseTime,
            RequiresAction: true,
            SessionId: "session-automation"));
        await repository.UpsertAsync(new InboxItem(
            Id: "inbox-ack",
            Kind: InboxItemKind.Information,
            Status: InboxItemStatus.Acknowledged,
            Title: "Plugin installed",
            Summary: "The browser plugin is now available.",
            Source: "plugin.manager",
            CreatedAt: baseTime.AddMinutes(1),
            UpdatedAt: baseTime.AddMinutes(1),
            RequiresAction: false,
            SessionId: "session-main"));

        var updated = await repository.UpdateStatusAsync(
            "inbox-open",
            InboxItemStatus.Resolved,
            updatedAt: baseTime.AddMinutes(5),
            resolvedAt: baseTime.AddMinutes(5));

        var actionItems = await repository.ListAsync(new InboxQuery(RequiresAction: true, Limit: 10));
        var resolvedItems = await repository.ListAsync(new InboxQuery(Status: InboxItemStatus.Resolved, Limit: 10));

        updated.Should().BeTrue();
        actionItems.Select(item => item.Id).Should().Equal("inbox-open");
        resolvedItems.Should().ContainSingle();
        resolvedItems[0].Id.Should().Be("inbox-open");
        resolvedItems[0].ResolvedAt.Should().Be(baseTime.AddMinutes(5));
    }

}
