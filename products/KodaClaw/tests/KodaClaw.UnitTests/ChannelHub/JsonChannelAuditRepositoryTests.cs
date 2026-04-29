using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Storage.Json.Repositories;
using Xunit;

namespace KodaClaw.UnitTests.ChannelHub;

public sealed class JsonChannelAuditRepositoryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private JsonChannelAuditRepository CreateRepository() => new(_tempDir);

    [Fact]
    public async Task Repository_should_round_trip_audit_entries_and_return_recent_first()
    {
        var repository = CreateRepository();
        var baseTime = new DateTimeOffset(2026, 3, 19, 15, 0, 0, TimeSpan.Zero);

        var oldest = BuildEntry(
            id: "audit-001",
            bindingId: "binding-main",
            createdAt: baseTime,
            summary: "oldest");
        var middle = BuildEntry(
            id: "audit-002",
            bindingId: "binding-main",
            createdAt: baseTime.AddMinutes(1),
            summary: "middle");
        var newest = BuildEntry(
            id: "audit-003",
            bindingId: "binding-main",
            createdAt: baseTime.AddMinutes(2),
            summary: "newest",
            deliveryMode: DeliveryMode.RequireApproval);

        await repository.AppendAsync(oldest);
        await repository.AppendAsync(middle);
        await repository.AppendAsync(newest);
        await repository.AppendAsync(BuildEntry(
            id: "audit-other-001",
            bindingId: "binding-other",
            createdAt: baseTime.AddMinutes(3),
            summary: "other binding"));

        var recent = await repository.ListByBindingIdAsync("binding-main", limit: 2);

        recent.Should().HaveCount(2);
        // ListByBindingIdAsync returns newest-first (descending), matching SQLite ORDER BY created_at DESC
        recent.Select(static item => item.Id).Should().Equal("audit-003", "audit-002");
        recent[0].Should().Be(newest);
        recent[0].DeliveryMode.Should().Be(DeliveryMode.RequireApproval);
        recent[0].MetadataJson.Should().Be("""{"trace":"audit-003"}""");
    }

    [Fact]
    public async Task Repository_should_respect_limit_when_listing_entries()
    {
        var repository = CreateRepository();
        var baseTime = new DateTimeOffset(2026, 3, 19, 16, 0, 0, TimeSpan.Zero);

        await repository.AppendAsync(BuildEntry(
            id: "audit-limit-1",
            bindingId: "binding-limit",
            createdAt: baseTime,
            summary: "first"));
        await repository.AppendAsync(BuildEntry(
            id: "audit-limit-2",
            bindingId: "binding-limit",
            createdAt: baseTime.AddMinutes(1),
            summary: "second"));

        var items = await repository.ListByBindingIdAsync("binding-limit", limit: 10);

        items.Should().HaveCount(2);
        // ListByBindingIdAsync returns newest-first (descending)
        items.Select(static item => item.Id).Should().Equal("audit-limit-2", "audit-limit-1");
    }

    [Fact]
    public async Task Repository_should_persist_audit_entries_to_filesystem()
    {
        var repository = CreateRepository();
        await repository.AppendAsync(BuildEntry(
            id: "audit-init",
            bindingId: "binding-init",
            createdAt: new DateTimeOffset(2026, 3, 19, 17, 0, 0, TimeSpan.Zero),
            summary: "init"));

        var items = await repository.ListByBindingIdAsync("binding-init", limit: 10);
        items.Should().ContainSingle().Which.Id.Should().Be("audit-init");
    }

    private static ChannelAuditEntry BuildEntry(
        string id,
        string bindingId,
        DateTimeOffset createdAt,
        string summary,
        DeliveryMode? deliveryMode = DeliveryMode.DraftApproval)
    {
        return new ChannelAuditEntry(
            Id: id,
            BindingId: bindingId,
            ConnectorKind: ChannelConnectorKind.GenericWebhook,
            AccountId: "webhook-main",
            ExternalThreadId: $"thread-{bindingId}",
            ThreadType: ChannelThreadType.Group,
            EventType: "message.received",
            CreatedAt: createdAt,
            SessionId: "session-channel-group-001",
            ApprovalId: deliveryMode is null ? null : $"approval-{id}",
            DeliveryMode: deliveryMode,
            ExternalMessageId: $"external-{id}",
            Summary: summary,
            MetadataJson: $$"""{"trace":"{{id}}"}""");
    }
}
