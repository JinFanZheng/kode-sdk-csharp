using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Approvals;
using Xunit;

namespace KodaClaw.ContractTests.Approvals;

public sealed class ApprovalContractsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Approval_query_should_round_trip_string_enums()
    {
        var payload = new ApprovalQuery(
            Status: ApprovalStatus.Pending,
            Kind: ApprovalKind.ExternalAction,
            SessionId: "session-main",
            Limit: 25);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ApprovalQuery>(json, JsonOptions);

        json.Should().Contain("\"status\":\"Pending\"");
        json.Should().Contain("\"kind\":\"ExternalAction\"");
        json.Should().Contain("\"sessionId\":\"session-main\"");
        json.Should().Contain("\"limit\":25");
        roundTrip.Should().Be(payload);
    }

    [Fact]
    public void Approval_query_response_should_serialize_approvals()
    {
        var payload = new ApprovalQueryResponse(
            new[]
            {
                new Approval(
                    Id: "approval-001",
                    Kind: ApprovalKind.PluginAuthorization,
                    Status: ApprovalStatus.Pending,
                    Title: "Authorize webhook",
                    Summary: "Allow webhook to post updates into the timeline.",
                    Source: "channel.webhook",
                    RequestedAt: new DateTimeOffset(2026, 3, 18, 19, 0, 0, TimeSpan.Zero),
                    UpdatedAt: new DateTimeOffset(2026, 3, 18, 19, 0, 0, TimeSpan.Zero),
                    SessionId: "session-main",
                    CorrelationId: "corr-approval-001",
                    InboxItemId: "inbox-approval-001",
                    PayloadJson: "{\"callId\":\"call-approval-001\"}")
            });

        var json = JsonSerializer.Serialize(payload, JsonOptions);

        json.Should().Contain("\"items\"");
        json.Should().Contain("\"id\":\"approval-001\"");
        json.Should().Contain("\"kind\":\"PluginAuthorization\"");
        json.Should().Contain("\"status\":\"Pending\"");
        json.Should().Contain("\"source\":\"channel.webhook\"");
    }

    [Fact]
    public void Approval_decision_request_should_round_trip()
    {
        var request = new ApprovalDecisionRequest(Note: "approved via API");

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ApprovalDecisionRequest>(json, JsonOptions);

        json.Should().Contain("\"note\":\"approved via API\"");
        roundTrip.Should().Be(request);
    }
}
