using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.System;
using KodaClaw.Gateway.Channels;

public static partial class GatewayApp
{
    private static bool TryValidateChannelAccountRequest(
        UpsertChannelAccountRequest request,
        out ErrorResponse? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(request.Id))
        {
            error = new ErrorResponse(
                Code: "validation.channel_account_id_required",
                Message: "Channel account id is required.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            error = new ErrorResponse(
                Code: "validation.channel_account_display_name_required",
                Message: "Channel account display name is required.");
            return false;
        }

        var normalizedConfigurationJson = NormalizeOptionalString(request.ConfigurationJson);
        if (normalizedConfigurationJson is null)
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(normalizedConfigurationJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = new ErrorResponse(
                    Code: "validation.channel_account_configuration_invalid",
                    Message: "Channel account configuration must be a JSON object.");
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = new ErrorResponse(
                Code: "validation.channel_account_configuration_invalid",
                Message: "Channel account configuration must be valid JSON.");
            return false;
        }
        catch (ArgumentException ex)
        {
            error = new ErrorResponse(
                Code: "validation.channel_account_configuration_invalid",
                Message: ex.Message);
            return false;
        }
    }

    private static ChannelAccountState ResolveChannelAccountState(
        UpsertChannelAccountRequest request,
        ChannelAccount? existing)
    {
        if (!request.InboundEnabled)
        {
            return ChannelAccountState.Disconnected;
        }

        return request.ConnectorKind switch
        {
            ChannelConnectorKind.GenericWebhook => ChannelAccountState.Connected,
            ChannelConnectorKind.Telegram => existing?.State ?? ChannelAccountState.Disconnected,
            ChannelConnectorKind.Feishu => existing?.State ?? ChannelAccountState.Disconnected,
            ChannelConnectorKind.DingTalk => existing?.State ?? ChannelAccountState.Disconnected,
            _ => existing?.State ?? ChannelAccountState.Disconnected,
        };
    }

    /// <summary>
    /// 将 defaultDeliveryMode 写入（或更新）账号的 ConfigurationJson，保留其余字段不变。
    /// </summary>
    private static string? MergeDefaultDeliveryMode(string? existingJson, DeliveryMode mode)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(existingJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        dict[prop.Name] = prop.Value.Clone();
                }
            }
            catch (JsonException) { /* 忽略损坏的 JSON，重新构建 */ }
        }

        dict["defaultDeliveryMode"] = mode.ToString();
        return JsonSerializer.Serialize(dict);
    }

    private static string? NormalizeOptionalJson(string? value)
    {
        var normalized = NormalizeOptionalString(value);
        if (normalized is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(normalized);
        return document.RootElement.GetRawText();
    }

    private static async Task<ChannelAccount> ReconcileChannelAccountRuntimeAsync(
        ChannelAccount account,
        ChannelAccount? existing,
        IChannelAccountRepository channelAccountRepository,
        ChannelInboundGatewayService channelInboundGatewayService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(channelAccountRepository);
        ArgumentNullException.ThrowIfNull(channelInboundGatewayService);

        if (account.ConnectorKind is not ChannelConnectorKind.Telegram
            and not ChannelConnectorKind.Feishu
            and not ChannelConnectorKind.WeChat
            and not ChannelConnectorKind.DingTalk
            and not ChannelConnectorKind.Relay)
        {
            await channelAccountRepository.UpsertAsync(account, cancellationToken);
            return account;
        }

        if (!account.InboundEnabled)
        {
            await StopConnectorAccountAsync(account, channelInboundGatewayService, cancellationToken);
            var disconnected = account with
            {
                State = ChannelAccountState.Disconnected,
                LastDisconnectedAt = account.LastDisconnectedAt ?? DateTimeOffset.UtcNow,
                LastError = null,
            };
            await channelAccountRepository.UpsertAsync(disconnected, cancellationToken);
            return disconnected;
        }

        try
        {
            await StopConnectorAccountAsync(account, channelInboundGatewayService, cancellationToken);
            await StartConnectorAccountAsync(account, channelInboundGatewayService, cancellationToken);

            var connectedAt = DateTimeOffset.UtcNow;
            var connected = account with
            {
                State = ChannelAccountState.Connected,
                LastConnectedAt = existing?.LastConnectedAt ?? connectedAt,
                LastDisconnectedAt = existing?.LastDisconnectedAt,
                LastError = null,
            };
            await channelAccountRepository.UpsertAsync(connected, cancellationToken);
            return connected;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            var degraded = account with
            {
                State = ChannelAccountState.Degraded,
                LastConnectedAt = existing?.LastConnectedAt,
                LastDisconnectedAt = existing?.LastDisconnectedAt,
                LastError = ex.Message,
            };
            await channelAccountRepository.UpsertAsync(degraded, cancellationToken);
            return degraded;
        }
    }

    private static Task StopConnectorAccountAsync(
        ChannelAccount account,
        ChannelInboundGatewayService channelInboundGatewayService,
        CancellationToken cancellationToken)
    {
        return channelInboundGatewayService.StopAccountAsync(account.Id, cancellationToken);
    }

    private static Task StartConnectorAccountAsync(
        ChannelAccount account,
        ChannelInboundGatewayService channelInboundGatewayService,
        CancellationToken cancellationToken)
    {
        return channelInboundGatewayService.StartAccountAsync(account, cancellationToken);
    }
}
