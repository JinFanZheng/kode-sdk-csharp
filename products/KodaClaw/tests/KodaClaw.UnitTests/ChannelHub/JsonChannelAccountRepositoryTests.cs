using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Storage.Json.Repositories;
using Xunit;

namespace KodaClaw.UnitTests.ChannelHub;

public sealed class JsonChannelAccountRepositoryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private JsonChannelAccountRepository CreateRepository() => new(_tempDir);

    [Fact]
    public async Task Repository_should_round_trip_channel_account()
    {
        var repository = CreateRepository();
        var account = BuildAccount(
            id: "telegram-main",
            connectorKind: ChannelConnectorKind.Telegram,
            state: ChannelAccountState.Connected,
            timestamp: new DateTimeOffset(2026, 3, 19, 8, 0, 0, TimeSpan.Zero));

        await repository.UpsertAsync(account);

        var stored = await repository.GetByIdAsync(account.Id);
        var list = await repository.ListAsync(new ChannelAccountQuery(
            ConnectorKind: ChannelConnectorKind.Telegram,
            State: ChannelAccountState.Connected,
            Limit: 10));

        stored.Should().Be(account);
        list.Should().ContainSingle().Which.Should().Be(account);
    }

    [Fact]
    public async Task List_should_apply_connector_and_state_filters()
    {
        var repository = CreateRepository();
        var baseTime = new DateTimeOffset(2026, 3, 19, 9, 0, 0, TimeSpan.Zero);

        await repository.UpsertAsync(BuildAccount(
            id: "telegram-1",
            connectorKind: ChannelConnectorKind.Telegram,
            state: ChannelAccountState.Connected,
            timestamp: baseTime));
        await repository.UpsertAsync(BuildAccount(
            id: "telegram-2",
            connectorKind: ChannelConnectorKind.Telegram,
            state: ChannelAccountState.Degraded,
            timestamp: baseTime.AddMinutes(1)));
        await repository.UpsertAsync(BuildAccount(
            id: "webhook-1",
            connectorKind: ChannelConnectorKind.GenericWebhook,
            state: ChannelAccountState.Connected,
            timestamp: baseTime.AddMinutes(2)));

        var telegramAccounts = await repository.ListAsync(new ChannelAccountQuery(
            ConnectorKind: ChannelConnectorKind.Telegram,
            Limit: 10));
        var degradedAccounts = await repository.ListAsync(new ChannelAccountQuery(
            State: ChannelAccountState.Degraded,
            Limit: 10));

        telegramAccounts.Select(static item => item.Id).Should().BeEquivalentTo(new[] { "telegram-1", "telegram-2" });
        degradedAccounts.Should().ContainSingle().Which.Id.Should().Be("telegram-2");
    }

    [Fact]
    public async Task Repository_should_persist_accounts_to_filesystem()
    {
        var repository = CreateRepository();
        await repository.UpsertAsync(BuildAccount(
            id: "telegram-init",
            connectorKind: ChannelConnectorKind.Telegram,
            state: ChannelAccountState.Disconnected,
            timestamp: new DateTimeOffset(2026, 3, 19, 10, 0, 0, TimeSpan.Zero)));

        var stored = await repository.GetByIdAsync("telegram-init");
        stored.Should().NotBeNull();
        stored!.Id.Should().Be("telegram-init");
    }

    private static ChannelAccount BuildAccount(
        string id,
        ChannelConnectorKind connectorKind,
        ChannelAccountState state,
        DateTimeOffset timestamp)
    {
        return new ChannelAccount(
            Id: id,
            ConnectorKind: connectorKind,
            DisplayName: $"Account {id}",
            State: state,
            CreatedAt: timestamp,
            UpdatedAt: timestamp.AddMinutes(1),
            ExternalAccountId: $"ext-{id}",
            CredentialReference: $"env:{id.ToUpperInvariant()}_TOKEN",
            Description: "Fixture channel account",
            ConfigurationJson: """{"parseMode":"markdown"}""",
            InboundEnabled: true,
            LastConnectedAt: state is ChannelAccountState.Connected ? timestamp.AddMinutes(2) : null,
            LastDisconnectedAt: state is ChannelAccountState.Disconnected ? timestamp.AddMinutes(3) : null,
            LastError: state is ChannelAccountState.Degraded ? "polling failed" : null);
    }
}
