using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Timers;
using Xunit;

namespace KodaClaw.ContractTests.Workspace;

/// <summary>
/// L3 contract tests: OneShotTimerRecord serialization and OneShotTimerStatus string enum format.
/// </summary>
public sealed class OneShotTimerContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void OneShotTimerStatus_serializes_as_string()
    {
        var json = JsonSerializer.Serialize(OneShotTimerStatus.Pending, JsonOptions);
        json.Should().Be("\"Pending\"");

        json = JsonSerializer.Serialize(OneShotTimerStatus.Fired, JsonOptions);
        json.Should().Be("\"Fired\"");

        json = JsonSerializer.Serialize(OneShotTimerStatus.Cancelled, JsonOptions);
        json.Should().Be("\"Cancelled\"");
    }

    [Fact]
    public void OneShotTimerStatus_deserializes_from_string()
    {
        JsonSerializer.Deserialize<OneShotTimerStatus>("\"Pending\"", JsonOptions)
            .Should().Be(OneShotTimerStatus.Pending);
        JsonSerializer.Deserialize<OneShotTimerStatus>("\"Fired\"", JsonOptions)
            .Should().Be(OneShotTimerStatus.Fired);
        JsonSerializer.Deserialize<OneShotTimerStatus>("\"Cancelled\"", JsonOptions)
            .Should().Be(OneShotTimerStatus.Cancelled);
    }

    [Fact]
    public void OneShotTimerRecord_round_trips_with_all_fields()
    {
        var now = new DateTimeOffset(2026, 3, 28, 10, 0, 0, TimeSpan.Zero);
        var record = new OneShotTimerRecord(
            Id: "timer-abc",
            Title: "My Reminder",
            Prompt: "Send daily summary.",
            FireAt: now.AddHours(2),
            Status: OneShotTimerStatus.Pending,
            Channels: ["tg-12345", "fs-67890"],
            CreatedAt: now,
            FiredAt: null,
            ErrorMessage: null);

        var json = JsonSerializer.Serialize(record, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<OneShotTimerRecord>(json, JsonOptions)!;

        deserialized.Id.Should().Be(record.Id);
        deserialized.Title.Should().Be(record.Title);
        deserialized.Prompt.Should().Be(record.Prompt);
        deserialized.FireAt.Should().Be(record.FireAt);
        deserialized.Status.Should().Be(OneShotTimerStatus.Pending);
        deserialized.Channels.Should().BeEquivalentTo(record.Channels);
        deserialized.FiredAt.Should().BeNull();
        deserialized.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void OneShotTimerRecord_round_trips_fired_state()
    {
        var now = new DateTimeOffset(2026, 3, 28, 10, 0, 0, TimeSpan.Zero);
        var record = new OneShotTimerRecord(
            Id: "timer-xyz",
            Title: null,
            Prompt: "Check server health.",
            FireAt: now,
            Status: OneShotTimerStatus.Fired,
            Channels: null,
            CreatedAt: now.AddHours(-1),
            FiredAt: now,
            ErrorMessage: null);

        var json = JsonSerializer.Serialize(record, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<OneShotTimerRecord>(json, JsonOptions)!;

        deserialized.Status.Should().Be(OneShotTimerStatus.Fired);
        deserialized.FiredAt.Should().Be(now);
        deserialized.Title.Should().BeNull();
        deserialized.Channels.Should().BeNullOrEmpty();
    }

    [Fact]
    public void AutomationDefinitionSource_includes_OneShot_value()
    {
        var json = JsonSerializer.Serialize(AutomationDefinitionSource.OneShot, JsonOptions);
        json.Should().Be("\"OneShot\"");

        JsonSerializer.Deserialize<AutomationDefinitionSource>("\"OneShot\"", JsonOptions)
            .Should().Be(AutomationDefinitionSource.OneShot);
    }
}
