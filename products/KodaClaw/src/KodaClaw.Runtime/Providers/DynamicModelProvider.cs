using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Infrastructure.Providers;
using Microsoft.Extensions.Logging;

namespace KodaClaw.Runtime.Providers;

public interface IRuntimeModelProviderFactory
{
    IModelProvider Create(RuntimeProviderKind kind, RuntimeConfigurationSnapshot snapshot);
}

internal sealed class DefaultRuntimeModelProviderFactory : IRuntimeModelProviderFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory? _loggerFactory;

    public DefaultRuntimeModelProviderFactory(
        IHttpClientFactory httpClientFactory,
        ILoggerFactory? loggerFactory = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _loggerFactory = loggerFactory;
    }

    public IModelProvider Create(RuntimeProviderKind kind, RuntimeConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return kind switch
        {
            RuntimeProviderKind.OpenAI => new OpenAIProvider(
                _httpClientFactory.CreateClient(nameof(OpenAIProvider)),
                new OpenAIOptions
                {
                    ApiKey = snapshot.OpenAIApiKey!,
                    BaseUrl = snapshot.OpenAIBaseUrl,
                    CustomHeaders = snapshot.CustomHeaders,
                    RetryPolicy = RetryPolicy.Default,
                    ModelCapabilitiesOverride = snapshot.ModelCapabilitiesOverride,
                },
                _loggerFactory?.CreateLogger<OpenAIProvider>()),
            RuntimeProviderKind.OpenAIResponses => new OpenAIResponsesProvider(
                _httpClientFactory.CreateClient(nameof(OpenAIResponsesProvider)),
                new OpenAIResponsesOptions
                {
                    ApiKey = snapshot.OpenAIApiKey!,
                    BaseUrl = snapshot.OpenAIBaseUrl,
                    CustomHeaders = snapshot.CustomHeaders,
                    RetryPolicy = RetryPolicy.Default,
                    ModelCapabilitiesOverride = snapshot.ModelCapabilitiesOverride,
                },
                _loggerFactory?.CreateLogger<OpenAIResponsesProvider>()),
            RuntimeProviderKind.Anthropic => new AnthropicProvider(
                _httpClientFactory.CreateClient(nameof(AnthropicProvider)),
                new AnthropicOptions
                {
                    ApiKey = snapshot.AnthropicApiKey!,
                    BaseUrl = snapshot.AnthropicBaseUrl,
                    ModelId = snapshot.DefaultModel,
                    CustomHeaders = snapshot.CustomHeaders,
                    RetryPolicy = RetryPolicy.Default,
                    ModelCapabilitiesOverride = snapshot.ModelCapabilitiesOverride,
                },
                _loggerFactory?.CreateLogger<AnthropicProvider>()),
            RuntimeProviderKind.DeepSeek => new DeepSeekProvider(
                _httpClientFactory.CreateClient(nameof(DeepSeekProvider)),
                new DeepSeekOptions
                {
                    ApiKey = snapshot.DeepSeekApiKey!,
                    BaseUrl = snapshot.DeepSeekBaseUrl,
                    ModelId = snapshot.DefaultModel,
                    CustomHeaders = snapshot.CustomHeaders,
                    RetryPolicy = RetryPolicy.Default,
                    ModelCapabilitiesOverride = snapshot.ModelCapabilitiesOverride,
                },
                _loggerFactory?.CreateLogger<DeepSeekProvider>()),
            _ => throw new InvalidOperationException("KodaClaw chat is not configured. Set KODACLAW_DEFAULT_MODEL and one provider API key."),
        };
    }
}

public sealed class DynamicModelProvider : IModelProvider
{
    private readonly IRuntimeConfigurationResolver _resolver;
    private readonly IRuntimeModelProviderFactory _factory;

    public DynamicModelProvider(
        IRuntimeConfigurationResolver resolver,
        IRuntimeModelProviderFactory factory)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public string ProviderName
    {
        get
        {
            var selection = RuntimeProviderSelector.Resolve(_resolver.Resolve());
            return selection.Kind switch
            {
                RuntimeProviderKind.OpenAI => "openai",
                RuntimeProviderKind.OpenAIResponses => "openai-responses",
                RuntimeProviderKind.Anthropic => "anthropic",
                RuntimeProviderKind.DeepSeek => "deepseek",
                _ => "unconfigured",
            };
        }
    }

    public IAsyncEnumerable<StreamChunk> StreamAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        var (provider, snapshot) = CreateProvider();
        return provider.StreamAsync(NormalizeRequest(request, snapshot), cancellationToken);
    }

    public Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        var (provider, snapshot) = CreateProvider();
        return provider.CompleteAsync(NormalizeRequest(request, snapshot), cancellationToken);
    }

    public Task<bool> ValidateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var (provider, _) = CreateProvider();
            return provider.ValidateAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult(false);
        }
    }

    public ModelCapabilities? GetModelCapabilities(string modelId)
    {
        try
        {
            var (provider, _) = CreateProvider();
            return provider.GetModelCapabilities(modelId);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private (IModelProvider Provider, RuntimeConfigurationSnapshot Snapshot) CreateProvider()
    {
        var snapshot = _resolver.Resolve();
        var selection = RuntimeProviderSelector.Resolve(snapshot);
        if (!selection.IsReady)
        {
            throw new InvalidOperationException(selection.ErrorMessage);
        }

        return (_factory.Create(selection.Kind, snapshot), snapshot);
    }

    private static ModelRequest NormalizeRequest(
        ModelRequest request,
        RuntimeConfigurationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(snapshot.DefaultModel) ||
            string.Equals(request.Model, snapshot.DefaultModel, StringComparison.Ordinal))
        {
            return request;
        }

        // Intentional: DynamicModelProvider is a single-model env-var path.
        // It always overrides request.Model with the configured default.
        // For multi-model or capability-aware routing, use RegistryAwareModelProvider
        // with a populated model registry.
        return request with
        {
            Model = snapshot.DefaultModel,
        };
    }
}
