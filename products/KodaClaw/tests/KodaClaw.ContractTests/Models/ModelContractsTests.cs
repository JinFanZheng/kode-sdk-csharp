using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using Xunit;

namespace KodaClaw.ContractTests.Models;

public sealed class ModelContractsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Create_provider_account_request_should_json_round_trip()
    {
        var payload = new CreateProviderAccountRequest(
            DisplayName: "OpenAI Core",
            ProviderKind: ModelProviderKind.OpenAI,
            BaseUrl: "https://api.openai.com/v1",
            ApiKeyValue: "sk-secret",
            ApiKeyEnvironmentVariable: "OPENAI_API_KEY",
            AccessMode: null,
            CustomHeaders: null,
            Models: new[]
            {
                new CreateAccountModelRequest(
                    DisplayName: "Base playground",
                    ModelId: "gpt-4o-mini",
                    Capabilities: ModelCapabilitySet.Text),
            });

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<CreateProviderAccountRequest>(json, JsonOptions);

        json.Should().Contain("\"displayName\":\"OpenAI Core\"");
        json.Should().Contain("\"providerKind\":\"OpenAI\"");
        json.Should().Contain("\"baseUrl\":\"https://api.openai.com/v1\"");
        json.Should().Contain("\"apiKeyEnvironmentVariable\":\"OPENAI_API_KEY\"");
        json.Should().Contain("\"modelId\":\"gpt-4o-mini\"");
        roundTrip.Should().NotBeNull();
        roundTrip!.Models.Should().HaveCount(1);
        roundTrip.Models[0].ModelId.Should().Be("gpt-4o-mini");
    }

    [Fact]
    public void Update_provider_account_request_should_json_round_trip()
    {
        var payload = new UpdateProviderAccountRequest(
            DisplayName: "Claude Proxy",
            BaseUrl: "https://api.anthropic.com/v1",
            ApiKeyValue: "sk-new",
            Enabled: true);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<UpdateProviderAccountRequest>(json, JsonOptions);

        json.Should().Contain("\"displayName\":\"Claude Proxy\"");
        json.Should().Contain("\"baseUrl\":\"https://api.anthropic.com/v1\"");
        json.Should().Contain("\"enabled\":true");
        roundTrip.Should().Be(payload);
    }

    [Fact]
    public void Update_account_model_request_should_json_round_trip()
    {
        var payload = new UpdateAccountModelRequest(
            DisplayName: "Claude 3 Opus",
            ModelId: "claude-3-opus",
            Capabilities: ModelCapabilitySet.Text);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<UpdateAccountModelRequest>(json, JsonOptions);

        json.Should().Contain("\"displayName\":\"Claude 3 Opus\"");
        json.Should().Contain("\"modelId\":\"claude-3-opus\"");
        json.Should().Contain("\"capabilities\":");
        roundTrip.Should().Be(payload);
    }

    // ── CustomHeaders ─────────────────────────────────────────────────────────

    [Fact]
    public void ProviderAccountResponse_without_custom_headers_round_trips_without_issues()
    {
        var json = """
            {
              "id": "acc-001",
              "displayName": "Old Endpoint",
              "providerKind": "Anthropic",
              "baseUrl": null,
              "accessMode": null,
              "enabled": true,
              "hasApiKey": true,
              "createdAt": "2025-01-01T00:00:00Z",
              "updatedAt": "2025-01-01T00:00:00Z",
              "models": []
            }
            """;

        var account = JsonSerializer.Deserialize<ProviderAccountResponse>(json, JsonOptions);

        account.Should().NotBeNull();
        account!.ProviderKind.Should().Be(ModelProviderKind.Anthropic);
        account.Models.Should().BeEmpty();
    }

    [Fact]
    public void CreateProviderAccountRequest_with_custom_headers_should_round_trip()
    {
        var headers = new Dictionary<string, string> { ["User-Agent"] = "claude-code/0.1.0" };
        var request = new CreateProviderAccountRequest(
            DisplayName: "Spoofed Agent",
            ProviderKind: ModelProviderKind.AnthropicCompatible,
            BaseUrl: "https://proxy.example.com",
            ApiKeyValue: null,
            ApiKeyEnvironmentVariable: null,
            AccessMode: null,
            CustomHeaders: headers,
            Models: new[]
            {
                new CreateAccountModelRequest(
                    DisplayName: "Spoofed Agent",
                    ModelId: "claude-sonnet-4-6"),
            });

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<CreateProviderAccountRequest>(json, JsonOptions);

        json.Should().Contain("customHeaders");
        json.Should().Contain("User-Agent");
        json.Should().Contain("claude-code/0.1.0");
        roundTrip.Should().NotBeNull();
        roundTrip!.CustomHeaders.Should().ContainKey("User-Agent")
            .WhoseValue.Should().Be("claude-code/0.1.0");
    }

    [Fact]
    public void UpdateProviderAccountRequest_without_custom_headers_should_round_trip_as_null()
    {
        var request = new UpdateProviderAccountRequest(
            DisplayName: "Plain endpoint");

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<UpdateProviderAccountRequest>(json, JsonOptions);

        roundTrip.Should().NotBeNull();
        roundTrip!.CustomHeaders.Should().BeNull();
    }

    [Fact]
    public void ProviderAccountResponse_payload_should_contain_models_array()
    {
        var account = new ProviderAccountResponse(
            Id: "acc-001",
            DisplayName: "Multi-model",
            ProviderKind: ModelProviderKind.OpenAICompatible,
            BaseUrl: "https://proxy.example.com",
            AccessMode: null,
            Enabled: true,
            HasApiKey: true,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Models: new[]
            {
                new AccountModelResponse(
                    Id: "model-001",
                    AccountId: "acc-001",
                    DisplayName: "Proxy primary",
                    ModelId: "o3.1",
                    Capabilities: ModelCapabilitySet.Text,
                    IsDefaultForAccount: true,
                    IsGlobalDefault: true,
                    Enabled: true,
                    ContextWindowSize: 128_000,
                    MaxOutputTokens: 8192,
                    IsReasoning: false,
                    SupportsToolCalling: true,
                    Pricing: null),
            });

        var json = JsonSerializer.Serialize(account, JsonOptions);

        json.Should().Contain("\"models\"");
        json.Should().Contain("\"id\":\"model-001\"");
        json.Should().Contain("\"providerKind\":\"OpenAICompatible\"");
        json.Should().Contain("\"isGlobalDefault\":true");
    }
}
