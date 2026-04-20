using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Workspace;

namespace KodaClaw.ChannelHub.Connectors.Relay;

internal sealed record RelayConnectorConfiguration(
    string AccountId,
    string RelayUrl,
    string? SharedSecret,
    ChannelThreadType DefaultThreadType,
    DeliveryMode? DefaultDeliveryMode = null,
    string? NotifyChannelId = null)
{
    public static async Task<RelayConnectorConfiguration> FromAccountAsync(
        ChannelAccount account,
        ChannelSecretResolver? secretResolver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (string.IsNullOrWhiteSpace(account.ConfigurationJson))
        {
            throw new ArgumentException(
                "Relay connector requires configuration JSON with 'relayUrl'.",
                nameof(account));
        }

        using var document = JsonDocument.Parse(account.ConfigurationJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Channel account configuration must be a JSON object.",
                nameof(account));
        }

        var root = document.RootElement;

        var accountId = GetRequiredString(root, "accountId");

        var relayUrl = GetRequiredString(root, "relayUrl");
        if (string.IsNullOrWhiteSpace(relayUrl))
        {
            throw new ArgumentException(
                "Relay connector configuration must specify 'relayUrl'.",
                nameof(account));
        }

        var sharedSecretFromConfig = GetOptionalString(root, "sharedSecret") ?? GetOptionalString(root, "secret");
        var credentialReferenceFromConfig = GetOptionalString(root, "credentialReference");

        var defaultThreadType = ChannelThreadType.DirectMessage;
        var configuredDefaultThreadType = GetOptionalString(root, "defaultThreadType");
        if (!string.IsNullOrWhiteSpace(configuredDefaultThreadType))
        {
            defaultThreadType = ParseThreadType(configuredDefaultThreadType, defaultThreadType);
        }

        DeliveryMode? defaultDeliveryMode = null;
        var configuredDeliveryMode = GetOptionalString(root, "defaultDeliveryMode");
        if (!string.IsNullOrWhiteSpace(configuredDeliveryMode)
            && Enum.TryParse<DeliveryMode>(configuredDeliveryMode, ignoreCase: true, out var parsed))
        {
            defaultDeliveryMode = parsed;
        }

        var resolvedSecret = await (secretResolver ?? new ChannelSecretResolver()).ResolveAsync(
            sharedSecretFromConfig,
            credentialReferenceFromConfig,
            account.CredentialReference,
            cancellationToken).ConfigureAwait(false);

        var notifyChannelId = GetOptionalString(root, "notifyChannelId");

        return new RelayConnectorConfiguration(
            AccountId: accountId.Trim(),
            RelayUrl: relayUrl.Trim(),
            SharedSecret: resolvedSecret,
            DefaultThreadType: defaultThreadType,
            DefaultDeliveryMode: defaultDeliveryMode,
            NotifyChannelId: notifyChannelId);
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString()?.Trim() ?? string.Empty;
        }

        throw new ArgumentException(
            $"Relay connector configuration missing required field: {propertyName}.");
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString()?.Trim();
        }

        return null;
    }

    private static ChannelThreadType ParseThreadType(string rawValue, ChannelThreadType fallback)
    {
        if (Enum.TryParse<ChannelThreadType>(rawValue, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        var normalized = rawValue.Trim().ToLowerInvariant().Replace("-", string.Empty);
        return normalized switch
        {
            "dm" or "direct" or "directmessage" => ChannelThreadType.DirectMessage,
            "group" or "groupchat" => ChannelThreadType.Group,
            _ => fallback,
        };
    }
}
