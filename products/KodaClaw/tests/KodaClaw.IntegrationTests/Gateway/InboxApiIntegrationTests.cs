using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Inbox;
using KodaClaw.Contracts.System;
using KodaClaw.ControlPlane;
using KodaClaw.Storage.Json;
using KodaClaw.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class InboxApiIntegrationTests
{
    [Fact]
    public async Task Inbox_list_should_require_token()
    {
        using var workspace = new TempWorkspaceRoot();
        await SeedInboxItemAsync(workspace.Path, CreateOpenApprovalItem());
        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);

        var response = await hosted.Client.GetAsync("/api/inbox");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Inbox_list_should_return_filtered_items()
    {
        using var workspace = new TempWorkspaceRoot();
        await SeedInboxItemAsync(workspace.Path, CreateOpenApprovalItem());
        await SeedInboxItemAsync(workspace.Path, new InboxItem(
            Id: "inbox-info-001",
            Kind: InboxItemKind.Information,
            Status: InboxItemStatus.Open,
            Title: "Webhook connected",
            Summary: "Binding finished.",
            Source: "channel.webhook",
            CreatedAt: new DateTimeOffset(2026, 3, 18, 18, 1, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 3, 18, 18, 1, 0, TimeSpan.Zero),
            RequiresAction: false,
            SessionId: "session-other"));
        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);

        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync(
            "/api/inbox?status=Open&kind=Approval&requiresAction=true&sessionId=session-main&limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<InboxQueryResponse>();
        payload.Should().NotBeNull();
        payload!.Items.Select(item => item.Id).Should().Equal("inbox-approval-001");
    }

    [Fact]
    public async Task Inbox_detail_should_return_not_found_when_missing()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);

        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/inbox/missing-id");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        payload.Should().NotBeNull();
        payload!.Code.Should().Be("inbox.not_found");
    }

    [Fact]
    public async Task Inbox_status_patch_should_update_item_and_return_reloaded_payload()
    {
        using var workspace = new TempWorkspaceRoot();
        await SeedInboxItemAsync(workspace.Path, CreateOpenApprovalItem());
        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);

        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.PatchAsJsonAsync(
            "/api/inbox/inbox-approval-001/status",
            new InboxStatusUpdateRequest("Resolved"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<InboxItem>();
        payload.Should().NotBeNull();
        payload!.Status.Should().Be(InboxItemStatus.Resolved);
        payload.ResolvedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Inbox_list_should_reject_invalid_kind_filter()
    {
        using var workspace = new TempWorkspaceRoot();
        await SeedInboxItemAsync(workspace.Path, CreateOpenApprovalItem());
        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);

        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/inbox?kind=unknown");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        payload.Should().NotBeNull();
        payload!.Code.Should().Be("validation.inbox_kind_invalid");
    }

    private static InboxItem CreateOpenApprovalItem()
    {
        return new InboxItem(
            Id: "inbox-approval-001",
            Kind: InboxItemKind.Approval,
            Status: InboxItemStatus.Open,
            Title: "Approve outbound reply",
            Summary: "A channel reply is waiting for approval.",
            Source: "channel.telegram",
            CreatedAt: new DateTimeOffset(2026, 3, 18, 18, 0, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 3, 18, 18, 0, 0, TimeSpan.Zero),
            RequiresAction: true,
            SessionId: "session-main",
            CorrelationId: "corr-inbox-001",
            ApprovalId: "approval-001");
    }

    private static async Task SeedInboxItemAsync(string workspaceRoot, InboxItem item)
    {
        var services = new ServiceCollection();
        services.AddKodaClawWorkspace(options => options.RootPath = workspaceRoot);
        services.AddKodaClawJsonStore(workspaceRoot);
        services.AddKodaClawControlPlane();
        using var provider = services.BuildServiceProvider();

        var repository = provider.GetRequiredService<IInboxRepository>();
        await repository.UpsertAsync(item);
    }

    private static Task<HostedGateway> StartRealWorkspaceGatewayAsync(string workspaceRoot)
    {
        return HostedGateway.StartAsync(
            gatewayToken: "test-token",
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false,
                rootPath: workspaceRoot),
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot
                });
            },
            useTestWorkspaceService: false);
    }

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-inbox-api",
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
