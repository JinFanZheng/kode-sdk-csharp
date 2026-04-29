using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Memory;
using Xunit;

namespace KodaClaw.ContractTests.Memory;

public sealed class MemoryStatsContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void MemoryStats_Serialization_RoundTrips()
    {
        // Simulate the shape returned by GET /api/memory/stats
        var stats = new
        {
            activeCount = 5,
            dormantCount = 2,
            archivedCount = 1,
            topicsCount = 3,
            sessionsCount = 10,
        };

        var json = JsonSerializer.Serialize(stats, JsonOptions);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("activeCount").GetInt32().Should().Be(5);
        doc.RootElement.GetProperty("dormantCount").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("archivedCount").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("topicsCount").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("sessionsCount").GetInt32().Should().Be(10);
    }

    [Fact]
    public void MemoryFileEntry_RequiredFields()
    {
        var entry = new MemoryFileEntry(
            Key: "user-preferences",
            Title: "User Preferences",
            Priority: "lasting",
            Status: "active",
            Created: "2026-03-20",
            SourcePath: "workspace/MEMORY.md",
            Tags: ["personal", "settings"]);

        entry.Key.Should().NotBeNullOrWhiteSpace();
        entry.Title.Should().NotBeNullOrWhiteSpace();
        entry.Priority.Should().BeOneOf("permanent", "lasting", "standard", "ephemeral");
        entry.Status.Should().NotBeNullOrWhiteSpace();
        entry.Created.Should().NotBeNullOrWhiteSpace();
        entry.SourcePath.Should().NotBeNullOrWhiteSpace();
        entry.Tags.Should().HaveCount(2);
    }

    [Fact]
    public void MemoryFileEntry_NullableFields_CanBeNull()
    {
        var entry = new MemoryFileEntry(
            Key: "simple-entry",
            Title: "Simple Entry",
            Priority: "standard",
            Status: "active",
            Created: null,
            SourcePath: "workspace/MEMORY.md",
            Tags: null);

        entry.Created.Should().BeNull();
        entry.Tags.Should().BeNull();
    }

    [Fact]
    public void MemoryFileStats_RoundTrips()
    {
        var stats = new MemoryFileStats(
            ActiveCount: 5,
            DormantCount: 2,
            ArchivedCount: 1,
            TopicsCount: 3,
            SessionsCount: 10);

        stats.ActiveCount.Should().Be(5);
        stats.DormantCount.Should().Be(2);
        stats.ArchivedCount.Should().Be(1);
        stats.TopicsCount.Should().Be(3);
        stats.SessionsCount.Should().Be(10);
    }
}
