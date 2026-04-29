using System.Text.Json;
using KodaClaw.ChannelHub.Connectors.WeChat;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.System;
using KodaClaw.Gateway.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

public static partial class GatewayApp
{
    private static void MapWeChatAuthEndpoints(WebApplication app)
    {
        var wechat = app.MapGroup("/api/channels/wechat");

        // ── 获取扫码登录二维码 ─────────────────────────────────────
        wechat.MapPost("/get-qrcode", async (
            HttpContext context,
            IConfiguration configuration,
            IWeChatApiClient weChatApiClient,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            try
            {
                var response = await weChatApiClient.GetQrCodeAsync(cancellationToken);
                return Results.Ok(new WeChatQrCodeResult(
                    Qrcode: response.Qrcode,
                    QrcodeImgUrl: response.QrcodeImgContent));
            }
            catch (Exception ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.wechat",
                    eventType: "gateway.wechat.get_qrcode_failed",
                    level: "error",
                    message: $"Failed to get WeChat QR code: {ex.Message}");
                return Results.Problem(
                    title: "Failed to get WeChat QR code",
                    detail: ex.Message,
                    statusCode: 502);
            }
        });

        // ── 轮询二维码扫描状态 ─────────────────────────────────────
        wechat.MapGet("/qrcode-status", async (
            string qrcode,
            HttpContext context,
            IConfiguration configuration,
            IWeChatApiClient weChatApiClient,
            IChannelAccountRepository channelAccountRepository,
            ChannelInboundGatewayService channelInboundGatewayService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(qrcode))
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.qrcode_required",
                    Message: "qrcode parameter is required."));
            }

            try
            {
                var response = await weChatApiClient.GetQrCodeStatusAsync(qrcode, cancellationToken);
                var status = new WeChatQrCodeStatus(response.Status, response.BotToken);

                // 扫码确认后：保存 BotToken 到账号并启动 Connector
                if (string.Equals(response.Status, "confirmed", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(response.BotToken))
                {
                    await HandleWeChatLoginConfirmedAsync(
                        response.BotToken,
                        response.ILinkBotId,
                        channelAccountRepository,
                        channelInboundGatewayService,
                        cancellationToken);
                }

                return Results.Ok(status);
            }
            catch (Exception ex)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.wechat",
                    eventType: "gateway.wechat.qrcode_status_failed",
                    level: "error",
                    message: $"Failed to poll WeChat QR code status: {ex.Message}");
                return Results.Problem(
                    title: "Failed to poll WeChat QR code status",
                    detail: ex.Message,
                    statusCode: 502);
            }
        });

        // ── 验证 BotToken 有效性 ───────────────────────────────────
        wechat.MapPost("/test-credentials", async (
            TestWeChatCredentialsRequest request,
            HttpContext context,
            IConfiguration configuration,
            IWeChatApiClient weChatApiClient,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.BotToken))
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.wechat_bot_token_required",
                    Message: "BotToken is required."));
            }

            try
            {
                var loginStatus = await weChatApiClient.CheckLoginStatusAsync(
                    request.BotToken.Trim(), cancellationToken);
                var ok = loginStatus.Ret == 0;
                return Results.Ok(new TestWeChatCredentialsResponse(
                    Ok: ok,
                    Error: ok ? null : $"Login status check returned ret={loginStatus.Ret}"));
            }
            catch (Exception ex)
            {
                return Results.Ok(new TestWeChatCredentialsResponse(Ok: false, Error: ex.Message));
            }
        });
    }

    // ── 登录确认后的账号处理 ───────────────────────────────────────

    private static async Task HandleWeChatLoginConfirmedAsync(
        string botToken,
        string? ilinkBotId,
        IChannelAccountRepository channelAccountRepository,
        ChannelInboundGatewayService channelInboundGatewayService,
        CancellationToken cancellationToken)
    {
        // 用 botToken 的哈希作为账号 ID（稳定且不暴露 token 明文）
        var accountId = $"wechat-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(botToken)))[..12].ToLowerInvariant()}";

        var existing = await channelAccountRepository.GetByIdAsync(accountId, cancellationToken);

        // BotToken 直接存 ConfigurationJson（明文）
        // 生产建议迁移到 CredentialReference + Keychain
        var configJson = JsonSerializer.Serialize(new { botToken }, GatewayJson.Options);

        var now = DateTimeOffset.UtcNow;
        var account = new ChannelAccount(
            Id: accountId,
            ConnectorKind: ChannelConnectorKind.WeChat,
            DisplayName: existing?.DisplayName ?? "微信",
            State: ChannelAccountState.Connected,
            CreatedAt: existing?.CreatedAt ?? now,
            UpdatedAt: now,
            ExternalAccountId: ilinkBotId ?? string.Empty,
            CredentialReference: null,
            ConfigurationJson: configJson,
            InboundEnabled: true,
            LastConnectedAt: now,
            LastDisconnectedAt: existing?.LastDisconnectedAt,
            LastError: null);

        await channelAccountRepository.UpsertAsync(account, cancellationToken);

        // 停止旧的（如果有），再启动新的
        // 注意：必须用 CancellationToken.None，不能用 HTTP 请求 token。
        // 若用 HTTP token，请求结束后 token 取消会杀掉轮询 loop，
        // 且 _startedAccounts 里的死条目会阻止 loop 再次启动。
        await channelInboundGatewayService.StopAccountAsync(accountId, CancellationToken.None);
        await channelInboundGatewayService.StartAccountAsync(account, CancellationToken.None);
    }
}

// ── 请求 / 响应 DTO ───────────────────────────────────────────────────────────

internal sealed record TestWeChatCredentialsRequest(string? BotToken);

internal sealed record TestWeChatCredentialsResponse(bool Ok, string? Error);
