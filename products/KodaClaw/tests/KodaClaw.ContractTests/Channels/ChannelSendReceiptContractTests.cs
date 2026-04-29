using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using Xunit;

namespace KodaClaw.ContractTests.Channels;

public sealed class ChannelSendReceiptContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Receipt_should_json_round_trip_with_external_message_id()
    {
        var sentAt = new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero);
        var receipt = new ChannelSendReceipt("msg-12345", sentAt);

        var json = JsonSerializer.Serialize(receipt, JsonOptions);
        var round = JsonSerializer.Deserialize<ChannelSendReceipt>(json, JsonOptions);

        round.Should().NotBeNull();
        round!.ExternalMessageId.Should().Be("msg-12345");
        round.SentAt.Should().Be(sentAt);
    }

    [Fact]
    public void Receipt_should_allow_null_external_message_id()
    {
        var receipt = new ChannelSendReceipt(null, DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(receipt, JsonOptions);
        var round = JsonSerializer.Deserialize<ChannelSendReceipt>(json, JsonOptions);

        round.Should().NotBeNull();
        round!.ExternalMessageId.Should().BeNull();
    }

    [Fact]
    public async Task Default_SendWithReceiptAsync_should_delegate_to_SendAsync_and_return_null_message_id()
    {
        var fake = new FakeBasicConnector();
        IChannelConnector connector = fake;
        var draft = BuildDraft();

        var receipt = await connector.SendWithReceiptAsync(draft, CancellationToken.None);

        fake.SendCount.Should().Be(1);
        receipt.ExternalMessageId.Should().BeNull();
        receipt.SentAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Default_SupportsEdit_should_be_false()
    {
        IChannelConnector connector = new FakeBasicConnector();

        connector.SupportsEdit.Should().BeFalse();
    }

    [Fact]
    public async Task Default_EditAsync_should_throw_NotSupportedException()
    {
        IChannelConnector connector = new FakeBasicConnector();

        var act = async () => await connector.EditAsync(
            externalThreadId: "thread-1",
            externalMessageId: "msg-1",
            text: "updated",
            format: OutboundMessageFormat.PlainText,
            cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    private static ChannelOutboundDraft BuildDraft() => new(
        DraftId: "draft-1",
        BindingId: "binding-1",
        ConnectorKind: ChannelConnectorKind.Telegram,
        AccountId: "account-1",
        ExternalThreadId: "thread-1",
        MessageText: "hello");

    private sealed class FakeBasicConnector : IChannelConnector
    {
        public int SendCount { get; private set; }

        public ChannelConnectorKind Kind => ChannelConnectorKind.Telegram;

        public Task StartAsync(
            ChannelAccount account,
            Func<ChannelEventEnvelope, CancellationToken, Task> onEvent,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SendAsync(ChannelOutboundDraft draft, CancellationToken cancellationToken = default)
        {
            SendCount++;
            return Task.CompletedTask;
        }
    }
}
