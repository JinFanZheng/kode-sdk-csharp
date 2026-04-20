using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Storage.Json.Repositories;
using Xunit;

namespace KodaClaw.UnitTests.ChannelHub;

public sealed class JsonThreadBindingRepositoryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private JsonThreadBindingRepository CreateRepository() => new(_tempDir);

    [Fact]
    public async Task Repository_should_round_trip_binding_and_lookup_by_external_thread()
    {
        var repository = CreateRepository();
        var binding = BuildBinding(
            id: "binding-dm-001",
            connectorKind: ChannelConnectorKind.Telegram,
            threadType: ChannelThreadType.DirectMessage,
            timestamp: new DateTimeOffset(2026, 3, 19, 11, 0, 0, TimeSpan.Zero));

        await repository.UpsertAsync(binding);

        var stored = await repository.GetByIdAsync(binding.Id);
        var externalLookup = await repository.GetByExternalThreadAsync(
            binding.ConnectorKind,
            binding.AccountId,
            binding.ExternalThreadId);

        stored.Should().Be(binding);
        externalLookup.Should().Be(binding);
    }

    [Fact]
    public async Task List_should_apply_account_thread_type_and_session_filters()
    {
        var repository = CreateRepository();
        var baseTime = new DateTimeOffset(2026, 3, 19, 12, 0, 0, TimeSpan.Zero);

        await repository.UpsertAsync(BuildBinding(
            id: "binding-group-1",
            connectorKind: ChannelConnectorKind.Telegram,
            threadType: ChannelThreadType.Group,
            accountId: "account-telegram",
            timestamp: baseTime));
        await repository.UpsertAsync(BuildBinding(
            id: "binding-dm-2",
            connectorKind: ChannelConnectorKind.Telegram,
            threadType: ChannelThreadType.DirectMessage,
            accountId: "account-telegram",
            timestamp: baseTime.AddMinutes(1)));
        await repository.UpsertAsync(BuildBinding(
            id: "binding-webhook-3",
            connectorKind: ChannelConnectorKind.GenericWebhook,
            threadType: ChannelThreadType.DirectMessage,
            accountId: "account-webhook",
            timestamp: baseTime.AddMinutes(2)));

        var telegramBindings = await repository.ListAsync(new ChannelQuery(
            ConnectorKind: ChannelConnectorKind.Telegram,
            Limit: 10));
        var groupBindings = await repository.ListAsync(new ChannelQuery(
            ThreadType: ChannelThreadType.Group,
            Limit: 10));
        var dmSessionBindings = await repository.ListAsync(new ChannelQuery(
            SessionKind: SessionKind.ChannelDirectMessage,
            SessionId: "session-binding-dm-2",
            Limit: 10));

        telegramBindings.Select(static item => item.Id).Should().BeEquivalentTo(new[] { "binding-group-1", "binding-dm-2" });
        groupBindings.Should().ContainSingle().Which.Id.Should().Be("binding-group-1");
        dmSessionBindings.Should().ContainSingle().Which.Id.Should().Be("binding-dm-2");
    }

    [Fact]
    public async Task Repository_should_persist_bindings_to_filesystem()
    {
        var repository = CreateRepository();
        await repository.UpsertAsync(BuildBinding(
            id: "binding-init",
            connectorKind: ChannelConnectorKind.Telegram,
            threadType: ChannelThreadType.DirectMessage,
            timestamp: new DateTimeOffset(2026, 3, 19, 14, 0, 0, TimeSpan.Zero)));

        var stored = await repository.GetByIdAsync("binding-init");
        stored.Should().NotBeNull();
        stored!.Id.Should().Be("binding-init");
    }

    // ── GetBySessionIdAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetBySessionIdAsync_returns_matching_binding()
    {
        var repository = CreateRepository();
        var timestamp = new DateTimeOffset(2026, 4, 10, 10, 0, 0, TimeSpan.Zero);
        var binding = BuildBinding(
            id: "binding-session-001",
            connectorKind: ChannelConnectorKind.Telegram,
            threadType: ChannelThreadType.DirectMessage,
            timestamp: timestamp);

        await repository.UpsertAsync(binding);

        var result = await repository.GetBySessionIdAsync(binding.SessionId);

        result.Should().NotBeNull();
        result!.Id.Should().Be("binding-session-001");
        result.SessionId.Should().Be(binding.SessionId);
    }

    [Fact]
    public async Task Roundtrip_preserves_ThinkingEnabled_and_StreamOverride()
    {
        var repository = CreateRepository();
        var baseBinding = BuildBinding(
            id: "binding-toggles",
            connectorKind: ChannelConnectorKind.Telegram,
            threadType: ChannelThreadType.DirectMessage,
            timestamp: new DateTimeOffset(2026, 4, 20, 9, 0, 0, TimeSpan.Zero));
        var binding = baseBinding with { ThinkingEnabled = true, StreamOverride = false };

        await repository.UpsertAsync(binding);

        var stored = await repository.GetByIdAsync(binding.Id);
        stored.Should().NotBeNull();
        stored!.ThinkingEnabled.Should().BeTrue();
        stored.StreamOverride.Should().BeFalse();
    }

    [Fact]
    public async Task Roundtrip_defaults_ThinkingEnabled_false_and_StreamOverride_null()
    {
        var repository = CreateRepository();
        var binding = BuildBinding(
            id: "binding-defaults",
            connectorKind: ChannelConnectorKind.Telegram,
            threadType: ChannelThreadType.DirectMessage,
            timestamp: new DateTimeOffset(2026, 4, 20, 9, 30, 0, TimeSpan.Zero));

        await repository.UpsertAsync(binding);

        var stored = await repository.GetByIdAsync(binding.Id);
        stored.Should().NotBeNull();
        stored!.ThinkingEnabled.Should().BeFalse();
        stored.StreamOverride.Should().BeNull();
    }

    [Fact]
    public async Task GetBySessionIdAsync_returns_null_when_session_not_found()
    {
        var repository = CreateRepository();
        var timestamp = new DateTimeOffset(2026, 4, 10, 10, 0, 0, TimeSpan.Zero);
        await repository.UpsertAsync(BuildBinding(
            id: "binding-other",
            connectorKind: ChannelConnectorKind.Telegram,
            threadType: ChannelThreadType.DirectMessage,
            timestamp: timestamp));

        var result = await repository.GetBySessionIdAsync("session-does-not-exist");

        result.Should().BeNull();
    }

    private static ThreadBinding BuildBinding(
        string id,
        ChannelConnectorKind connectorKind,
        ChannelThreadType threadType,
        DateTimeOffset timestamp,
        string accountId = "account-telegram")
    {
        var sessionKind = threadType == ChannelThreadType.DirectMessage
            ? SessionKind.ChannelDirectMessage
            : SessionKind.ChannelGroup;

        return new ThreadBinding(
            Id: id,
            ConnectorKind: connectorKind,
            AccountId: accountId,
            ExternalThreadId: $"thread-{id}",
            ThreadType: threadType,
            SessionId: $"session-{id}",
            SessionKind: sessionKind,
            ChannelIdentity: new ChannelIdentity(
                Id: $"identity-{id}",
                Username: $"user_{id}",
                DisplayName: $"User {id}"),
            PolicyId: $"policy-{threadType}",
            DeliveryRuleId: $"delivery-{threadType}",
            CreatedAt: timestamp,
            UpdatedAt: timestamp.AddMinutes(1),
            LastInboundAt: timestamp.AddMinutes(2),
            LastOutboundAt: timestamp.AddMinutes(3),
            LastMessagePreview: $"preview:{id}");
    }
}
