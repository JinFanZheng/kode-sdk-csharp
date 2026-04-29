using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Inbox;
using Xunit;

namespace KodaClaw.ContractTests.Inbox;

public sealed class InboxContractsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Inbox_query_response_should_serialize_items_with_string_enums()
    {
        var payload = new InboxQueryResponse(
            [
                new InboxItem(
                    Id: "inbox-001",
                    Kind: InboxItemKind.Approval,
                    Status: InboxItemStatus.Open,
                    Title: "Approve outbound reply",
                    Summary: "Needs review.",
                    Source: "channel.telegram",
                    CreatedAt: new DateTimeOffset(2026, 3, 18, 18, 0, 0, TimeSpan.Zero),
                    UpdatedAt: new DateTimeOffset(2026, 3, 18, 18, 0, 0, TimeSpan.Zero),
                    RequiresAction: true)
            ]);

        var json = JsonSerializer.Serialize(payload, JsonOptions);

        json.Should().Contain("\"items\"");
        json.Should().Contain("\"kind\":\"Approval\"");
        json.Should().Contain("\"status\":\"Open\"");
    }

    [Fact]
    public void Inbox_status_update_request_should_round_trip()
    {
        var request = new InboxStatusUpdateRequest("Resolved");

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<InboxStatusUpdateRequest>(json, JsonOptions);

        json.Should().Contain("\"status\":\"Resolved\"");
        roundTrip.Should().Be(request);
    }
}
