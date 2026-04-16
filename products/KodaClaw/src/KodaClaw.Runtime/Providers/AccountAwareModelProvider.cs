using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Infrastructure.Providers;
using KodaClaw.Contracts;
using KodaClaw.ModelHub;

namespace KodaClaw.Runtime;

/// <summary>
/// Account-first IModelProvider: resolves the appropriate model from
/// IProviderAccountRepository based on required capabilities, then constructs
/// the matching LLM provider. Falls back to DynamicModelProvider when the
/// repository is empty.
/// </summary>
public sealed class AccountAwareModelProvider : IModelProvider
{
    private const string DiagnosticSource = "account_aware_model_provider";

    private readonly IProviderAccountRepository _repo;
    private readonly ISecretStore _secretStore;
    private readonly IRuntimeModelProviderFactory _factory;
    private readonly DynamicModelProvider _fallback;
    private readonly IDiagnosticsService? _diagnosticsService;

    // Cache providers by accountId:apiKey so all models under the same account
    // share one HttpClient/connection pool.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IModelProvider> _providerCache = new();

    public AccountAwareModelProvider(
        IProviderAccountRepository repo,
        ISecretStore secretStore,
        IRuntimeModelProviderFactory factory,
        DynamicModelProvider fallback,
        IDiagnosticsService? diagnosticsService = null)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _diagnosticsService = diagnosticsService;
    }

    public string ProviderName => "account";

    public async IAsyncEnumerable<StreamChunk> StreamAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (provider, normalizedRequest) = await ResolveAsync(request, cancellationToken);
        await foreach (var chunk in provider.StreamAsync(normalizedRequest, cancellationToken))
            yield return chunk;
    }

    public async Task<ModelResponse> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        var (provider, normalizedRequest) = await ResolveAsync(request, cancellationToken);
        return await provider.CompleteAsync(normalizedRequest, cancellationToken);
    }

    public async Task<bool> ValidateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var resolved = await _repo.ResolveDefaultForAsync(
                ModelCapabilitySet.Text, cancellationToken);

            if (resolved is not null)
            {
                var provider = await BuildProviderAsync(resolved.Account, cancellationToken);
                return await provider.ValidateAsync(cancellationToken);
            }
        }
        catch (InvalidOperationException)
        {
            // Account path not ready — fall through to env-var fallback
        }

        return await _fallback.ValidateAsync(cancellationToken);
    }

    // ── internals ─────────────────────────────────────────────────────────────

    private async Task<(IModelProvider Provider, ModelRequest Request)> ResolveAsync(
        ModelRequest request,
        CancellationToken cancellationToken)
    {
        var required = InferRequiredCapabilities(request);
        var resolved = await _repo.ResolveDefaultForAsync(required, cancellationToken);

        // Capability-degraded fallback
        if (resolved is null && required != ModelCapabilitySet.Text)
        {
            resolved = await _repo.ResolveDefaultForAsync(
                ModelCapabilitySet.Text, cancellationToken);

            if (resolved is not null)
            {
                var stripped = StripUnsupportedContent(request, resolved.Model.Capabilities);
                if (stripped != request)
                {
                    RecordDegradationEvent(resolved, required, resolved.Model.Capabilities);
                    request = stripped;
                }
            }
        }

        if (resolved is not null)
        {
            var provider = await BuildProviderAsync(resolved.Account, cancellationToken);
            var normalized = NormalizeRequest(request, resolved.Model);
            return (provider, normalized);
        }

        // No account entry — delegate to env-var DynamicModelProvider
        return (_fallback, request);
    }

    private async Task<IModelProvider> BuildProviderAsync(
        ProviderAccount account,
        CancellationToken cancellationToken)
    {
        var apiKey = await ResolveApiKeyAsync(account, cancellationToken);

        var cacheKey = $"{account.Id}:{apiKey}";
        if (_providerCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var (providerKind, snapshot) = account.ProviderKind switch
        {
            ModelProviderKind.Anthropic or ModelProviderKind.AnthropicCompatible =>
                (RuntimeProviderKind.Anthropic, new RuntimeConfigurationSnapshot(
                    DefaultModel: null,
                    OpenAIApiKey: null,
                    OpenAIBaseUrl: null,
                    AnthropicApiKey: apiKey,
                    AnthropicBaseUrl: account.BaseUrl,
                    CustomHeaders: account.CustomHeaders)),

            ModelProviderKind.OpenAI or ModelProviderKind.OpenAICompatible =>
                (RuntimeProviderKind.OpenAI, new RuntimeConfigurationSnapshot(
                    DefaultModel: null,
                    OpenAIApiKey: apiKey,
                    OpenAIBaseUrl: account.BaseUrl,
                    AnthropicApiKey: null,
                    AnthropicBaseUrl: null,
                    CustomHeaders: account.CustomHeaders)),

            ModelProviderKind.OpenAIResponses =>
                (RuntimeProviderKind.OpenAIResponses, new RuntimeConfigurationSnapshot(
                    DefaultModel: null,
                    OpenAIApiKey: apiKey,
                    OpenAIBaseUrl: account.BaseUrl,
                    AnthropicApiKey: null,
                    AnthropicBaseUrl: null,
                    CustomHeaders: account.CustomHeaders)),

            _ => throw new InvalidOperationException(
                $"Unsupported provider kind '{account.ProviderKind}' for account '{account.Id}'."),
        };

        var provider = _factory.Create(providerKind, snapshot);
        return _providerCache.GetOrAdd(cacheKey, provider);
    }

    private async Task<string> ResolveApiKeyAsync(
        ProviderAccount account,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(account.ApiKeySecretRef) &&
            SecretRef.TryParse(account.ApiKeySecretRef, out var secretRef))
        {
            var secret = await _secretStore.GetAsync(secretRef, cancellationToken);
            if (!string.IsNullOrWhiteSpace(secret))
                return secret;
        }

        if (!string.IsNullOrWhiteSpace(account.ApiKeyEnvironmentVariable))
        {
            var envVal = Environment.GetEnvironmentVariable(account.ApiKeyEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(envVal))
                return envVal;
        }

        throw new InvalidOperationException(
            $"No API key configured for provider account '{account.DisplayName}'. " +
            "Configure a key in Models settings or set the corresponding environment variable.");
    }

    private static ModelRequest NormalizeRequest(ModelRequest request, AccountModel model)
    {
        var normalized = request;

        if (!string.IsNullOrWhiteSpace(model.ModelId) &&
            !string.Equals(request.Model, model.ModelId, StringComparison.Ordinal))
        {
            normalized = normalized with { Model = model.ModelId };
        }

        if (model.MaxOutputTokens > 0 && normalized.MaxTokens is null)
        {
            normalized = normalized with { MaxTokens = model.MaxOutputTokens };
        }

        if (!model.SupportsToolCalling && normalized.Tools is { Count: > 0 })
        {
            throw new InvalidOperationException(
                $"Model '{model.DisplayName}' (id: {model.Id}) does not support " +
                "tool calling. KodaClaw sessions require tool calling. " +
                "Please configure a model that supports function calling.");
        }

        return normalized;
    }

    private static ModelCapabilitySet InferRequiredCapabilities(ModelRequest request)
    {
        var required = ModelCapabilitySet.Text;
        foreach (var message in request.Messages)
        {
            foreach (var block in message.Content)
            {
                required |= block switch
                {
                    ImageContent => ModelCapabilitySet.Image,
                    VideoContent => ModelCapabilitySet.Video,
                    FileContent  => ModelCapabilitySet.File,
                    _            => ModelCapabilitySet.None,
                };
            }
        }
        return required;
    }

    private static ModelRequest StripUnsupportedContent(
        ModelRequest request,
        ModelCapabilitySet supported)
    {
        var messages = request.Messages
            .Select(msg =>
            {
                var filtered = msg.Content
                    .Where(block => block switch
                    {
                        ImageContent => supported.HasFlag(ModelCapabilitySet.Image),
                        VideoContent => supported.HasFlag(ModelCapabilitySet.Video),
                        FileContent  => supported.HasFlag(ModelCapabilitySet.File),
                        _            => true,
                    })
                    .ToList();

                return (IReadOnlyList<ContentBlock>)filtered == msg.Content
                    ? msg
                    : msg with { Content = filtered };
            })
            .ToList();

        return request with { Messages = messages };
    }

    private void RecordDegradationEvent(
        ResolvedModel resolved,
        ModelCapabilitySet requested,
        ModelCapabilitySet available)
    {
        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: DiagnosticSource,
            EventType: "multimodal_content_degraded",
            Level: "Warning",
            Message: $"No model found for capabilities '{requested}'. " +
                     $"Degraded to '{resolved.Model.DisplayName}' with capabilities '{available}'. " +
                     "Unsupported content blocks (image/video/file) have been stripped from the request.",
            Timestamp: DateTimeOffset.UtcNow,
            Attributes: new Dictionary<string, string?>
            {
                ["accountId"]          = resolved.Account.Id,
                ["modelId"]            = resolved.Model.Id,
                ["modelDisplayName"]   = resolved.Model.DisplayName,
                ["requestedCapabilities"] = requested.ToString(),
                ["availableCapabilities"] = available.ToString(),
            }));
    }
}
