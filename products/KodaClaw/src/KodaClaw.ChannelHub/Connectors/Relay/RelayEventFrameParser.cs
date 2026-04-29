using System.Globalization;
using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;

namespace KodaClaw.ChannelHub.Connectors.Relay;

internal static class RelayEventFrameParser
{
    public static bool IsNotificationEventType(string? eventType) =>
        string.Equals(eventType, "NotificationReceived", StringComparison.Ordinal);

    public static bool TryParse(
        string accountId,
        RelayConnectorConfiguration configuration,
        JsonElement root,
        out ChannelEventEnvelope? envelope,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        ArgumentNullException.ThrowIfNull(configuration);

        envelope = null;
        error = null;

        var eventId = GetRequiredString(root, "eventId");
        if (eventId is null)
        {
            error = "Missing required EventFrame field 'eventId'.";
            return false;
        }

        var eventTypeRaw = GetRequiredString(root, "eventType");
        if (eventTypeRaw is null)
        {
            error = "Missing required EventFrame field 'eventType'.";
            return false;
        }

        if (!TryParseEventType(eventTypeRaw, out var eventType))
        {
            error = $"Unsupported Relay eventType '{eventTypeRaw}'.";
            return false;
        }

        var threadTypeRaw = GetRequiredString(root, "threadType");
        if (threadTypeRaw is null)
        {
            error = "Missing required EventFrame field 'threadType'.";
            return false;
        }

        if (!TryParseThreadType(threadTypeRaw, out var threadType))
        {
            error = $"Unsupported Relay threadType '{threadTypeRaw}'.";
            return false;
        }

        var externalThreadId = GetRequiredString(root, "externalThreadId");
        if (externalThreadId is null)
        {
            error = "Missing required EventFrame field 'externalThreadId'.";
            return false;
        }

        var externalMessageId = GetRequiredString(root, "externalMessageId");
        if (externalMessageId is null)
        {
            error = "Missing required EventFrame field 'externalMessageId'.";
            return false;
        }

        var sender = ParseIdentity(root, "sender", required: true, requireDisplayName: true, out error);
        if (sender is null)
        {
            error ??= "Missing required EventFrame field 'sender'.";
            return false;
        }

        var recipient = ParseIdentity(root, "recipient", required: false, requireDisplayName: false, out error);
        if (error is not null)
        {
            return false;
        }

        var occurredAt = GetRequiredDateTimeOffset(root, "occurredAt");
        if (occurredAt is null)
        {
            error = "Missing or invalid EventFrame field 'occurredAt'.";
            return false;
        }

        string? text = null;
        if (eventType is ChannelEventType.MessageReceived or ChannelEventType.MessageEdited)
        {
            text = GetRequiredString(root, "text");
            if (text is null)
            {
                error = "Missing required EventFrame field 'text'.";
                return false;
            }
        }

        var correlationId = GetOptionalString(root, "correlationId");
        var metadataJson = GetOptionalString(root, "metadataJson") ?? root.GetRawText();

        envelope = new ChannelEventEnvelope(
            EventId: eventId,
            EventType: eventType,
            ConnectorKind: ChannelConnectorKind.Relay,
            AccountId: accountId,
            ExternalThreadId: externalThreadId,
            ThreadType: threadType,
            OccurredAt: occurredAt.Value,
            Sender: sender,
            Recipient: recipient,
            ExternalMessageId: externalMessageId,
            Text: text,
            CorrelationId: correlationId,
            MetadataJson: metadataJson,
            DefaultDeliveryMode: configuration.DefaultDeliveryMode);
        return true;
    }

    private static bool TryParseEventType(string rawValue, out ChannelEventType eventType)
    {
        eventType = default;
        return rawValue switch
        {
            nameof(ChannelEventType.MessageReceived) => SetEventType(ChannelEventType.MessageReceived, out eventType),
            nameof(ChannelEventType.MessageEdited) => SetEventType(ChannelEventType.MessageEdited, out eventType),
            _ => false,
        };
    }

    private static bool TryParseThreadType(string rawValue, out ChannelThreadType threadType)
    {
        threadType = default;
        return rawValue switch
        {
            nameof(ChannelThreadType.DirectMessage) => SetThreadType(ChannelThreadType.DirectMessage, out threadType),
            nameof(ChannelThreadType.Group) => SetThreadType(ChannelThreadType.Group, out threadType),
            _ => false,
        };
    }

    private static ChannelIdentity? ParseIdentity(
        JsonElement root,
        string propertyName,
        bool required,
        bool requireDisplayName,
        out string? error)
    {
        error = null;

        if (!root.TryGetProperty(propertyName, out var identityElement) || identityElement.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                error = $"Missing required EventFrame field '{propertyName}'.";
            }

            return null;
        }

        if (identityElement.ValueKind != JsonValueKind.Object)
        {
            error = $"EventFrame field '{propertyName}' must be an object.";
            return null;
        }

        var id = GetRequiredString(identityElement, "id");
        if (id is null)
        {
            error = $"EventFrame field '{propertyName}.id' is required.";
            return null;
        }

        var displayName = GetOptionalString(identityElement, "displayName");
        if (requireDisplayName && displayName is null)
        {
            error = $"EventFrame field '{propertyName}.displayName' is required.";
            return null;
        }

        bool isBot = false;
        if (identityElement.TryGetProperty("isBot", out var isBotProp))
        {
            if (isBotProp.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                error = $"EventFrame field '{propertyName}.isBot' must be a boolean.";
                return null;
            }

            isBot = isBotProp.GetBoolean();
        }

        string? metadataJson = null;
        if (identityElement.TryGetProperty("metadata", out var metadataProp))
        {
            if (metadataProp.ValueKind != JsonValueKind.Object)
            {
                error = $"EventFrame field '{propertyName}.metadata' must be an object.";
                return null;
            }

            metadataJson = metadataProp.GetRawText();
        }

        return new ChannelIdentity(
            Id: id,
            Username: GetOptionalString(identityElement, "username"),
            DisplayName: displayName,
            IsBot: isBot,
            MetadataJson: metadataJson);
    }

    private static string? GetRequiredString(JsonElement element, string propertyName)
    {
        var value = GetOptionalString(element, propertyName);
        return value is null ? null : value;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => Normalize(prop.GetString()),
            _ => null,
        };
    }

    private static DateTimeOffset? GetRequiredDateTimeOffset(JsonElement element, string propertyName)
    {
        var rawValue = GetOptionalString(element, propertyName);
        if (rawValue is null)
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
            rawValue,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
            out var occurredAt))
        {
            return occurredAt;
        }

        return null;
    }

    private static string? Normalize(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static bool SetEventType(ChannelEventType value, out ChannelEventType eventType)
    {
        eventType = value;
        return true;
    }

    private static bool SetThreadType(ChannelThreadType value, out ChannelThreadType threadType)
    {
        threadType = value;
        return true;
    }
}
