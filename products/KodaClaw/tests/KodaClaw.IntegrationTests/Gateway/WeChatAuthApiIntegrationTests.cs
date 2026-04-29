using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using KodaClaw.ChannelHub.Connectors.WeChat;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class WeChatAuthApiIntegrationTests
{
    private const string GatewayToken = "test-token";

    [Fact]
    public async Task Get_qrcode_should_require_authorization()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);

        var response = await hosted.Client.PostAsJsonAsync("/api/channels/wechat/get-qrcode", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_qrcode_should_return_qrcode_and_img_url()
    {
        using var workspace = new TempWorkspaceRoot();
        var stubClient = new StubWeChatApiClient();
        await using var hosted = await StartGatewayAsync(workspace.Path, stubClient);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var response = await hosted.Client.PostAsJsonAsync("/api/channels/wechat/get-qrcode", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<WeChatQrCodeResult>();
        result.Should().NotBeNull();
        result!.Qrcode.Should().Be("stub-qrcode-token");
        result.QrcodeImgUrl.Should().StartWith("data:image/png;base64,");
    }

    [Fact]
    public async Task Poll_qrcode_status_should_return_wait_status()
    {
        using var workspace = new TempWorkspaceRoot();
        var stubClient = new StubWeChatApiClient();
        await using var hosted = await StartGatewayAsync(workspace.Path, stubClient);
        hosted.Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayToken);

        var response = await hosted.Client.GetAsync(
            "/api/channels/wechat/qrcode-status?qrcode=stub-qrcode-token");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await response.Content.ReadFromJsonAsync<WeChatQrCodeStatus>();
        status.Should().NotBeNull();
        status!.Status.Should().Be("wait");
        status.BotToken.Should().BeNull();
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static Task<HostedGateway> StartGatewayAsync(
        string workspaceRoot,
        StubWeChatApiClient? stubClient = null)
    {
        return HostedGateway.StartAsync(
            gatewayToken: GatewayToken,
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false,
                rootPath: workspaceRoot),
            configureServices: services =>
            {
                if (stubClient is not null)
                {
                    services.AddSingleton<IWeChatApiClient>(stubClient);
                }
            },
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                });
            });
    }

    // ── temp workspace ────────────────────────────────────────────────────

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-wechat-auth",
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

    // ── stub ──────────────────────────────────────────────────────────────

    private sealed class StubWeChatApiClient : IWeChatApiClient
    {
        public Task<ILinkQrCodeResponse> GetQrCodeAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new ILinkQrCodeResponse
            {
                Qrcode = "stub-qrcode-token",
                QrcodeImgContent = "data:image/png;base64,STUB==",
            });
        }

        public Task<ILinkQrCodeStatusResponse> GetQrCodeStatusAsync(
            string qrcode, CancellationToken ct = default)
        {
            return Task.FromResult(new ILinkQrCodeStatusResponse { Status = "wait" });
        }

        public Task<ILinkLoginStatusResponse> CheckLoginStatusAsync(
            string botToken, CancellationToken ct = default)
        {
            return Task.FromResult(new ILinkLoginStatusResponse { Ret = 0 });
        }

        public Task<ILinkGetUpdatesResponse> GetUpdatesAsync(
            string syncBuf, CancellationToken ct = default)
        {
            return Task.FromResult(new ILinkGetUpdatesResponse
            {
                Msgs = [],
                GetUpdatesBuf = syncBuf,
            });
        }

        public Task SendTextAsync(
            string toUserId, string contextToken, string text, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task<ILinkGetConfigResponse> GetConfigAsync(
            string ilinkUserId, string contextToken, CancellationToken ct = default)
        {
            return Task.FromResult(new ILinkGetConfigResponse { TypingTicket = "stub-typing-ticket" });
        }

        public Task SendTypingAsync(
            string ilinkUserId, string typingTicket, int status, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task<ILinkGetUploadUrlResponse> GetUploadUrlAsync(
            ILinkGetUploadUrlRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new ILinkGetUploadUrlResponse
            {
                UploadParam = new ILinkUploadParam { Url = "https://stub-cdn.example.com/upload" }
            });
        }

        public Task SendMediaAsync(
            string toUserId, string contextToken,
            IReadOnlyList<ILinkMessageItem> items, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public void SetBotToken(string botToken) { }
    }
}
