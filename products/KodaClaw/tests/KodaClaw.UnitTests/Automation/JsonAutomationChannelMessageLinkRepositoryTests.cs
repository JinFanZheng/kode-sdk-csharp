using FluentAssertions;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Channels;
using KodaClaw.Storage.Json.Repositories;
using Xunit;

namespace KodaClaw.UnitTests.Automation;

public sealed class JsonAutomationChannelMessageLinkRepositoryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private JsonAutomationChannelMessageLinkRepository CreateRepository() => new(_tempDir);

    [Fact]
    public async Task Upsert_and_lookup_by_external_message_should_round_trip_link()
    {
        var repository = CreateRepository();
        var link = BuildLink(
            id: JsonAutomationChannelMessageLinkRepository.BuildId(
                ChannelConnectorKind.Telegram,
                "telegram-main",
                "12345",
                "67890"),
            runId: "run-001",
            externalMessageId: "67890");

        await repository.UpsertAsync(link);

        var reloaded = await repository.GetByExternalMessageAsync(
            ChannelConnectorKind.Telegram,
            "telegram-main",
            "12345",
            "67890");

        reloaded.Should().Be(link);
    }

    [Fact]
    public async Task Lookup_should_load_index_from_existing_files()
    {
        var first = CreateRepository();
        var link = BuildLink("link-existing", "run-existing", "msg-existing");
        await first.UpsertAsync(link);

        var second = CreateRepository();
        var reloaded = await second.GetByExternalMessageAsync(
            ChannelConnectorKind.Telegram,
            "telegram-main",
            "12345",
            "msg-existing");

        reloaded.Should().Be(link);
    }

    [Fact]
    public async Task ListByRunId_should_filter_and_order_links()
    {
        var repository = CreateRepository();
        await repository.UpsertAsync(BuildLink("link-old", "run-001", "msg-old") with
        {
            CreatedAt = new DateTimeOffset(2026, 3, 18, 9, 0, 0, TimeSpan.Zero),
        });
        await repository.UpsertAsync(BuildLink("link-new", "run-001", "msg-new") with
        {
            CreatedAt = new DateTimeOffset(2026, 3, 18, 10, 0, 0, TimeSpan.Zero),
        });
        await repository.UpsertAsync(BuildLink("link-other", "run-002", "msg-other"));

        var links = await repository.ListByRunIdAsync("run-001");

        links.Select(static link => link.Id).Should().Equal("link-new", "link-old");
    }

    private static AutomationChannelMessageLink BuildLink(
        string id,
        string runId,
        string externalMessageId)
        => new(
            Id: id,
            AutomationId: "auto-digest",
            RunId: runId,
            SessionId: "auto-20260318090000-digest-abcd1234",
            BindingId: "binding-telegram-001",
            ConnectorKind: ChannelConnectorKind.Telegram,
            AccountId: "telegram-main",
            ExternalThreadId: "12345",
            ExternalMessageId: externalMessageId,
            CreatedAt: new DateTimeOffset(2026, 3, 18, 9, 1, 0, TimeSpan.Zero),
            ExpiresAt: new DateTimeOffset(2026, 4, 17, 9, 1, 0, TimeSpan.Zero),
            Summary: "Digest posted.");
}
