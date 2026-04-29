using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using Xunit;

namespace KodaClaw.ContractTests.Automation;

public sealed class AutomationApiContractsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Automation_definitions_query_response_should_json_round_trip()
    {
        var payload = new AutomationDefinitionsQueryResponse([CreateDefinition(enabled: true)]);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<AutomationDefinitionsQueryResponse>(json, JsonOptions);

        json.Should().Contain("\"items\"");
        json.Should().Contain("\"enabled\":true");
        roundTrip.Should().BeEquivalentTo(payload);
    }

    [Fact]
    public void Automation_runs_query_response_should_json_round_trip()
    {
        var payload = new AutomationRunsQueryResponse(
        [
            new AutomationRunRecord(
                RunId: "run-001",
                AutomationId: "auto-heartbeat",
                Status: AutomationRunStatus.Succeeded,
                Trigger: "heartbeat",
                Attempt: 1,
                SessionId: "auto-session-001",
                StartedAt: new DateTimeOffset(2026, 3, 18, 9, 0, 0, TimeSpan.Zero),
                CompletedAt: new DateTimeOffset(2026, 3, 18, 9, 1, 0, TimeSpan.Zero),
                Summary: "Queue digest posted.",
                ErrorMessage: null),
        ]);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<AutomationRunsQueryResponse>(json, JsonOptions);

        json.Should().Contain("\"status\":\"Succeeded\"");
        roundTrip.Should().BeEquivalentTo(payload);
    }

    [Fact]
    public void Update_automation_definition_request_should_json_round_trip()
    {
        var payload = new UpdateAutomationDefinitionRequest(Enabled: false);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<UpdateAutomationDefinitionRequest>(json, JsonOptions);

        json.Should().Contain("\"enabled\":false");
        roundTrip.Should().Be(payload);
    }

    private static AutomationDefinition CreateDefinition(bool enabled)
    {
        return new AutomationDefinition(
            Id: "auto-heartbeat",
            Title: "Heartbeat Digest",
            Prompt: "Review unresolved inbox items and summarize the queue.",
            Source: AutomationDefinitionSource.Heartbeat,
            SourcePath: "workspace/HEARTBEAT.md",
            CronExpression: "0 9 * * 1,3,5",
            Enabled: enabled,
            InputPaths: ["workspace/inbox", "workspace/tasks"],
            ModelId: null,
            NotificationChannels: null,
            NotifyMode: AutomationNotifyMode.None,
            CreatedAt: new DateTimeOffset(2026, 3, 18, 8, 0, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 3, 18, 8, 0, 0, TimeSpan.Zero),
            LastRunAt: new DateTimeOffset(2026, 3, 18, 9, 0, 0, TimeSpan.Zero),
            NextRunAt: new DateTimeOffset(2026, 3, 19, 9, 0, 0, TimeSpan.Zero),
            LastRunStatus: AutomationRunStatus.Succeeded,
            LastError: null);
    }
}
