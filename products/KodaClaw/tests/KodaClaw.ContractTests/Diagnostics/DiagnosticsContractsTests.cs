using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using Xunit;

namespace KodaClaw.ContractTests.Diagnostics;

public sealed class DiagnosticsContractsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Diagnostics_query_should_round_trip_json()
    {
        var payload = new DiagnosticsQuery(
            Limit: 20,
            CorrelationId: "corr-123",
            SessionId: "session-main",
            Source: "gateway.chat",
            EventType: "gateway.chat.failed",
            Levels: ["error"]);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<DiagnosticsQuery>(json, JsonOptions);

        json.Should().Contain("\"limit\":20");
        json.Should().Contain("\"correlationId\":\"corr-123\"");
        json.Should().Contain("\"sessionId\":\"session-main\"");
        json.Should().Contain("\"source\":\"gateway.chat\"");
        json.Should().Contain("\"eventType\":\"gateway.chat.failed\"");
        json.Should().Contain("\"level\":\"error\"");
        roundTrip.Should().BeEquivalentTo(payload);
    }

    [Fact]
    public void Diagnostics_query_response_should_serialize_events()
    {
        var payload = new DiagnosticsQueryResponse(
            [
                new DiagnosticEvent(
                    Id: "diag-001",
                    Source: "gateway.chat",
                    EventType: "gateway.chat.requested",
                    Level: "info",
                    Message: "accepted",
                    Timestamp: new DateTimeOffset(2026, 3, 18, 18, 0, 0, TimeSpan.Zero),
                    CorrelationId: "corr-001",
                    SessionId: "session-main")
            ]);

        var json = JsonSerializer.Serialize(payload, JsonOptions);

        json.Should().Contain("\"events\"");
        json.Should().Contain("\"source\":\"gateway.chat\"");
        json.Should().Contain("\"eventType\":\"gateway.chat.requested\"");
    }
}
