using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ModelProviderKind>))]
public enum ModelProviderKind
{
    OpenAI = 0,
    Anthropic = 1,
    OpenAICompatible = 2,
    AnthropicCompatible = 3,
    /// <summary>
    /// OpenAI Responses API (/v1/responses). Used for GPT-5.x and newer models
    /// that are natively served via the Responses endpoint.
    /// </summary>
    OpenAIResponses = 4,
}
