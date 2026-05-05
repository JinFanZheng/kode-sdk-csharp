using KodaClaw.Contracts.Models;

namespace KodaClaw.Runtime.Providers;

public enum RuntimeProviderKind
{
    None,
    OpenAI,
    Anthropic,
    OpenAIResponses,
    DeepSeek,
}

internal sealed record RuntimeProviderSelection(
    RuntimeProviderKind Kind,
    string? ErrorMessage = null)
{
    public bool IsReady => string.IsNullOrWhiteSpace(ErrorMessage);
}

internal static class RuntimeProviderSelector
{
    public static RuntimeProviderSelection Resolve(RuntimeConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var hasOpenAi = !string.IsNullOrWhiteSpace(snapshot.OpenAIApiKey);
        var hasAnthropic = !string.IsNullOrWhiteSpace(snapshot.AnthropicApiKey);
        var hasDeepSeek = !string.IsNullOrWhiteSpace(snapshot.DeepSeekApiKey);
        var model = snapshot.DefaultModel;

        if (string.IsNullOrWhiteSpace(model))
        {
            return new RuntimeProviderSelection(
                RuntimeProviderKind.None,
                "KodaClaw chat is not configured. Set KODACLAW_DEFAULT_MODEL and one provider API key.");
        }

        if (!hasOpenAi && !hasAnthropic && !hasDeepSeek)
        {
            return new RuntimeProviderSelection(
                RuntimeProviderKind.None,
                "KodaClaw chat is not configured. Set OPENAI_API_KEY, ANTHROPIC_API_KEY or DEEPSEEK_API_KEY.");
        }

        var looksLikeOpenAi = LooksLikeOpenAiModel(model);
        var looksLikeAnthropic = LooksLikeAnthropicModel(model);
        var looksLikeDeepSeek = LooksLikeDeepSeekModel(model);

        // DeepSeek-first: if model looks like DeepSeek and key is set
        if (looksLikeDeepSeek && hasDeepSeek)
            return new RuntimeProviderSelection(RuntimeProviderKind.DeepSeek);

        // DeepSeek model with wrong key
        if (looksLikeDeepSeek && !hasDeepSeek)
        {
            if (hasOpenAi || hasAnthropic)
            {
                return new RuntimeProviderSelection(
                    RuntimeProviderKind.None,
                    "KODACLAW_DEFAULT_MODEL looks like a DeepSeek model, but only OPENAI_API_KEY/ANTHROPIC_API_KEY is configured. " +
                    "Set DEEPSEEK_API_KEY for DeepSeek models, or use KODACLAW_DEFAULT_MODEL with a matching provider model.");
            }
        }

        if (hasOpenAi && !hasAnthropic && !hasDeepSeek)
        {
            if (looksLikeAnthropic || looksLikeDeepSeek)
            {
                return new RuntimeProviderSelection(
                    RuntimeProviderKind.None,
                    "KODACLAW_DEFAULT_MODEL looks like an Anthropic or DeepSeek model, but only OPENAI_API_KEY is configured.");
            }

            return new RuntimeProviderSelection(RuntimeProviderKind.OpenAI);
        }

        if (hasAnthropic && !hasOpenAi && !hasDeepSeek)
        {
            if (looksLikeOpenAi || looksLikeDeepSeek)
            {
                return new RuntimeProviderSelection(
                    RuntimeProviderKind.None,
                    "KODACLAW_DEFAULT_MODEL looks like an OpenAI or DeepSeek model, but only ANTHROPIC_API_KEY is configured.");
            }

            return new RuntimeProviderSelection(RuntimeProviderKind.Anthropic);
        }

        if (hasDeepSeek && !hasOpenAi && !hasAnthropic)
        {
            if (looksLikeOpenAi || looksLikeAnthropic)
            {
                return new RuntimeProviderSelection(
                    RuntimeProviderKind.None,
                    "KODACLAW_DEFAULT_MODEL looks like an OpenAI or Anthropic model, but only DEEPSEEK_API_KEY is configured.");
            }

            return new RuntimeProviderSelection(RuntimeProviderKind.DeepSeek);
        }

        // Multiple keys set — disambiguate by model name
        if (looksLikeAnthropic)
            return new RuntimeProviderSelection(RuntimeProviderKind.Anthropic);
        if (looksLikeOpenAi)
            return new RuntimeProviderSelection(RuntimeProviderKind.OpenAI);
        if (looksLikeDeepSeek)
            return new RuntimeProviderSelection(RuntimeProviderKind.DeepSeek);

        return new RuntimeProviderSelection(
            RuntimeProviderKind.None,
            "Multiple API keys are configured. Use KODACLAW_DEFAULT_MODEL to disambiguate the provider.");
    }

    public static string ResolveModelOrThrow(
        IRuntimeConfigurationResolver? resolver,
        string? fallbackModel)
    {
        if (resolver is null)
        {
            if (!string.IsNullOrWhiteSpace(fallbackModel))
            {
                return fallbackModel.Trim();
            }

            throw new InvalidOperationException(
                "KodaClaw chat is not configured. Set KODACLAW_DEFAULT_MODEL and one provider API key.");
        }

        var snapshot = resolver.Resolve();
        var selection = Resolve(snapshot);
        if (!selection.IsReady)
        {
            throw new InvalidOperationException(selection.ErrorMessage);
        }

        return snapshot.DefaultModel!;
    }

    /// <summary>
    /// Like ResolveModelOrThrow, but falls back to the default account model's
    /// ModelId when env-var config is absent. Use this in session services to support
    /// account-first model routing without requiring environment variables.
    /// </summary>
    public static async Task<string> ResolveModelOrFallbackAsync(
        IRuntimeConfigurationResolver? resolver,
        string? fallbackModel,
        IProviderAccountRepository? accountRepo,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return ResolveModelOrThrow(resolver, fallbackModel);
        }
        catch (InvalidOperationException) when (accountRepo is not null)
        {
            var resolved = await accountRepo.ResolveDefaultForAsync(
                ModelCapabilitySet.Text,
                cancellationToken);

            if (resolved is not null && !string.IsNullOrWhiteSpace(resolved.Model.ModelId))
                return resolved.Model.ModelId;

            throw;
        }
    }

    private static bool LooksLikeOpenAiModel(string model)
    {
        return model.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o4", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeAnthropicModel(string model)
    {
        return model.StartsWith("claude", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeDeepSeekModel(string model)
    {
        return model.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase);
    }
}
