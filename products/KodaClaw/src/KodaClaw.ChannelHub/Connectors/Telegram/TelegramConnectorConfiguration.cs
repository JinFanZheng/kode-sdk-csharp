using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;

namespace KodaClaw.ChannelHub.Connectors.Telegram;

internal sealed record TelegramConnectorConfiguration(
    string BotToken,
    int LongPollingTimeoutSeconds,
    DeliveryMode? DefaultDeliveryMode = null)
{
    private const int DefaultPollingTimeoutSeconds = 25;

    public static async Task<TelegramConnectorConfiguration> FromAccountAsync(
        ChannelAccount account,
        ChannelSecretResolver? secretResolver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.ConnectorKind != ChannelConnectorKind.Telegram)
        {
            throw new ArgumentException(
                $"Telegram connector cannot start account with connector kind '{account.ConnectorKind}'.",
                nameof(account));
        }

        var tokenFromConfig = default(string);
        var credentialReferenceFromConfig = default(string);
        var pollingTimeoutSeconds = DefaultPollingTimeoutSeconds;
        DeliveryMode? defaultDeliveryMode = null;

        if (!string.IsNullOrWhiteSpace(account.ConfigurationJson))
        {
            using var document = JsonDocument.Parse(account.ConfigurationJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Telegram account configuration must be a JSON object.", nameof(account));
            }

            var root = document.RootElement;
            tokenFromConfig = GetOptionalString(root, "botToken")
                ?? GetOptionalString(root, "token");
            credentialReferenceFromConfig = GetOptionalString(root, "credentialReference");
            var configuredPollingTimeout = GetOptionalInt(root, "pollingTimeoutSeconds");
            if (configuredPollingTimeout.HasValue)
            {
                pollingTimeoutSeconds = Math.Clamp(configuredPollingTimeout.Value, 1, 60);
            }

            var configuredDeliveryMode = GetOptionalString(root, "defaultDeliveryMode");
            if (!string.IsNullOrWhiteSpace(configuredDeliveryMode)
                && Enum.TryParse<DeliveryMode>(configuredDeliveryMode, ignoreCase: true, out var parsed))
            {
                defaultDeliveryMode = parsed;
            }
        }

        var botToken = await (secretResolver ?? new ChannelSecretResolver()).ResolveAsync(
            tokenFromConfig,
            credentialReferenceFromConfig,
            account.CredentialReference,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(botToken))
        {
            throw new ArgumentException(
                "Telegram bot token is required and must resolve from configuration or credential reference.",
                nameof(account));
        }

        return new TelegramConnectorConfiguration(
            BotToken: botToken,
            LongPollingTimeoutSeconds: pollingTimeoutSeconds,
            DefaultDeliveryMode: defaultDeliveryMode);
    }

    private static int? GetOptionalInt(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetInt32(out var intValue))
            {
                return intValue;
            }

            if (property.Value.ValueKind == JsonValueKind.String &&
                int.TryParse(property.Value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                return ChannelHubValidation.NormalizeNullableText(property.Value.GetString());
            }
        }

        return null;
    }

}
