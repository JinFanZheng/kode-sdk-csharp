using System.Globalization;
using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;

namespace KodaClaw.ChannelHub.Connectors.Webhook;

internal static class GenericWebhookPayloadParser
{
    public static ChannelEventEnvelope Parse(
        ChannelAccount account,
        string payloadJson,
        ChannelThreadType defaultThreadType)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            throw new ArgumentException("Webhook payload is required.", nameof(payloadJson));
        }

        using var document = JsonDocument.Parse(payloadJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Webhook payload must be a JSON object.", nameof(payloadJson));
        }

        var root = document.RootElement;
        var eventTypeRaw = GetRequiredString(root, "eventType", "event", "type");
        var eventType = ParseEventType(eventTypeRaw);

        var threadTypeRaw = GetOptionalString(root, "threadType", "chatType");
        var threadType = ParseThreadType(threadTypeRaw, defaultThreadType);

        var externalThreadId = GetOptionalString(root, "externalThreadId", "threadId", "chatId", "conversationId");
        if (string.IsNullOrWhiteSpace(externalThreadId))
        {
            externalThreadId = eventType is ChannelEventType.AccountConnected or ChannelEventType.AccountDisconnected
                ? $"account:{account.Id}"
                : throw new ArgumentException("Webhook payload must contain externalThreadId/threadId/chatId/conversationId.");
        }

        var eventId = GetOptionalString(root, "eventId", "id")
            ?? $"webhook-{Guid.NewGuid():N}";
        var occurredAt = GetOptionalDateTimeOffset(root, "occurredAt", "timestamp", "createdAt")
            ?? DateTimeOffset.UtcNow;
        var sender = ParseIdentity(GetOptionalObject(root, "sender", "from", "user"));
        var recipient = ParseIdentity(GetOptionalObject(root, "recipient", "to"));

        return new ChannelEventEnvelope(
            EventId: eventId,
            EventType: eventType,
            ConnectorKind: ChannelConnectorKind.GenericWebhook,
            AccountId: account.Id,
            ExternalThreadId: externalThreadId,
            ThreadType: threadType,
            OccurredAt: occurredAt,
            Sender: sender,
            Recipient: recipient,
            ExternalMessageId: GetOptionalString(root, "externalMessageId", "messageId"),
            Text: GetOptionalString(root, "text", "message", "content"),
            CorrelationId: GetOptionalString(root, "correlationId", "traceId"),
            MetadataJson: root.GetRawText());
    }

    public static ChannelThreadType ParseThreadType(string? rawValue, ChannelThreadType fallback)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return fallback;
        }

        if (Enum.TryParse<ChannelThreadType>(rawValue, ignoreCase: true, out var parsedEnum))
        {
            return parsedEnum;
        }

        var normalized = NormalizeToken(rawValue);
        return normalized switch
        {
            "dm" => ChannelThreadType.DirectMessage,
            "direct" => ChannelThreadType.DirectMessage,
            "direct.message" => ChannelThreadType.DirectMessage,
            "directmessage" => ChannelThreadType.DirectMessage,
            "group" => ChannelThreadType.Group,
            "group.chat" => ChannelThreadType.Group,
            "groupchat" => ChannelThreadType.Group,
            _ => throw new ArgumentException($"Unsupported webhook thread type '{rawValue}'.", nameof(rawValue)),
        };
    }

    private static ChannelEventType ParseEventType(string rawValue)
    {
        if (Enum.TryParse<ChannelEventType>(rawValue, ignoreCase: true, out var parsedEnum))
        {
            return parsedEnum;
        }

        var normalized = NormalizeToken(rawValue);
        return normalized switch
        {
            "message.received" => ChannelEventType.MessageReceived,
            "messagereceived" => ChannelEventType.MessageReceived,
            "message.edited" => ChannelEventType.MessageEdited,
            "messageedited" => ChannelEventType.MessageEdited,
            "message.deleted" => ChannelEventType.MessageDeleted,
            "messagedeleted" => ChannelEventType.MessageDeleted,
            "reaction.received" => ChannelEventType.ReactionReceived,
            "reactionreceived" => ChannelEventType.ReactionReceived,
            "account.connected" => ChannelEventType.AccountConnected,
            "accountconnected" => ChannelEventType.AccountConnected,
            "account.disconnected" => ChannelEventType.AccountDisconnected,
            "accountdisconnected" => ChannelEventType.AccountDisconnected,
            "delivery.failed" => ChannelEventType.DeliveryFailed,
            "deliveryfailed" => ChannelEventType.DeliveryFailed,
            _ => throw new ArgumentException($"Unsupported webhook event type '{rawValue}'.", nameof(rawValue)),
        };
    }

    private static ChannelIdentity? ParseIdentity(JsonElement? identityElement)
    {
        if (!identityElement.HasValue || identityElement.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var element = identityElement.Value;
        var id = GetOptionalString(element, "id", "userId", "externalUserId");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var metadata = GetOptionalObject(element, "metadata");
        return new ChannelIdentity(
            Id: id,
            Username: GetOptionalString(element, "username", "handle"),
            DisplayName: GetOptionalString(element, "displayName", "name"),
            IsBot: GetOptionalBoolean(element, "isBot") ?? false,
            MetadataJson: metadata?.GetRawText());
    }

    private static string GetRequiredString(JsonElement element, params string[] candidateNames)
    {
        return GetOptionalString(element, candidateNames)
            ?? throw new ArgumentException($"Webhook payload missing required field: {candidateNames[0]}.");
    }

    private static string? GetOptionalString(JsonElement element, params string[] candidateNames)
    {
        foreach (var candidateName in candidateNames)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!property.Name.Equals(candidateName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => ChannelHubValidation.NormalizeNullableText(property.Value.GetString()),
                    JsonValueKind.Number => property.Value.ToString(),
                    JsonValueKind.True => bool.TrueString,
                    JsonValueKind.False => bool.FalseString,
                    _ => null,
                };
            }
        }

        return null;
    }

    private static DateTimeOffset? GetOptionalDateTimeOffset(JsonElement element, params string[] candidateNames)
    {
        var raw = GetOptionalString(element, candidateNames);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
            out var value))
        {
            return value;
        }

        throw new ArgumentException($"Webhook timestamp '{raw}' is invalid.");
    }

    private static bool? GetOptionalBoolean(JsonElement element, params string[] candidateNames)
    {
        foreach (var candidateName in candidateNames)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!property.Name.Equals(candidateName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return property.Value.GetBoolean();
                }

                if (property.Value.ValueKind == JsonValueKind.String &&
                    bool.TryParse(property.Value.GetString(), out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static JsonElement? GetOptionalObject(JsonElement element, params string[] candidateNames)
    {
        foreach (var candidateName in candidateNames)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!property.Name.Equals(candidateName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    return property.Value;
                }

                return null;
            }
        }

        return null;
    }

    private static string NormalizeToken(string value)
    {
        return value
            .Trim()
            .ToLowerInvariant()
            .Replace("_", ".", StringComparison.Ordinal)
            .Replace("-", ".", StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
    }

}
