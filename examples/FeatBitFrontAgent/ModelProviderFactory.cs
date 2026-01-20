using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Infrastructure.Providers;

namespace Kode.Agent.WebApiAssistant;

public static class ModelProviderFactory
{
    public static IModelProvider CreateFromConfiguration(
        IConfiguration configuration,
        string provider,
        string? defaultModel = null)
    {
        var normalized = provider.Trim().ToLowerInvariant();
        return normalized switch
        {
            "openai" => CreateOpenAIProvider(configuration, defaultModel),
            _ => CreateAnthropicProvider(configuration, defaultModel)
        };
    }

    public static IModelProvider CreateFromEnvironment(string provider, string? defaultModel = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        return CreateFromConfiguration(configuration, provider, defaultModel);
    }

    private static IModelProvider CreateAnthropicProvider(IConfiguration configuration, string? defaultModel)
    {
        var apiKey = configuration["Kode:Anthropic:ApiKey"]
            ?? configuration["ANTHROPIC_API_KEY"]
            ?? throw new InvalidOperationException("ANTHROPIC_API_KEY environment variable is required");

        var enableBetaRaw = configuration["Kode:Anthropic:EnableBetaFeatures"]
            ?? configuration["ANTHROPIC_ENABLE_BETA"]
            ?? "false";

        var options = new AnthropicOptions
        {
            ApiKey = apiKey,
            BaseUrl = configuration["Kode:Anthropic:BaseUrl"] ?? configuration["ANTHROPIC_BASE_URL"],
            ModelId = configuration["Kode:Anthropic:ModelId"] ?? configuration["ANTHROPIC_MODEL_ID"] ?? defaultModel,
            EnableBetaFeatures = enableBetaRaw.Equals("true", StringComparison.OrdinalIgnoreCase)
        };

        return new AnthropicProvider(options);
    }

    private static IModelProvider CreateOpenAIProvider(IConfiguration configuration, string? defaultModel)
    {
        var apiKey = configuration["Kode:OpenAI:ApiKey"]
            ?? configuration["OPENAI_API_KEY"]
            ?? throw new InvalidOperationException("OPENAI_API_KEY environment variable is required");

        var options = new OpenAIOptions
        {
            ApiKey = apiKey,
            BaseUrl = configuration["Kode:OpenAI:BaseUrl"] ?? configuration["OPENAI_BASE_URL"],
            Organization = configuration["Kode:OpenAI:Organization"] ?? configuration["OPENAI_ORGANIZATION"],
            DefaultModel = configuration["Kode:OpenAI:DefaultModel"] ?? configuration["OPENAI_MODEL_ID"] ?? defaultModel
        };

        return new OpenAIProvider(options);
    }
}
