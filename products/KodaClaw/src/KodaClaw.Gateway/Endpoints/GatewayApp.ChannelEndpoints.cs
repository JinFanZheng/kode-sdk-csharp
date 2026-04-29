using System.IO;
using System.Net.Http;
using System.Text.Json;
using KodaClaw.ChannelHub;
using KodaClaw.ChannelHub.Audit;
using KodaClaw.ChannelHub.Connectors.Webhook;
using KodaClaw.ChannelHub.Policy;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Approvals;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Gateway.Channels;
using KodaClaw.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

public static partial class GatewayApp
{
    private static void MapChannelEndpoints(WebApplication app)
    {
        var channels = app.MapGroup("/api/channels");
        channels.MapGet("/connectors", (
            HttpContext context,
            IConfiguration configuration,
            IDiagnosticsService diagnosticsService) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            return Results.Ok(new[]
            {
                new
                {
                    Kind = ChannelConnectorKind.GenericWebhook,
                    DisplayName = "Generic Webhook",
                    Implemented = true,
                    SupportsInbound = true,
                    SupportsOutbound = false,
                    ProductOwned = true,
                },
                new
                {
                    Kind = ChannelConnectorKind.Telegram,
                    DisplayName = "Telegram",
                    Implemented = true,
                    SupportsInbound = true,
                    SupportsOutbound = true,
                    ProductOwned = true,
                },
                new
                {
                    Kind = ChannelConnectorKind.Feishu,
                    DisplayName = "飞书 / Lark",
                    Implemented = true,
                    SupportsInbound = true,
                    SupportsOutbound = true,
                    ProductOwned = true,
                },
                new
                {
                    Kind = ChannelConnectorKind.WeChat,
                    DisplayName = "微信",
                    Implemented = true,
                    SupportsInbound = true,
                    SupportsOutbound = true,
                    ProductOwned = true,
                },
                new
                {
                    Kind = ChannelConnectorKind.DingTalk,
                    DisplayName = "钉钉 / DingTalk",
                    Implemented = true,
                    SupportsInbound = true,
                    SupportsOutbound = true,
                    ProductOwned = true,
                },
            });
        });

        channels.MapGet("/accounts", async (
            HttpContext context,
            string? connectorKind,
            string? state,
            int? limit,
            IConfiguration configuration,
            IChannelAccountRepository channelAccountRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            if (!TryParseEnum(connectorKind, out ChannelConnectorKind? parsedConnectorKind))
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.channel_connector_kind_invalid",
                    Message: "Channel connector kind is invalid."));
            }

            if (!TryParseEnum(state, out ChannelAccountState? parsedState))
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.channel_account_state_invalid",
                    Message: "Channel account state is invalid."));
            }

            var items = await channelAccountRepository.ListAsync(
                new ChannelAccountQuery(
                    ConnectorKind: parsedConnectorKind,
                    State: parsedState,
                    Limit: NormalizeChannelsLimit(limit)),
                cancellationToken);

            return Results.Ok(items);
        });

        channels.MapPost("/accounts", async (
            HttpContext context,
            UpsertChannelAccountRequest request,
            IConfiguration configuration,
            IChannelAccountRepository channelAccountRepository,
            ChannelInboundGatewayService channelInboundGatewayService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            if (!TryValidateChannelAccountRequest(request, out var validationError))
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.channels",
                    eventType: "gateway.channels.invalid_request",
                    level: "warning",
                    message: validationError!.Message,
                    attributes: new Dictionary<string, string?>
                    {
                        ["accountId"] = request.Id,
                    });
                return Results.BadRequest(validationError);
            }

            var existing = await channelAccountRepository.GetByIdAsync(request.Id, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var state = ResolveChannelAccountState(request, existing);
            var account = new ChannelAccount(
                Id: request.Id.Trim(),
                ConnectorKind: request.ConnectorKind,
                DisplayName: request.DisplayName.Trim(),
                State: state,
                CreatedAt: existing?.CreatedAt ?? now,
                UpdatedAt: now,
                ExternalAccountId: NormalizeOptionalString(request.ExternalAccountId),
                CredentialReference: NormalizeOptionalString(request.CredentialReference),
                Description: NormalizeOptionalString(request.Description),
                ConfigurationJson: NormalizeOptionalJson(request.ConfigurationJson),
                InboundEnabled: request.InboundEnabled,
                LastConnectedAt: state == ChannelAccountState.Connected
                    ? existing?.LastConnectedAt ?? now
                    : existing?.LastConnectedAt,
                LastDisconnectedAt: state == ChannelAccountState.Disconnected
                    ? now
                    : existing?.LastDisconnectedAt,
                LastError: existing?.LastError);

            account = await ReconcileChannelAccountRuntimeAsync(
                account,
                existing,
                channelAccountRepository,
                channelInboundGatewayService,
                cancellationToken);

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.channels",
                eventType: existing is null
                    ? "gateway.channels.account_created"
                    : "gateway.channels.account_updated",
                level: "info",
                message: existing is null
                    ? "Created channel account."
                    : "Updated channel account.",
                attributes: new Dictionary<string, string?>
                {
                    ["accountId"] = account.Id,
                    ["connectorKind"] = account.ConnectorKind.ToString(),
                    ["state"] = account.State.ToString(),
                });

            return existing is null
                ? Results.Created($"/api/channels/accounts/{account.Id}", account)
                : Results.Ok(account);
        });

        channels.MapGet("/threads", async (
            HttpContext context,
            string? connectorKind,
            string? accountId,
            string? threadType,
            string? sessionKind,
            string? sessionId,
            int? limit,
            IConfiguration configuration,
            IThreadBindingRepository threadBindingRepository,
            IChannelAccountRepository channelAccountRepository,
            ChannelAuditQueryService channelAuditQueryService,
            IApprovalRepository approvalRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            if (!TryParseEnum(connectorKind, out ChannelConnectorKind? parsedConnectorKind))
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.channel_connector_kind_invalid",
                    Message: "Channel connector kind is invalid."));
            }

            if (!TryParseEnum(threadType, out ChannelThreadType? parsedThreadType))
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.channel_thread_type_invalid",
                    Message: "Channel thread type is invalid."));
            }

            if (!TryParseEnum(sessionKind, out SessionKind? parsedSessionKind))
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.channel_session_kind_invalid",
                    Message: "Channel session kind is invalid."));
            }

            var bindings = await threadBindingRepository.ListAsync(
                new ChannelQuery(
                    ConnectorKind: parsedConnectorKind,
                    AccountId: accountId,
                    ThreadType: parsedThreadType,
                    SessionKind: parsedSessionKind,
                    SessionId: sessionId,
                    Limit: NormalizeChannelsLimit(limit)),
                cancellationToken);

            var items = new List<ChannelThreadSummary>(bindings.Count);
            foreach (var binding in bindings)
            {
                var account = await channelAccountRepository.GetByIdAsync(binding.AccountId, cancellationToken);
                var pendingApproval = await LoadPendingChannelApprovalAsync(
                    approvalRepository,
                    binding.SessionId,
                    cancellationToken);
                var recentAudit = await channelAuditQueryService.ListRecentByBindingIdAsync(binding.Id, 6, cancellationToken);

                items.Add(BuildChannelThreadSummary(
                    binding,
                    account,
                    pendingApproval,
                    TryResolveLastTurnOutcome(recentAudit)));
            }

            return Results.Ok(new ChannelsQueryResponse(items));
        });

        channels.MapGet("/threads/{bindingId}", async (
            HttpContext context,
            string bindingId,
            IConfiguration configuration,
            IWorkspaceService workspaceService,
            IThreadBindingRepository threadBindingRepository,
            IChannelAccountRepository channelAccountRepository,
            ChannelPolicyEngine channelPolicyEngine,
            ChannelAuditQueryService channelAuditQueryService,
            IApprovalRepository approvalRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var detail = await LoadChannelThreadDetailAsync(
                workspaceService,
                threadBindingRepository,
                channelAccountRepository,
                channelPolicyEngine,
                channelAuditQueryService,
                approvalRepository,
                bindingId,
                cancellationToken);
            if (detail is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.channels",
                    eventType: "gateway.channels.thread_not_found",
                    level: "warning",
                    message: "Requested channel thread was not found.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["bindingId"] = bindingId,
                    });
                return Results.NotFound(new ErrorResponse(
                    Code: "channel.thread_not_found",
                    Message: "Channel thread was not found."));
            }

            return Results.Ok(detail);
        });

        channels.MapPatch("/threads/{bindingId}/settings", async (
            HttpContext context,
            string bindingId,
            UpdateThreadSettingsRequest request,
            IConfiguration configuration,
            IThreadBindingRepository threadBindingRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            if (request.DeliveryMode is null)
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.delivery_mode_required",
                    Message: "At least one setting field is required."));
            }

            var found = await threadBindingRepository.UpdateDeliveryModeOverrideAsync(
                bindingId,
                request.DeliveryMode,
                cancellationToken);

            if (!found)
            {
                return Results.NotFound(new ErrorResponse(
                    Code: "channel.thread_not_found",
                    Message: "Channel thread was not found."));
            }

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.channels",
                eventType: "gateway.channels.thread_settings_updated",
                level: "info",
                message: "Channel thread settings updated.",
                attributes: new Dictionary<string, string?>
                {
                    ["bindingId"] = bindingId,
                    ["deliveryMode"] = request.DeliveryMode?.ToString(),
                });

            return Results.NoContent();
        });

        channels.MapGet("/threads/{bindingId}/audit", async (
            HttpContext context,
            string bindingId,
            int? limit,
            IConfiguration configuration,
            IThreadBindingRepository threadBindingRepository,
            ChannelAuditQueryService channelAuditQueryService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var binding = await threadBindingRepository.GetByIdAsync(bindingId, cancellationToken);
            if (binding is null)
            {
                return Results.NotFound(new ErrorResponse(
                    Code: "channel.thread_not_found",
                    Message: "Channel thread was not found."));
            }

            var items = await channelAuditQueryService.ListRecentByBindingIdAsync(
                bindingId,
                NormalizeChannelsLimit(limit),
                cancellationToken);

            return Results.Ok(items);
        });

        channels.MapPost("/webhook/{accountId}/events", async (
            HttpContext context,
            string accountId,
            IConfiguration configuration,
            IWorkspaceService workspaceService,
            IChannelAccountRepository channelAccountRepository,
            IThreadBindingRepository threadBindingRepository,
            GenericWebhookConnector webhookConnector,
            ChannelPolicyEngine channelPolicyEngine,
            ChannelAuditQueryService channelAuditQueryService,
            IApprovalRepository approvalRepository,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var account = await channelAccountRepository.GetByIdAsync(accountId, cancellationToken);
            if (account is null)
            {
                return Results.NotFound(new ErrorResponse(
                    Code: "channel.account_not_found",
                    Message: "Channel account was not found."));
            }

            if (account.ConnectorKind != ChannelConnectorKind.GenericWebhook)
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.channel_account_connector_invalid",
                    Message: "Channel account is not a generic webhook account."));
            }

            if (!account.InboundEnabled)
            {
                return Results.Conflict(new ErrorResponse(
                    Code: "channel.account_inbound_disabled",
                    Message: "Channel account inbound delivery is disabled."));
            }

            using var reader = new StreamReader(context.Request.Body);
            var payloadJson = await reader.ReadToEndAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.channel_webhook_payload_required",
                    Message: "Webhook payload is required."));
            }

            var channelInboundGatewayService = context.RequestServices.GetRequiredService<ChannelInboundGatewayService>();
            ChannelInboundHandlingResult? handlingResult = null;
            var dispatchResult = await webhookConnector.HandleInboundAsync(
                account,
                payloadJson,
                context.Request.Headers[WebhookSecretHeaderName].ToString(),
                async (envelope, token) =>
                {
                    handlingResult = await channelInboundGatewayService.ProcessAsync(envelope, token);
                },
                cancellationToken);

            if (!dispatchResult.Accepted)
            {
                var (statusCode, error) = MapWebhookRejection(dispatchResult);
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.channels",
                    eventType: "gateway.channels.webhook_rejected",
                    level: statusCode == StatusCodes.Status401Unauthorized ? "warning" : "error",
                    message: error.Message,
                    attributes: new Dictionary<string, string?>
                    {
                        ["accountId"] = account.Id,
                        ["connectorKind"] = account.ConnectorKind.ToString(),
                        ["rejectionCode"] = dispatchResult.RejectionCode,
                    });
                return Results.Json(error, statusCode: statusCode);
            }

            if (handlingResult?.Processing is null)
            {
                return Results.Json(
                    new ErrorResponse(
                        Code: "channel.webhook_processing_failed",
                        Message: "Webhook event was accepted but channel processing did not complete."),
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var detail = await LoadChannelThreadDetailAsync(
                workspaceService,
                threadBindingRepository,
                channelAccountRepository,
                channelPolicyEngine,
                channelAuditQueryService,
                approvalRepository,
                handlingResult.Processing.Binding.Id,
                cancellationToken);

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.channels",
                eventType: "gateway.channels.webhook_accepted",
                level: "info",
                message: "Accepted generic webhook event.",
                sessionId: handlingResult.Processing.Binding.SessionId,
                attributes: new Dictionary<string, string?>
                {
                    ["accountId"] = account.Id,
                    ["bindingId"] = handlingResult.Processing.Binding.Id,
                    ["createdBinding"] = handlingResult.Processing.CreatedBinding.ToString(),
                    ["eventType"] = dispatchResult.Event!.EventType.ToString(),
                    ["turnOutcomeKind"] = handlingResult.Turn?.Outcome.Kind.ToString(),
                });

            return Results.Ok(detail);
        });

        channels.MapPatch("/accounts/{id}", async (
            HttpContext context,
            string id,
            PatchChannelAccountRequest request,
            IConfiguration configuration,
            IChannelAccountRepository channelAccountRepository,
            IThreadBindingRepository threadBindingRepository,
            IChannelConnectorRegistry channelConnectorRegistry,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var existing = await channelAccountRepository.GetByIdAsync(id, cancellationToken);
            if (existing is null)
            {
                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.channels",
                    eventType: "gateway.channels.account_not_found",
                    level: "warning",
                    message: "Patch channel account targeted a missing account.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["accountId"] = id,
                    });
                return Results.NotFound(new ErrorResponse(
                    Code: "channel.account_not_found",
                    Message: "Channel account was not found."));
            }

            var enabledChanged = request.Enabled.HasValue && request.Enabled.Value != existing.InboundEnabled;

            // 若传入 DeliveryMode，将 defaultDeliveryMode 合并进 ConfigurationJson
            var updatedConfigJson = existing.ConfigurationJson;

            // 若传入 ConfigurationJson，用新的配置覆盖
            if (!string.IsNullOrWhiteSpace(request.ConfigurationJson))
                updatedConfigJson = request.ConfigurationJson;
            if (request.DeliveryMode.HasValue)
                updatedConfigJson = MergeDefaultDeliveryMode(updatedConfigJson, request.DeliveryMode.Value);

            var updated = existing with
            {
                DisplayName = NormalizeOptionalString(request.DisplayName) ?? existing.DisplayName,
                InboundEnabled = request.Enabled ?? existing.InboundEnabled,
                ConfigurationJson = updatedConfigJson,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await channelAccountRepository.UpsertAsync(updated, cancellationToken);

            // 批量更新该账号所有线程绑定的投递模式
            if (request.DeliveryMode.HasValue)
                await threadBindingRepository.UpdateDeliveryModeByAccountIdAsync(id, request.DeliveryMode.Value, cancellationToken);

            var configChanged = !string.Equals(updatedConfigJson, existing.ConfigurationJson, StringComparison.Ordinal);
            var needReload = enabledChanged || configChanged;

            if (needReload)
            {
                if (updated.InboundEnabled)
                {
                    _ = Task.Run(
                        () => channelConnectorRegistry.ReloadAccountAsync(id, CancellationToken.None),
                        CancellationToken.None);
                }
                else
                {
                    _ = Task.Run(
                        () => channelConnectorRegistry.StopAccountAsync(id, CancellationToken.None),
                        CancellationToken.None);
                }
            }

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.channels",
                eventType: "gateway.channels.account_patched",
                level: "info",
                message: "Patched channel account.",
                attributes: new Dictionary<string, string?>
                {
                    ["accountId"] = id,
                    ["enabledChanged"] = enabledChanged.ToString(),
                });

            var reloaded = await channelAccountRepository.GetByIdAsync(id, cancellationToken) ?? updated;
            return Results.Ok(reloaded);
        });

        channels.MapDelete("/accounts/{id}", async (
            HttpContext context,
            string id,
            IConfiguration configuration,
            IChannelAccountRepository channelAccountRepository,
            IThreadBindingRepository threadBindingRepository,
            IChannelConnectorRegistry channelConnectorRegistry,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var existing = await channelAccountRepository.GetByIdAsync(id, cancellationToken);
            if (existing is null)
            {
                return Results.NotFound(new ErrorResponse(
                    Code: "channel.account_not_found",
                    Message: "Channel account was not found."));
            }

            // Stop the connector before deleting.
            _ = Task.Run(
                () => channelConnectorRegistry.StopAccountAsync(id, CancellationToken.None),
                CancellationToken.None);

            // 删除该账号的所有线程绑定（线程索引）
            await threadBindingRepository.DeleteByAccountIdAsync(id, cancellationToken);

            await channelAccountRepository.DeleteAsync(id, cancellationToken);

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.channels",
                eventType: "gateway.channels.account_deleted",
                level: "info",
                message: "Deleted channel account.",
                attributes: new Dictionary<string, string?>
                {
                    ["accountId"] = id,
                    ["connectorKind"] = existing.ConnectorKind.ToString(),
                });

            return Results.NoContent();
        });

        channels.MapPost("/test-telegram-token", async (
            HttpContext context,
            TestTelegramTokenRequest request,
            IConfiguration configuration,
            IDiagnosticsService diagnosticsService,
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.BotToken))
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.telegram_bot_token_required",
                    Message: "Bot token is required."));
            }

            var token = request.BotToken.Trim();
            var url = $"https://api.telegram.org/bot{token}/getMe";

            try
            {
                var httpClient = httpClientFactory.CreateClient();
                using var response = await httpClient.GetAsync(url, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                var ok = root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
                if (!ok)
                {
                    var errorDescription = root.TryGetProperty("description", out var desc)
                        ? desc.GetString()
                        : "Unknown Telegram error.";
                    return Results.Ok(new TestTelegramTokenResponse(
                        Ok: false,
                        Error: errorDescription));
                }

                string? botName = null;
                string? botUsername = null;
                if (root.TryGetProperty("result", out var result))
                {
                    if (result.TryGetProperty("first_name", out var firstName))
                    {
                        botName = firstName.GetString();
                    }

                    if (result.TryGetProperty("username", out var username))
                    {
                        botUsername = username.GetString();
                    }
                }

                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.channels",
                    eventType: "gateway.channels.telegram_token_verified",
                    level: "info",
                    message: $"Telegram bot token verified for @{botUsername}.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["botUsername"] = botUsername,
                    });

                return Results.Ok(new TestTelegramTokenResponse(
                    Ok: true,
                    BotName: botName,
                    BotUsername: botUsername));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Results.Ok(new TestTelegramTokenResponse(
                    Ok: false,
                    Error: ex.Message));
            }
        });

        channels.MapPost("/test-feishu-credentials", async (
            HttpContext context,
            TestFeishuCredentialsRequest request,
            IConfiguration configuration,
            IDiagnosticsService diagnosticsService,
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.AppId) || string.IsNullOrWhiteSpace(request.AppSecret))
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "validation.feishu_credentials_required",
                    Message: "App ID and App Secret are required."));
            }

            try
            {
                var httpClient = httpClientFactory.CreateClient();
                using var tokenRequest = new HttpRequestMessage(
                    HttpMethod.Post,
                    "https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal");
                tokenRequest.Content = JsonContent.Create(
                    new { app_id = request.AppId.Trim(), app_secret = request.AppSecret.Trim() });

                using var tokenResponse = await httpClient.SendAsync(tokenRequest, cancellationToken);
                var responseBody = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);

                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                if (!root.TryGetProperty("code", out var codeProp) || codeProp.GetInt32() != 0)
                {
                    var errorMsg = root.TryGetProperty("msg", out var msgProp)
                        ? msgProp.GetString()
                        : "Invalid Feishu credentials.";
                    return Results.Ok(new TestFeishuCredentialsResponse(Ok: false, Error: errorMsg));
                }

                // 尝试获取应用名称（通过 app_access_token 查应用信息）
                string? appName = null;
                try
                {
                    using var appTokenRequest = new HttpRequestMessage(
                        HttpMethod.Post,
                        "https://open.feishu.cn/open-apis/auth/v3/app_access_token/internal");
                    appTokenRequest.Content = JsonContent.Create(
                        new { app_id = request.AppId.Trim(), app_secret = request.AppSecret.Trim() });

                    using var appTokenResponse = await httpClient.SendAsync(appTokenRequest, cancellationToken);
                    var appTokenBody = await appTokenResponse.Content.ReadAsStringAsync(cancellationToken);
                    using var appTokenDoc = JsonDocument.Parse(appTokenBody);
                    var appTokenRoot = appTokenDoc.RootElement;

                    if (appTokenRoot.TryGetProperty("code", out var appCode) && appCode.GetInt32() == 0
                        && appTokenRoot.TryGetProperty("app_access_token", out var appToken))
                    {
                        var appAccessToken = appToken.GetString();
                        if (!string.IsNullOrWhiteSpace(appAccessToken))
                        {
                            using var infoRequest = new HttpRequestMessage(
                                HttpMethod.Get,
                                "https://open.feishu.cn/open-apis/application/v6/applications/me");
                            infoRequest.Headers.Authorization =
                                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", appAccessToken);
                            using var infoResponse = await httpClient.SendAsync(infoRequest, cancellationToken);
                            if (infoResponse.IsSuccessStatusCode)
                            {
                                var infoBody = await infoResponse.Content.ReadAsStringAsync(cancellationToken);
                                using var infoDoc = JsonDocument.Parse(infoBody);
                                var infoRoot = infoDoc.RootElement;
                                if (infoRoot.TryGetProperty("data", out var data)
                                    && data.TryGetProperty("app", out var app)
                                    && app.TryGetProperty("app_name", out var name))
                                {
                                    appName = name.GetString();
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ChannelEndpoints] Failed to retrieve Feishu app name: {ex.Message}");
                }

                RecordDiagnosticEvent(
                    diagnosticsService,
                    context,
                    source: "gateway.channels",
                    eventType: "gateway.channels.feishu_credentials_verified",
                    level: "info",
                    message: $"Feishu credentials verified for app {request.AppId}.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["appId"] = request.AppId,
                        ["appName"] = appName,
                    });

                return Results.Ok(new TestFeishuCredentialsResponse(Ok: true, AppName: appName));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Results.Ok(new TestFeishuCredentialsResponse(Ok: false, Error: ex.Message));
            }
        });

    }
}
