using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Plugins;
using KodaClaw.PluginHost.Manifest;
using Xunit;

namespace KodaClaw.ContractTests.Plugins;

public sealed class PluginManifestContractsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Plugin_manifest_should_json_round_trip()
    {
        var payload = BuildValidManifest();

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);

        json.Should().Contain("\"id\":\"telegram-bridge\"");
        json.Should().Contain("\"transport\":\"Stdio\"");
        json.Should().Contain("\"configSchema\"");
        json.Should().Contain("\"environmentReferences\"");
        json.Should().Contain("\"headerReferences\"");
        roundTrip.Should().NotBeNull();
        roundTrip!.Id.Should().Be(payload.Id);
        roundTrip.ConfigSchema.Should().NotBeNull();
        roundTrip.ConfigSchema!.Value.TryGetProperty("type", out var typeNode).Should().BeTrue();
        typeNode.GetString().Should().Be("object");
        roundTrip.Runtime.EnvironmentReferences.Should().ContainKey("TELEGRAM_BOT_TOKEN");
        roundTrip.Runtime.HeaderReferences.Should().ContainKey("Authorization");
    }

    [Fact]
    public void Stdio_manifest_should_be_valid_after_normalization()
    {
        var manifest = BuildValidManifest() with
        {
            Id = " Telegram-Bridge ",
            Runtime = BuildValidManifest().Runtime with
            {
                Command = " python3 ",
                Args = [" main.py ", "  "],
            },
        };

        var normalized = PluginManifestNormalizer.Normalize(manifest);
        var result = PluginManifestValidator.Validate(normalized);

        result.IsValid.Should().BeTrue();
        normalized.Id.Should().Be("telegram-bridge");
        normalized.Runtime.Command.Should().Be("python3");
        normalized.Runtime.Args.Should().BeEquivalentTo(["main.py"]);
    }

    [Fact]
    public void Validator_should_fail_when_required_fields_are_missing()
    {
        var manifest = BuildValidManifest() with
        {
            Id = " ",
            Name = " ",
            Version = "",
        };

        var result = PluginManifestValidator.Validate(manifest);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Contains("id is required.", StringComparison.Ordinal));
        result.Errors.Should().Contain(error => error.Contains("name is required.", StringComparison.Ordinal));
        result.Errors.Should().Contain(error => error.Contains("version is required.", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_should_fail_when_id_is_invalid()
    {
        var manifest = BuildValidManifest() with { Id = "Invalid Id" };

        var result = PluginManifestValidator.Validate(manifest);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Contains("id is invalid", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_should_fail_when_tool_plugin_has_empty_tools_capability()
    {
        var manifest = BuildValidManifest() with
        {
            Capabilities = new PluginCapabilitySet(Tools: []),
        };

        var result = PluginManifestValidator.Validate(manifest);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error =>
            error.Contains("capabilities.tools must not be empty", StringComparison.Ordinal));
    }

    [Fact]
    public void Loader_should_throw_validation_exception_for_invalid_manifest()
    {
        const string json =
            """
            {
              "id": "Invalid Id",
              "name": "X",
              "version": "0.1.0",
              "types": ["Tool"],
              "runtime": { "transport": "Stdio", "command": "python3" },
              "permissions": {},
              "capabilities": { "tools": [] }
            }
            """;

        var loader = new PluginManifestLoader();
        var action = () => loader.ParseAndValidate(json);

        action.Should().Throw<PluginManifestValidationException>()
            .Where(ex => ex.Errors.Count > 0);
    }

    private static PluginManifest BuildValidManifest()
    {
        var configSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                botToken = new { type = "string" },
            },
            required = new[] { "botToken" },
        });

        return new PluginManifest(
            Id: "telegram-bridge",
            Name: "Telegram Bridge",
            Version: "0.1.0",
            Types: [PluginType.Tool, PluginType.Channel],
            Runtime: new PluginRuntimeSpec(
                Transport: PluginTransportKind.Stdio,
                Command: "python3",
                Args: ["main.py"],
                EnvironmentReferences: new Dictionary<string, string>
                {
                    ["TELEGRAM_BOT_TOKEN"] = "keychain:plugins:telegram-bot"
                },
                HeaderReferences: new Dictionary<string, string>
                {
                    ["Authorization"] = "env:TELEGRAM_PLUGIN_AUTH_HEADER"
                }),
            Permissions: new PluginPermissionSet(
                Filesystem: ["workspace/channels/telegram"],
                Network: true,
                Notifications: true,
                Background: true,
                Secrets: ["telegram.botToken"]),
            Capabilities: new PluginCapabilitySet(
                Tools: ["send_message", "list_updates"],
                Channels: ["telegram"],
                UiPanels: ["telegram-settings"]),
            ConfigSchema: configSchema);
    }
}
