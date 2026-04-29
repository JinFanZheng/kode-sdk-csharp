using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;

namespace KodaClaw.ChannelHub.Connectors.Webhook;

internal sealed record GenericWebhookConnectorConfiguration(
    string? SharedSecret,
    ChannelThreadType DefaultThreadType,
    DeliveryMode? DefaultDeliveryMode = null)
{
    public static async Task<GenericWebhookConnectorConfiguration> FromAccountAsync(
        ChannelAccount account,
        ChannelSecretResolver? secretResolver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var sharedSecretFromConfig = default(string);
        var credentialReferenceFromConfig = default(string);
        var defaultThreadType = ChannelThreadType.DirectMessage;
        DeliveryMode? defaultDeliveryMode = null;

        if (!string.IsNullOrWhiteSpace(account.ConfigurationJson))
        {
            using var document = JsonDocument.Parse(account.ConfigurationJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Channel account configuration must be a JSON object.", nameof(account));
            }

            var root = document.RootElement;
            sharedSecretFromConfig = GetOptionalString(root, "sharedSecret")
                ?? GetOptionalString(root, "secret");
            credentialReferenceFromConfig = GetOptionalString(root, "credentialReference");

            var configuredDefaultThreadType = GetOptionalString(root, "defaultThreadType");
            if (!string.IsNullOrWhiteSpace(configuredDefaultThreadType))
            {
                defaultThreadType = GenericWebhookPayloadParser.ParseThreadType(
                    configuredDefaultThreadType,
                    defaultThreadType);
            }

            var configuredDeliveryMode = GetOptionalString(root, "defaultDeliveryMode");
            if (!string.IsNullOrWhiteSpace(configuredDeliveryMode)
                && Enum.TryParse<DeliveryMode>(configuredDeliveryMode, ignoreCase: true, out var parsed))
            {
                defaultDeliveryMode = parsed;
            }
        }

        var resolvedSecret = await (secretResolver ?? new ChannelSecretResolver()).ResolveAsync(
            sharedSecretFromConfig,
            credentialReferenceFromConfig,
            account.CredentialReference,
            cancellationToken).ConfigureAwait(false);

        return new GenericWebhookConnectorConfiguration(
            SharedSecret: resolvedSecret,
            DefaultThreadType: defaultThreadType,
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

            return null;
        }

        return null;
    }

}
