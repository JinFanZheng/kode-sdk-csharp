using System.Text.Json;
using Kode.Agent.Sdk.Core.Types;

namespace Kode.Agent.WebApiAssistant.Assistant.Hooks.Utils;

/// <summary>
/// Utility methods for working with messages.
/// </summary>
public static class MessageUtils
{
    /// <summary>
    /// Extract the last user text from a list of messages.
    /// </summary>
    public static string GetLastUserTextFromMessages(IReadOnlyList<Message> messages)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (msg.Role != MessageRole.User) continue;

            var text = ExtractTextFromContent(msg.Content);
            if (!string.IsNullOrWhiteSpace(text))
            {
                // Skip system reminders
                if (!text.StartsWith("<system-reminder>"))
                {
                    return text.Trim();
                }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Extract text from message content (which can be a string or a list of content blocks).
    /// </summary>
    private static string ExtractTextFromContent(object? content)
    {
        if (content == null) return string.Empty;

        if (content is string text)
        {
            return text;
        }

        // Handle content blocks (array of objects with type and text properties)
        if (content is IEnumerable<object> blocks)
        {
            var texts = new List<string>();
            foreach (var block in blocks)
            {
                if (block is JsonElement element)
                {
                    if (element.ValueKind == JsonValueKind.Object &&
                        element.TryGetProperty("type", out var type) &&
                        type.GetString() == "text" &&
                        element.TryGetProperty("text", out var textProp))
                    {
                        texts.Add(textProp.GetString() ?? string.Empty);
                    }
                }
            }
            return string.Join("\n", texts);
        }

        return string.Empty;
    }

    /// <summary>
    /// Get the last user text from an agent instance.
    /// </summary>
    public static string GetLastUserTextFromAgent(object? agent)
    {
        try
        {
            // Try to get messages from the agent
            var messagesProperty = agent?.GetType().GetProperty("Messages");
            if (messagesProperty != null)
            {
                if (messagesProperty.GetValue(agent) is IReadOnlyList<Message> messages)
                {
                    return GetLastUserTextFromMessages(messages);
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        return string.Empty;
    }
}
