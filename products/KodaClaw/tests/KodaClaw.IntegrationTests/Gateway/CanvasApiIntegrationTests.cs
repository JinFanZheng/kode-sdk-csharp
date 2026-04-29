using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Canvas;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Storage.Json;
using KodaClaw.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class CanvasApiIntegrationTests
{
    [Fact]
    public async Task Canvas_list_should_require_token()
    {
        using var workspace = new TempCanvasWorkspaceRoot();
        await SeedCanvasArtifactAsync(workspace.Path, CreateArtifact("canvas-report", CanvasArtifactKind.Report, updatedAtHour: 8));
        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);

        var response = await hosted.Client.GetAsync("/api/canvas");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Canvas_list_should_return_filtered_items_and_latest_default_entry()
    {
        using var workspace = new TempCanvasWorkspaceRoot();
        await SeedCanvasArtifactAsync(workspace.Path, CreateArtifact("canvas-report", CanvasArtifactKind.Report, source: "runtime.main", sessionId: "main-001", updatedAtHour: 8));
        await SeedCanvasArtifactAsync(workspace.Path, CreateArtifact("canvas-dashboard", CanvasArtifactKind.Dashboard, source: "runtime.automation", sessionId: "auto-001", updatedAtHour: 10));
        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);

        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/canvas?kind=Dashboard&source=runtime.automation&sessionId=auto-001&limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<CanvasQueryResponse>();
        payload.Should().NotBeNull();
        payload!.Items.Select(item => item.Id).Should().Equal("canvas-dashboard");
        payload.DefaultArtifactId.Should().Be("canvas-dashboard");
        payload.DefaultEntryPath.Should().Be("workspace/canvas/artifacts/canvas-dashboard/index.html");
    }

    [Fact]
    public async Task Canvas_default_should_fall_back_to_workspace_index_when_no_artifacts_exist()
    {
        using var workspace = new TempCanvasWorkspaceRoot();
        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);

        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/canvas/default");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<CanvasEntryResponse>();
        payload.Should().NotBeNull();
        payload!.EntryPath.Should().Be("workspace/canvas/index.html");
        payload.EntryUrl.Should().StartWith("/api/canvas/preview/");
        payload.EntryUrl.Should().EndWith("/workspace/canvas/index.html");
        payload.ArtifactId.Should().BeNull();
    }

    [Fact]
    public async Task Canvas_artifact_entry_should_issue_preview_url_that_serves_html_and_relative_assets_without_bearer_token()
    {
        using var workspace = new TempCanvasWorkspaceRoot();
        await SeedCanvasArtifactAsync(workspace.Path, CreateArtifact("canvas-dashboard", CanvasArtifactKind.Dashboard, updatedAtHour: 10));

        var entryAbsolutePath = Path.Combine(
            workspace.Path,
            "workspace",
            "canvas",
            "artifacts",
            "canvas-dashboard",
            "index.html");
        Directory.CreateDirectory(Path.GetDirectoryName(entryAbsolutePath)!);
        await File.WriteAllTextAsync(entryAbsolutePath, "<html><head><link rel=\"stylesheet\" href=\"app.css\"></head><body><h1>dashboard</h1></body></html>");
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(entryAbsolutePath)!, "app.css"), "body { color: red; }");

        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/canvas/canvas-dashboard/entry");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<CanvasEntryResponse>();
        payload.Should().NotBeNull();
        payload!.ArtifactId.Should().Be("canvas-dashboard");
        payload.EntryUrl.Should().StartWith("/api/canvas/preview/");
        payload.EntryUrl.Should().EndWith("/workspace/canvas/artifacts/canvas-dashboard/index.html");

        using var previewClient = new HttpClient
        {
            BaseAddress = hosted.Client.BaseAddress
        };

        var previewResponse = await previewClient.GetAsync(payload.EntryUrl);
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var previewHtml = await previewResponse.Content.ReadAsStringAsync();
        previewHtml.Should().Contain("dashboard");

        var cssResponse = await previewClient.GetAsync(
            payload.EntryUrl.Replace("/index.html", "/app.css", StringComparison.Ordinal));
        cssResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        cssResponse.Content.Headers.ContentType.Should().NotBeNull();
        cssResponse.Content.Headers.ContentType!.MediaType.Should().Be("text/css");
        var cssBody = await cssResponse.Content.ReadAsStringAsync();
        cssBody.Should().Contain("color: red");

        var directFsResponse = await previewClient.GetAsync("/api/canvas/fs/workspace/canvas/artifacts/canvas-dashboard/index.html");
        directFsResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Canvas_detail_should_return_not_found_when_missing()
    {
        using var workspace = new TempCanvasWorkspaceRoot();
        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);

        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.GetAsync("/api/canvas/missing-id");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        payload.Should().NotBeNull();
        payload!.Code.Should().Be("canvas.not_found");
    }

    [Fact]
    public async Task Canvas_post_should_create_artifact_and_return_created_payload()
    {
        using var workspace = new TempCanvasWorkspaceRoot();
        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);

        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var response = await hosted.Client.PostAsJsonAsync(
            "/api/canvas",
            new UpsertCanvasArtifactRequest(
                Id: "canvas-launch",
                Title: "Launch Snapshot",
                Kind: CanvasArtifactKind.Report,
                Summary: "Executive summary board.",
                Source: "runtime.main",
                EntryPath: "workspace/canvas/artifacts/canvas-launch/index.html",
                AssetDirectory: "workspace/canvas/artifacts/canvas-launch",
                Route: "/canvas/launch",
                SessionId: "main-001",
                CorrelationId: "corr-launch-001",
                MetadataJson: null));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var payload = await response.Content.ReadFromJsonAsync<CanvasArtifact>();
        payload.Should().NotBeNull();
        payload!.Id.Should().Be("canvas-launch");
        payload.Kind.Should().Be(CanvasArtifactKind.Report);
        response.Headers.Location.Should().NotBeNull();
    }

    [Fact]
    public async Task Canvas_fs_should_serve_workspace_file_and_reject_traversal()
    {
        using var workspace = new TempCanvasWorkspaceRoot();
        var servedPath = System.IO.Path.Combine(
            workspace.Path,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            "canvas",
            "artifacts",
            "canvas-dashboard",
            "index.html");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(servedPath)!);
        await File.WriteAllTextAsync(servedPath, "<html><body><h1>dashboard</h1></body></html>");
        await using var hosted = await StartRealWorkspaceGatewayAsync(workspace.Path);

        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");

        var fileResponse = await hosted.Client.GetAsync("/api/canvas/fs/workspace/canvas/artifacts/canvas-dashboard/index.html");

        fileResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        fileResponse.Content.Headers.ContentType.Should().NotBeNull();
        fileResponse.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        var body = await fileResponse.Content.ReadAsStringAsync();
        body.Should().Contain("dashboard");

        var traversalResponse = await hosted.Client.GetAsync("/api/canvas/fs/workspace/canvas/%2E%2E/secrets.txt");

        traversalResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await traversalResponse.Content.ReadFromJsonAsync<ErrorResponse>();
        error.Should().NotBeNull();
        error!.Code.Should().Be("validation.canvas_path_invalid");
    }

    private static async Task SeedCanvasArtifactAsync(string workspaceRoot, CanvasArtifact artifact)
    {
        var services = new ServiceCollection();
        services.AddKodaClawWorkspace(options => options.RootPath = workspaceRoot);
        services.AddKodaClawJsonStore(workspaceRoot);
        using var provider = services.BuildServiceProvider();

        var repository = provider.GetRequiredService<ICanvasArtifactRepository>();
        await repository.UpsertAsync(artifact);
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
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                });
            },
            useTestWorkspaceService: false);
    }

    private static CanvasArtifact CreateArtifact(
        string id,
        CanvasArtifactKind kind,
        string source = "runtime.main",
        string? sessionId = null,
        int updatedAtHour = 8)
    {
        return new CanvasArtifact(
            Id: id,
            Title: id == "canvas-dashboard" ? "Ops Wallboard" : "Launch Snapshot",
            Kind: kind,
            Summary: id == "canvas-dashboard" ? "Realtime operations board." : "Executive summary board.",
            Source: source,
            EntryPath: $"workspace/canvas/artifacts/{id}/index.html",
            AssetDirectory: $"workspace/canvas/artifacts/{id}",
            CreatedAt: new DateTimeOffset(2026, 3, 18, 8, 0, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 3, 18, updatedAtHour, 0, 0, TimeSpan.Zero),
            Route: $"/canvas/{id}",
            SessionId: sessionId,
            CorrelationId: $"corr-{id}",
            MetadataJson: null);
    }

    private sealed class TempCanvasWorkspaceRoot : IDisposable
    {
        public TempCanvasWorkspaceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-canvas-api",
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
