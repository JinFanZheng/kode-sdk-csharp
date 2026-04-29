using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;

namespace KodaClaw.ChannelHub.Connectors.DingTalk;

internal sealed record DingTalkConnectorConfiguration(
    string AppKey,
    string AppSecret,
    string RobotCode,
    DeliveryMode? DefaultDeliveryMode = null)
{
    public static async Task<DingTalkConnectorConfiguration> FromAccountAsync(
        ChannelAccount account,
        ChannelSecretResolver? secretResolver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.ConnectorKind != ChannelConnectorKind.DingTalk)
        {
            throw new ArgumentException(
                $"DingTalk connector cannot start account with connector kind '{account.ConnectorKind}'.",
                nameof(account));
        }

        var appKeyFromConfig = default(string);
        var appSecretFromConfig = default(string);
        var robotCodeFromConfig = default(string);
        var credentialReferenceFromConfig = default(string);
        DeliveryMode? defaultDeliveryMode = null;

        if (!string.IsNullOrWhiteSpace(account.ConfigurationJson))
        {
            using var document = JsonDocument.Parse(account.ConfigurationJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "DingTalk account configuration must be a JSON object.", nameof(account));
            }

            var root = document.RootElement;
            appKeyFromConfig = GetOptionalString(root, "appKey");
            appSecretFromConfig = GetOptionalString(root, "appSecret");
            robotCodeFromConfig = GetOptionalString(root, "robotCode");
            credentialReferenceFromConfig = GetOptionalString(root, "credentialReference");

            var configuredDeliveryMode = GetOptionalString(root, "defaultDeliveryMode");
            if (!string.IsNullOrWhiteSpace(configuredDeliveryMode)
                && Enum.TryParse<DeliveryMode>(configuredDeliveryMode, ignoreCase: true, out var parsed))
            {
                defaultDeliveryMode = parsed;
            }
        }

        var appKey = ChannelHubValidation.NormalizeNullableText(appKeyFromConfig ?? account.ExternalAccountId);
        if (string.IsNullOrWhiteSpace(appKey))
        {
            throw new ArgumentException(
                "DingTalk appKey is required (set 'appKey' in ConfigurationJson or ExternalAccountId).",
                nameof(account));
        }

        var resolver = secretResolver ?? new ChannelSecretResolver();
        var appSecret = await resolver.ResolveAsync(
            appSecretFromConfig,
            credentialReferenceFromConfig,
            account.CredentialReference,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(appSecret))
        {
            throw new ArgumentException(
                "DingTalk appSecret is required and must resolve from configuration or credential reference.",
                nameof(account));
        }

        var robotCode = ChannelHubValidation.NormalizeNullableText(robotCodeFromConfig);
        if (string.IsNullOrWhiteSpace(robotCode))
        {
            throw new ArgumentException(
                "DingTalk robotCode is required (set 'robotCode' in ConfigurationJson).",
                nameof(account));
        }

        return new DingTalkConnectorConfiguration(
            AppKey: appKey,
            AppSecret: appSecret,
            RobotCode: robotCode,
            DefaultDeliveryMode: defaultDeliveryMode);
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
