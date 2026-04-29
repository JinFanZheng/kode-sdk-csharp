using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Sessions;
using Xunit;

namespace KodaClaw.ContractTests.Channels;

/// <summary>
/// KC-BUG-W5: ThreadBinding JSON 序列化/反序列化契约测试。
/// 验证新增字段（PendingModelOverride / ActiveModelId）的向后兼容性。
/// </summary>
public sealed class ThreadBindingContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static ThreadBinding BuildBinding(
        string? pendingModelOverride = null,
        string? activeModelId = null) => new ThreadBinding(
        Id: "binding-001",
        ConnectorKind: ChannelConnectorKind.Telegram,
        AccountId: "acc-001",
        ExternalThreadId: "tg-thread-1",
        ThreadType: ChannelThreadType.DirectMessage,
        SessionId: "session-abc",
        SessionKind: SessionKind.ChannelDirectMessage,
        ChannelIdentity: new ChannelIdentity(Id: "user-001", DisplayName: "Test User"),
        PolicyId: "policy-001",
        DeliveryRuleId: "rule-001",
        CreatedAt: new DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero),
        UpdatedAt: new DateTimeOffset(2026, 4, 10, 0, 1, 0, TimeSpan.Zero),
        PendingModelOverride: pendingModelOverride,
        ActiveModelId: activeModelId);

    [Fact]
    public void ThreadBinding_with_new_fields_should_round_trip()
    {
        var binding = BuildBinding(
            pendingModelOverride: "kimi-k2.5",
            activeModelId: "claude-sonnet-4-6");

        var json = JsonSerializer.Serialize(binding, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ThreadBinding>(json, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.PendingModelOverride.Should().Be("kimi-k2.5");
        deserialized.ActiveModelId.Should().Be("claude-sonnet-4-6");
    }

    [Fact]
    public void ThreadBinding_new_fields_default_to_null()
    {
        var binding = BuildBinding();

        binding.PendingModelOverride.Should().BeNull();
        binding.ActiveModelId.Should().BeNull();
    }

    [Fact]
    public void Old_json_without_new_fields_deserializes_with_null_values()
    {
        // JSON 不含 PendingModelOverride / ActiveModelId（模拟重启前的旧 JSON 文件）
        const string oldJson = """
            {
                "id": "binding-001",
                "connectorKind": "Telegram",
                "accountId": "acc-001",
                "externalThreadId": "tg-thread-1",
                "threadType": "DirectMessage",
                "sessionId": "session-abc",
                "sessionKind": "ChannelDirectMessage",
                "channelIdentity": { "id": "user-001", "displayName": "Test User" },
                "policyId": "policy-001",
                "deliveryRuleId": "rule-001",
                "createdAt": "2026-04-10T00:00:00+00:00",
                "updatedAt": "2026-04-10T00:01:00+00:00"
            }
            """;

        var deserialized = JsonSerializer.Deserialize<ThreadBinding>(oldJson, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.PendingModelOverride.Should().BeNull("旧 JSON 文件缺失字段应反序列化为 null");
        deserialized.ActiveModelId.Should().BeNull("旧 JSON 文件缺失字段应反序列化为 null");
    }

    [Fact]
    public void ThreadBinding_with_null_new_fields_serializes_and_omits_nulls()
    {
        var binding = BuildBinding();

        var json = JsonSerializer.Serialize(binding, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<ThreadBinding>(json, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.PendingModelOverride.Should().BeNull();
        deserialized.ActiveModelId.Should().BeNull();
    }
}
