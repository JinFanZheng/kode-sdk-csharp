using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Infrastructure.Providers;

namespace Kode.Agent.Boilerplate;

/// <summary>
/// Factory for creating IModelProvider instances from configuration
/// </summary>
public static class ModelProviderFactory
{
    /// <summary>
    /// Create a model provider from IConfiguration
    /// </summary>
    public static IModelProvider CreateFromConfiguration(
        IConfiguration configuration,
        string provider,
        string? defaultModel = null)
    {
        var normalized = provider.Trim().ToLowerInvariant();
        return normalized switch
        {
            "openai" => CreateOpenAIProvider(configuration, defaultModel),
            "anthropic" => CreateAnthropicProvider(configuration, defaultModel),
            _ => throw new InvalidOperationException($"Unknown provider: {provider}. Supported: anthropic, openai")
        };
    }

    private static IModelProvider CreateAnthropicProvider(IConfiguration configuration, string? defaultModel)
    {
        var apiKey = configuration["Anthropic:ApiKey"]
            ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? throw new InvalidOperationException(
                "Anthropic API Key is required. Set it in appsettings.json (Anthropic:ApiKey) or environment variable (ANTHROPIC_API_KEY)");

        var enableBetaRaw = configuration["Anthropic:EnableBetaFeatures"] 
            ?? Environment.GetEnvironmentVariable("ANTHROPIC_ENABLE_BETA") 
            ?? "false";

        var options = new AnthropicOptions
        {
            ApiKey = apiKey,
            BaseUrl = configuration["Anthropic:BaseUrl"] 
                ?? Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL"),
            ModelId = configuration["Anthropic:ModelId"] 
                ?? Environment.GetEnvironmentVariable("ANTHROPIC_MODEL_ID") 
                ?? defaultModel,
            EnableBetaFeatures = enableBetaRaw.Equals("true", StringComparison.OrdinalIgnoreCase)
        };

        return new AnthropicProvider(options);
    }

    private static IModelProvider CreateOpenAIProvider(IConfiguration configuration, string? defaultModel)
    {
        var apiKey = configuration["OpenAI:ApiKey"]
            ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new InvalidOperationException(
                "OpenAI API Key is required. Set it in appsettings.json (OpenAI:ApiKey) or environment variable (OPENAI_API_KEY)");

        var options = new OpenAIOptions
        {
            ApiKey = apiKey,
            BaseUrl = configuration["OpenAI:BaseUrl"] 
                ?? Environment.GetEnvironmentVariable("OPENAI_BASE_URL"),
            Organization = configuration["OpenAI:Organization"] 
                ?? Environment.GetEnvironmentVariable("OPENAI_ORGANIZATION"),
            DefaultModel = configuration["OpenAI:DefaultModel"] 
                ?? Environment.GetEnvironmentVariable("OPENAI_MODEL_ID") 
                ?? defaultModel
        };

        return new OpenAIProvider(options);
    }
}
