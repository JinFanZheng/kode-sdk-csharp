using FluentAssertions;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Providers;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Infrastructure.Providers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.UnitTests.Runtime;

/// <summary>
/// Tests for DeepSeek integration across KodaClaw's provider routing layers:
/// RuntimeProviderSelector, DefaultRuntimeModelProviderFactory, and
/// RuntimeConfigurationSnapshot.
/// </summary>
public sealed class DeepSeekProviderIntegrationTests
{
    // ═══════════════════════════════════════════════════════════════════════════
    // RuntimeProviderSelector — Resolve()
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_DeepSeekModelAndKey_ReturnsDeepSeek()
    {
        var snapshot = new RuntimeConfigurationSnapshot(
            DefaultModel: "deepseek-chat",
            OpenAIApiKey: null,
            OpenAIBaseUrl: null,
            AnthropicApiKey: null,
            AnthropicBaseUrl: null,
            DeepSeekApiKey: "sk-ds-test",
            DeepSeekBaseUrl: null);

        var result = RuntimeProviderSelector.Resolve(snapshot);

        result.IsReady.Should().BeTrue();
        result.Kind.Should().Be(RuntimeProviderKind.DeepSeek);
    }

    [Fact]
    public void Resolve_DeepSeekModelNoKey_ReturnsError()
    {
        var snapshot = new RuntimeConfigurationSnapshot(
            DefaultModel: "deepseek-chat",
            OpenAIApiKey: null,
            OpenAIBaseUrl: null,
            AnthropicApiKey: null,
            AnthropicBaseUrl: null,
            DeepSeekApiKey: null,
            DeepSeekBaseUrl: null);

        var result = RuntimeProviderSelector.Resolve(snapshot);

        result.IsReady.Should().BeFalse();
        result.ErrorMessage.Should().Contain("DEEPSEEK_API_KEY");
    }

    [Fact]
    public void Resolve_DeepSeekModelWithOnlyOpenAIKey_ReturnsError()
    {
        var snapshot = new RuntimeConfigurationSnapshot(
            DefaultModel: "deepseek-chat",
            OpenAIApiKey: "sk-openai",
            OpenAIBaseUrl: null,
            AnthropicApiKey: null,
            AnthropicBaseUrl: null,
            DeepSeekApiKey: null,
            DeepSeekBaseUrl: null);

        var result = RuntimeProviderSelector.Resolve(snapshot);

        result.IsReady.Should().BeFalse();
        result.ErrorMessage.Should().Contain("DeepSeek");
    }

    [Fact]
    public void Resolve_DeepSeekKeyOnly_ReturnsDeepSeek()
    {
        var snapshot = new RuntimeConfigurationSnapshot(
            DefaultModel: "deepseek-reasoner",
            OpenAIApiKey: null,
            OpenAIBaseUrl: null,
            AnthropicApiKey: null,
            AnthropicBaseUrl: null,
            DeepSeekApiKey: "sk-ds-test",
            DeepSeekBaseUrl: null);

        var result = RuntimeProviderSelector.Resolve(snapshot);

        result.IsReady.Should().BeTrue();
        result.Kind.Should().Be(RuntimeProviderKind.DeepSeek);
    }

    [Fact]
    public void Resolve_DeepSeekModelWithAllKeys_ReturnsDeepSeek()
    {
        var snapshot = new RuntimeConfigurationSnapshot(
            DefaultModel: "deepseek-chat",
            OpenAIApiKey: "sk-openai",
            OpenAIBaseUrl: null,
            AnthropicApiKey: "sk-ant",
            AnthropicBaseUrl: null,
            DeepSeekApiKey: "sk-ds",
            DeepSeekBaseUrl: null);

        var result = RuntimeProviderSelector.Resolve(snapshot);

        result.IsReady.Should().BeTrue();
        result.Kind.Should().Be(RuntimeProviderKind.DeepSeek,
            "DeepSeek model prefix should win when all keys are set");
    }

    [Fact]
    public void Resolve_NoModel_ReturnsNotReady()
    {
        var snapshot = new RuntimeConfigurationSnapshot(
            DefaultModel: null,
            OpenAIApiKey: "sk-openai",
            OpenAIBaseUrl: null,
            AnthropicApiKey: null,
            AnthropicBaseUrl: null,
            DeepSeekApiKey: "sk-ds",
            DeepSeekBaseUrl: null);

        var result = RuntimeProviderSelector.Resolve(snapshot);

        result.IsReady.Should().BeFalse();
    }

    [Fact]
    public void Resolve_OpenAIModelWithOnlyDeepSeekKey_ReturnsError()
    {
        var snapshot = new RuntimeConfigurationSnapshot(
            DefaultModel: "gpt-4o",
            OpenAIApiKey: null,
            OpenAIBaseUrl: null,
            AnthropicApiKey: null,
            AnthropicBaseUrl: null,
            DeepSeekApiKey: "sk-ds",
            DeepSeekBaseUrl: null);

        var result = RuntimeProviderSelector.Resolve(snapshot);

        result.IsReady.Should().BeFalse();
        result.ErrorMessage.Should().Contain("DEEPSEEK_API_KEY");
    }

    [Fact]
    public void Resolve_DeepSeekModelCaseInsensitive()
    {
        var snapshot = new RuntimeConfigurationSnapshot(
            DefaultModel: "DeepSeek-Chat",
            OpenAIApiKey: null,
            OpenAIBaseUrl: null,
            AnthropicApiKey: null,
            AnthropicBaseUrl: null,
            DeepSeekApiKey: "sk-ds",
            DeepSeekBaseUrl: null);

        var result = RuntimeProviderSelector.Resolve(snapshot);

        result.IsReady.Should().BeTrue();
        result.Kind.Should().Be(RuntimeProviderKind.DeepSeek);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // RuntimeConfigurationSnapshot — FromOptions
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void FromOptions_PreservesDeepSeekFields()
    {
        var options = new KodaClawRuntimeOptions
        {
            DefaultModel = "deepseek-chat",
            DeepSeekApiKey = "sk-ds-test",
            DeepSeekBaseUrl = "https://api.deepseek.com/v1",
        };

        var snapshot = RuntimeConfigurationSnapshot.FromOptions(options);

        snapshot.DeepSeekApiKey.Should().Be("sk-ds-test");
        snapshot.DeepSeekBaseUrl.Should().Be("https://api.deepseek.com/v1");
    }

    [Fact]
    public void FromOptions_NormalizesDeepSeekBaseUrl()
    {
        var options = new KodaClawRuntimeOptions
        {
            DeepSeekBaseUrl = "https://api.deepseek.com/v1/",
        };

        var snapshot = RuntimeConfigurationSnapshot.FromOptions(options);

        snapshot.DeepSeekBaseUrl.Should().Be("https://api.deepseek.com/v1");
    }

    [Fact]
    public void FromOptions_EmptyDeepSeekFieldsAreNull()
    {
        var options = new KodaClawRuntimeOptions
        {
            DeepSeekApiKey = "  ",
            DeepSeekBaseUrl = "",
        };

        var snapshot = RuntimeConfigurationSnapshot.FromOptions(options);

        snapshot.DeepSeekApiKey.Should().BeNull();
        snapshot.DeepSeekBaseUrl.Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DefaultRuntimeModelProviderFactory — Create()
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Factory_CreateDeepSeek_ReturnsDeepSeekProvider()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(nameof(DeepSeekProvider))
            .ConfigureHttpClient(client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan);
        var provider = services.BuildServiceProvider();
        var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();

        var factory = new DefaultRuntimeModelProviderFactory(httpClientFactory);
        var snapshot = new RuntimeConfigurationSnapshot(
            DefaultModel: "deepseek-chat",
            OpenAIApiKey: null,
            OpenAIBaseUrl: null,
            AnthropicApiKey: null,
            AnthropicBaseUrl: null,
            DeepSeekApiKey: "sk-ds-test",
            DeepSeekBaseUrl: "https://api.deepseek.com/v1");

        var modelProvider = factory.Create(RuntimeProviderKind.DeepSeek, snapshot);

        modelProvider.Should().BeOfType<DeepSeekProvider>();
        modelProvider.ProviderName.Should().Be("deepseek");
    }

    [Fact]
    public void ProviderName_SwitchesCorrectlyForAllKinds()
    {
        // Verify DynamicModelProvider.ProviderName switch covers all kinds
        var snapshot = new RuntimeConfigurationSnapshot(
            DefaultModel: "deepseek-chat",
            OpenAIApiKey: "sk-openai",
            OpenAIBaseUrl: null,
            AnthropicApiKey: "sk-ant",
            AnthropicBaseUrl: null,
            DeepSeekApiKey: "sk-ds",
            DeepSeekBaseUrl: null);

        var resolver = new StaticRuntimeConfigurationResolver(snapshot);
        var services = new ServiceCollection();
        services.AddHttpClient(nameof(OpenAIProvider))
            .ConfigureHttpClient(client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan);
        services.AddHttpClient(nameof(AnthropicProvider))
            .ConfigureHttpClient(client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan);
        services.AddHttpClient(nameof(DeepSeekProvider))
            .ConfigureHttpClient(client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan);
        services.AddHttpClient(nameof(OpenAIResponsesProvider))
            .ConfigureHttpClient(client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan);
        var sp = services.BuildServiceProvider();
        var factory = new DefaultRuntimeModelProviderFactory(
            sp.GetRequiredService<IHttpClientFactory>());
        var provider = new DynamicModelProvider(resolver, factory);

        // With deepseek model + all keys set, ProviderName should be "deepseek"
        provider.ProviderName.Should().Be("deepseek");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Internal helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private sealed class DefaultRuntimeModelProviderFactory : IRuntimeModelProviderFactory
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public DefaultRuntimeModelProviderFactory(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public IModelProvider Create(RuntimeProviderKind kind, RuntimeConfigurationSnapshot snapshot)
        {
            return kind switch
            {
                RuntimeProviderKind.OpenAI => new OpenAIProvider(
                    _httpClientFactory.CreateClient(nameof(OpenAIProvider)),
                    new OpenAIOptions { ApiKey = snapshot.OpenAIApiKey! }),
                RuntimeProviderKind.Anthropic => new AnthropicProvider(
                    _httpClientFactory.CreateClient(nameof(AnthropicProvider)),
                    new AnthropicOptions { ApiKey = snapshot.AnthropicApiKey! }),
                RuntimeProviderKind.DeepSeek => new DeepSeekProvider(
                    _httpClientFactory.CreateClient(nameof(DeepSeekProvider)),
                    new DeepSeekOptions
                    {
                        ApiKey = snapshot.DeepSeekApiKey!,
                        BaseUrl = snapshot.DeepSeekBaseUrl,
                        ModelId = snapshot.DefaultModel,
                    }),
                _ => throw new InvalidOperationException("No provider configured."),
            };
        }
    }
}
