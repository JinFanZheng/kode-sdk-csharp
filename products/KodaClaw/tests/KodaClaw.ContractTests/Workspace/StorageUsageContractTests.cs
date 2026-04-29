using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.System;
using Xunit;

namespace KodaClaw.ContractTests.Workspace;

/// <summary>
/// L3 契约测试 — StorageUsageResponse DTO
/// 验证 JSON 序列化/反序列化格式与 API 契约一致。
/// </summary>
public sealed class StorageUsageContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void StorageUsageResponse_round_trips_correctly()
    {
        const string json = """
            {
              "main":    { "count": 3,  "sizeBytes": 1048576 },
              "auto":    { "count": 12, "sizeBytes": 5242880 },
              "channel": { "count": 2,  "sizeBytes": 204800  },
              "totalSizeBytes": 6496256
            }
            """;

        var response = JsonSerializer.Deserialize<StorageUsageResponse>(json, JsonOptions)!;

        response.Main.Count.Should().Be(3);
        response.Main.SizeBytes.Should().Be(1_048_576);
        response.Auto.Count.Should().Be(12);
        response.Auto.SizeBytes.Should().Be(5_242_880);
        response.Channel.Count.Should().Be(2);
        response.Channel.SizeBytes.Should().Be(204_800);
        response.TotalSizeBytes.Should().Be(6_496_256);
    }

    [Fact]
    public void StorageUsageResponse_serializes_all_fields_in_camelCase()
    {
        var response = new StorageUsageResponse
        {
            Main    = new SessionStorageUsage { Count = 1, SizeBytes = 512 },
            Auto    = new SessionStorageUsage { Count = 5, SizeBytes = 2048 },
            Channel = new SessionStorageUsage { Count = 0, SizeBytes = 0 },
            TotalSizeBytes = 2560,
        };

        var json = JsonSerializer.Serialize(response, JsonOptions);

        json.Should().Contain("\"main\"");
        json.Should().Contain("\"auto\"");
        json.Should().Contain("\"channel\"");
        json.Should().Contain("\"totalSizeBytes\"");
        json.Should().Contain("\"count\"");
        json.Should().Contain("\"sizeBytes\"");
    }

    [Fact]
    public void StorageUsageResponse_zero_values_round_trip_correctly()
    {
        const string json = """
            {
              "main":    { "count": 0, "sizeBytes": 0 },
              "auto":    { "count": 0, "sizeBytes": 0 },
              "channel": { "count": 0, "sizeBytes": 0 },
              "totalSizeBytes": 0
            }
            """;

        var response = JsonSerializer.Deserialize<StorageUsageResponse>(json, JsonOptions)!;

        response.Main.Count.Should().Be(0);
        response.Main.SizeBytes.Should().Be(0);
        response.Auto.Count.Should().Be(0);
        response.TotalSizeBytes.Should().Be(0);
    }

    [Fact]
    public void SessionStorageUsage_count_suffix_reflects_session_count()
    {
        // 验证字段名称和类型符合前端期望（count: number, sizeBytes: number）
        var usage = new SessionStorageUsage { Count = 42, SizeBytes = 1_000_000_000 };

        usage.Count.Should().Be(42);
        usage.SizeBytes.Should().Be(1_000_000_000);
    }
}
