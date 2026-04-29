using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;

namespace KodaClaw.ChannelHub.Connectors.WeChat;

internal sealed record WeChatConnectorConfiguration(
    string BotToken,
    string StateDir,
    DeliveryMode? DefaultDeliveryMode = null)
{
    public static async Task<WeChatConnectorConfiguration> FromAccountAsync(
        ChannelAccount account,
        string workspaceRootPath,
        ChannelSecretResolver? secretResolver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.ConnectorKind != ChannelConnectorKind.WeChat)
        {
            throw new ArgumentException(
                $"WeChat connector cannot start account with connector kind '{account.ConnectorKind}'.",
                nameof(account));
        }

        var botTokenFromConfig = default(string);
        var credentialReferenceFromConfig = default(string);
        DeliveryMode? defaultDeliveryMode = null;

        if (!string.IsNullOrWhiteSpace(account.ConfigurationJson))
        {
            using var document = JsonDocument.Parse(account.ConfigurationJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("WeChat account configuration must be a JSON object.", nameof(account));
            }

            var root = document.RootElement;
            botTokenFromConfig = GetOptionalString(root, "botToken");
            credentialReferenceFromConfig = GetOptionalString(root, "credentialReference");

            var configuredDeliveryMode = GetOptionalString(root, "defaultDeliveryMode");
            if (!string.IsNullOrWhiteSpace(configuredDeliveryMode)
                && Enum.TryParse<DeliveryMode>(configuredDeliveryMode, ignoreCase: true, out var parsed))
            {
                defaultDeliveryMode = parsed;
            }
        }

        var resolver = secretResolver ?? new ChannelSecretResolver();
        var botToken = await resolver.ResolveAsync(
            botTokenFromConfig,
            credentialReferenceFromConfig,
            account.CredentialReference,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(botToken))
        {
            throw new ArgumentException(
                "WeChat bot_token is required and must resolve from configuration or credential reference.",
                nameof(account));
        }

        var stateDir = Path.Combine(workspaceRootPath, "state", "wechat", account.Id);

        return new WeChatConnectorConfiguration(
            BotToken: botToken,
            StateDir: stateDir,
            DefaultDeliveryMode: defaultDeliveryMode);
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (property.Value.ValueKind == JsonValueKind.String)
                return ChannelHubValidation.NormalizeNullableText(property.Value.GetString());
        }

        return null;
    }

}
