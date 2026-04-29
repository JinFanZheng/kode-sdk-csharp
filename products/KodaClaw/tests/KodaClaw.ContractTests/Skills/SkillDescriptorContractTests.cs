using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Skills;
using Xunit;

namespace KodaClaw.ContractTests.Skills;

/// <summary>
/// L3 契约测试 — SkillDescriptor JSON 序列化格式。
/// </summary>
public sealed class SkillDescriptorContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void SkillDescriptor_full_round_trips_correctly()
    {
        const string json = """
            {
              "name":         "koda-automation",
              "description":  "HEARTBEAT.md automation guide",
              "source":       "built-in",
              "path":         "/app/skills/koda-automation",
              "hasResources": false,
              "kind":         "builtin-core",
              "tags":         ["automation", "heartbeat", "scheduling"],
              "allowedTools": ["workspace_protocol_update", "workspace_read"],
              "version":      "1.0",
              "compatibility": "KodaClaw 1.x"
            }
            """;

        var dto = JsonSerializer.Deserialize<SkillDescriptor>(json, JsonOptions)!;

        dto.Name.Should().Be("koda-automation");
        dto.Description.Should().Be("HEARTBEAT.md automation guide");
        dto.Source.Should().Be("built-in");
        dto.Path.Should().Be("/app/skills/koda-automation");
        dto.HasResources.Should().BeFalse();
        dto.Kind.Should().Be("builtin-core");
        dto.Tags.Should().Equal("automation", "heartbeat", "scheduling");
        dto.AllowedTools.Should().Equal("workspace_protocol_update", "workspace_read");
        dto.Version.Should().Be("1.0");
        dto.Compatibility.Should().Be("KodaClaw 1.x");
    }

    [Fact]
    public void SkillDescriptor_missing_optional_fields_deserializes_without_throwing()
    {
        const string json = """
            {
              "name":         "my-skill",
              "source":       "workspace",
              "path":         "/workspace/skills/my-skill",
              "hasResources": false,
              "kind":         "optional",
              "tags":         [],
              "allowedTools": []
            }
            """;

        var dto = JsonSerializer.Deserialize<SkillDescriptor>(json, JsonOptions)!;

        dto.Description.Should().BeNull();
        dto.Version.Should().BeNull();
        dto.Compatibility.Should().BeNull();
        dto.Tags.Should().BeEmpty();
        dto.AllowedTools.Should().BeEmpty();
    }

    [Fact]
    public void SkillDescriptor_serialization_produces_camelCase_json()
    {
        var dto = new SkillDescriptor(
            Name: "koda-workspace",
            Description: "Workspace protocol",
            Source: "built-in",
            Path: "/app/skills/koda-workspace",
            HasResources: false,
            Kind: "builtin-core",
            Tags: ["workspace"],
            AllowedTools: ["workspace_read"],
            Version: "1.0",
            Compatibility: "KodaClaw 1.x");

        var json = JsonSerializer.Serialize(dto, JsonOptions);

        json.Should().Contain("\"name\":");
        json.Should().Contain("\"hasResources\":");
        json.Should().Contain("\"kind\":");
        json.Should().Contain("\"tags\":");
        json.Should().Contain("\"allowedTools\":");
        json.Should().Contain("\"version\":");
        json.Should().Contain("\"compatibility\":");
    }

    [Fact]
    public void SkillDescriptor_null_compatibility_omitted_or_null_in_json()
    {
        var dto = new SkillDescriptor(
            Name: "my-skill",
            Description: null,
            Source: "workspace",
            Path: "/skills/my-skill",
            HasResources: false,
            Kind: "optional",
            Tags: [],
            AllowedTools: [],
            Version: null,
            Compatibility: null);

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<SkillDescriptor>(json, JsonOptions)!;

        roundTripped.Compatibility.Should().BeNull();
        roundTripped.Version.Should().BeNull();
    }
}
