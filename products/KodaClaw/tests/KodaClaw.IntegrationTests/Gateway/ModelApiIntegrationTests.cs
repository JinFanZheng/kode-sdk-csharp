using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Models;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class ModelApiIntegrationTests
{
    private const string GatewayToken = "test-token";

    [Fact]
    public async Task Models_list_should_require_token()
    {
        await using var hosted = await HostedGateway.StartAsync(
            gatewayToken: GatewayToken,
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(requiresBootstrap: false));

        var response = await hosted.Client.GetAsync("/api/models");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Models_list_should_return_created_models_via_compat_endpoint()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var created = await CreateProviderAccountAsync(hosted.Client, "Model 001");
        var createdModelId = created.Models[0].Id;

        var response = await hosted.Client.GetAsync("/api/models");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<List<AccountModelResponse>>();
        payload.Should().NotBeNull();
        payload!.Should().Contain(item => item.Id == createdModelId);
    }

    [Fact]
    public async Task Provider_account_detail_should_return_not_found_for_missing()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var response = await hosted.Client.GetAsync("/api/provider-accounts/missing");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_provider_account_should_persist()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var request = new CreateProviderAccountRequest(
            DisplayName: "New account",
            ProviderKind: ModelProviderKind.OpenAICompatible,
            BaseUrl: "https://proxy.test",
            ApiKeyValue: "sk-secret",
            ApiKeyEnvironmentVariable: null,
            AccessMode: null,
            CustomHeaders: null,
            Models: new[]
            {
                new CreateAccountModelRequest(
                    DisplayName: "New endpoint",
                    ModelId: "o3",
                    Capabilities: ModelCapabilitySet.Text),
            });

        var response = await hosted.Client.PostAsJsonAsync("/api/provider-accounts", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var payload = await response.Content.ReadFromJsonAsync<ProviderAccountResponse>();
        payload.Should().NotBeNull();
        payload!.DisplayName.Should().Be("New account");
        payload.HasApiKey.Should().BeTrue();
        payload.Models.Should().HaveCount(1);
        payload.Models[0].ModelId.Should().Be("o3");
    }

    [Fact]
    public async Task Update_provider_account_should_change_fields()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);
        var created = await CreateProviderAccountAsync(hosted.Client, "Before update");

        var request = new UpdateProviderAccountRequest(
            DisplayName: "Updated name",
            BaseUrl: "https://anthropic.proxy.test");

        var response = await hosted.Client.PutAsJsonAsync($"/api/provider-accounts/{created.Id}", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<ProviderAccountResponse>();
        payload.Should().NotBeNull();
        payload!.DisplayName.Should().Be("Updated name");
        payload.BaseUrl.Should().Be("https://anthropic.proxy.test");
    }

    [Fact]
    public async Task Delete_provider_account_should_remove_resource()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var created = await CreateProviderAccountAsync(hosted.Client, "Delete candidate");

        var response = await hosted.Client.DeleteAsync($"/api/provider-accounts/{created.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var detail = await hosted.Client.GetAsync($"/api/provider-accounts/{created.Id}");
        detail.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Set_default_model_should_mark_model_as_global_default()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var first = await CreateProviderAccountAsync(hosted.Client, "Default first");
        var second = await CreateProviderAccountAsync(hosted.Client, "Default second");
        var secondModelId = second.Models[0].Id;

        var response = await hosted.Client.PostAsync(
            $"/api/provider-accounts/{second.Id}/models/{secondModelId}/default", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var listResponse = await hosted.Client.GetAsync("/api/models");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var listPayload = await listResponse.Content.ReadFromJsonAsync<List<AccountModelResponse>>();
        listPayload.Should().NotBeNull();
        listPayload!.Single(item => item.Id == secondModelId).IsGlobalDefault.Should().BeTrue();
        listPayload.Single(item => item.Id == first.Models[0].Id).IsGlobalDefault.Should().BeFalse();
    }

    [Fact]
    public async Task Invalid_create_request_should_return_bad_request()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var request = new CreateProviderAccountRequest(
            DisplayName: "",
            ProviderKind: ModelProviderKind.OpenAI,
            BaseUrl: null,
            ApiKeyValue: null,
            ApiKeyEnvironmentVariable: null,
            AccessMode: null,
            CustomHeaders: null,
            Models: Array.Empty<CreateAccountModelRequest>());

        var response = await hosted.Client.PostAsJsonAsync("/api/provider-accounts", request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── CustomHeaders: POST 持久化 ─────────────────────────────────────────────

    [Fact]
    public async Task Create_provider_account_with_custom_headers_should_persist_them()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var headers = new Dictionary<string, string> { ["User-Agent"] = "claude-code/0.1.0" };
        var request = new CreateProviderAccountRequest(
            DisplayName: "Custom Headers Account",
            ProviderKind: ModelProviderKind.OpenAICompatible,
            BaseUrl: "https://proxy.test",
            ApiKeyValue: "sk-secret",
            ApiKeyEnvironmentVariable: null,
            AccessMode: null,
            CustomHeaders: headers,
            Models: new[]
            {
                new CreateAccountModelRequest(
                    DisplayName: "Custom Headers Account",
                    ModelId: "o3"),
            });

        var response = await hosted.Client.PostAsJsonAsync("/api/provider-accounts", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var payload = await response.Content.ReadFromJsonAsync<ProviderAccountResponse>();
        payload.Should().NotBeNull();
        payload!.HasApiKey.Should().BeTrue();
    }

    [Fact]
    public async Task Update_provider_account_with_custom_headers_should_persist_them()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var created = await CreateProviderAccountAsync(hosted.Client, "Before update");

        var headers = new Dictionary<string, string> { ["User-Agent"] = "my-agent/2.0" };
        var request = new UpdateProviderAccountRequest(
            DisplayName: "Updated with headers",
            CustomHeaders: headers);

        var response = await hosted.Client.PutAsJsonAsync($"/api/provider-accounts/{created.Id}", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<ProviderAccountResponse>();
        payload.Should().NotBeNull();
        payload!.DisplayName.Should().Be("Updated with headers");
    }

    // ── Connection test: AnthropicCompatible (real MiMo endpoint) ─────────

    [Fact]
    public async Task Test_connection_anthropic_compatible_should_return_ok()
    {
        var apiKey = Environment.GetEnvironmentVariable("KODACLAW_TEST_XIAOMI_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
            return; // Skip when no API key is configured

        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var request = new ModelConnectionTestRequest(
            PresetId: null,
            ModelId: "mimo-v2.5",
            BaseUrl: "https://token-plan-cn.xiaomimimo.com/anthropic",
            ApiKey: apiKey,
            Provider: "AnthropicCompatible",
            EndpointId: null);

        var response = await hosted.Client.PostAsJsonAsync("/api/models/test-connection", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<ModelConnectionTestResponse>();
        payload.Should().NotBeNull();
        payload!.Ok.Should().BeTrue($"expected success but got error: {payload.Error} - {payload.ErrorMessage}");
        payload.ModelId.Should().Be("mimo-v2.5");
        payload.LatencyMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Test_connection_invalid_api_key_should_return_authentication_error()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var request = new ModelConnectionTestRequest(
            PresetId: null,
            ModelId: "mimo-v2.5",
            BaseUrl: "https://token-plan-cn.xiaomimimo.com/anthropic",
            ApiKey: "sk-invalid-key",
            Provider: "AnthropicCompatible",
            EndpointId: null);

        var response = await hosted.Client.PostAsJsonAsync("/api/models/test-connection", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<ModelConnectionTestResponse>();
        payload.Should().NotBeNull();
        payload!.Ok.Should().BeFalse();
        payload.Error.Should().Be("authentication_error");
    }

    private static Task<HostedGateway> StartGatewayAsync(
        string workspaceRoot)
    {
        return HostedGateway.StartAsync(
            gatewayToken: GatewayToken,
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false,
                rootPath: workspaceRoot),
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                });
            },
            useTestWorkspaceService: false);
    }

    private static async Task<ProviderAccountResponse> CreateProviderAccountAsync(HttpClient client, string displayName)
    {
        var request = new CreateProviderAccountRequest(
            DisplayName: displayName,
            ProviderKind: ModelProviderKind.OpenAICompatible,
            BaseUrl: "https://proxy",
            ApiKeyValue: "sk-test",
            ApiKeyEnvironmentVariable: null,
            AccessMode: null,
            CustomHeaders: null,
            Models: new[]
            {
                new CreateAccountModelRequest(
                    DisplayName: displayName,
                    ModelId: "o3"),
            });

        var response = await client.PostAsJsonAsync("/api/provider-accounts", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var payload = await response.Content.ReadFromJsonAsync<ProviderAccountResponse>();
        payload.Should().NotBeNull();
        payload!.Id.Should().NotBeNullOrWhiteSpace();
        payload.Models.Should().NotBeEmpty();
        return payload;
    }

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-model-api",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
