using Kode.Agent.Sdk.Infrastructure.Providers;
using Kode.Agent.Sdk.Infrastructure.Sandbox;
using Kode.Agent.Sdk.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kode.Agent.Sdk.Extensions;

/// <summary>
/// Extension methods for registering Agent SDK services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the core Agent SDK services.
    /// </summary>
    public static IServiceCollection AddAgentSdk(this IServiceCollection services)
    {
        // Tool registry as singleton
        services.TryAddSingleton<IToolRegistry, ToolRegistry>();

        // Sandbox factory
        services.TryAddSingleton<ISandboxFactory, DefaultSandboxFactory>();

        return services;
    }

    /// <summary>
    /// Adds the core Agent SDK services with configuration.
    /// </summary>
    public static IServiceCollection AddAgentSdk(this IServiceCollection services, Action<AgentSdkOptions> configure)
    {
        var options = new AgentSdkOptions();
        configure(options);

        services.AddAgentSdk();

        // Configure based on options
        if (options.WorkingDirectory != null)
        {
            services.AddSingleton<ISandboxFactory>(_ => new DefaultSandboxFactory());
        }

        return services;
    }

    /// <summary>
    /// Adds the Anthropic model provider.
    /// </summary>
    public static IServiceCollection AddAnthropicProvider(this IServiceCollection services, Action<AnthropicOptions> configure)
    {
        var options = new AnthropicOptions { ApiKey = "" };
        configure(options);

        services.AddHttpClient<IModelProvider, AnthropicProvider>((sp, client) =>
        {
            // Disable default 100s timeout; streaming calls are bounded by CancellationToken instead.
            client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler());

        services.AddSingleton(options);
        services.AddSingleton<IModelProvider>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient(nameof(AnthropicProvider));
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<AnthropicProvider>>();
            return new AnthropicProvider(httpClient, options, logger);
        });

        return services;
    }

    /// <summary>
    /// Adds the OpenAI model provider.
    /// </summary>
    public static IServiceCollection AddOpenAIProvider(this IServiceCollection services, Action<OpenAIOptions> configure)
    {
        var options = new OpenAIOptions { ApiKey = "" };
        configure(options);

        services.AddHttpClient<IModelProvider, OpenAIProvider>()
            .ConfigureHttpClient(client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan);
        services.AddSingleton(options);
        services.AddSingleton<IModelProvider>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient(nameof(OpenAIProvider));
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<OpenAIProvider>>();
            return new OpenAIProvider(httpClient, options, logger);
        });

        return services;
    }

    /// <summary>
    /// Adds the OpenAI Responses API model provider.
    /// </summary>
    public static IServiceCollection AddOpenAIResponsesProvider(this IServiceCollection services, Action<OpenAIResponsesOptions> configure)
    {
        var options = new OpenAIResponsesOptions { ApiKey = "" };
        configure(options);

        services.AddHttpClient<IModelProvider, OpenAIResponsesProvider>()
            .ConfigureHttpClient(client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan);
        services.AddSingleton(options);
        services.AddSingleton<IModelProvider>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient(nameof(OpenAIResponsesProvider));
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<OpenAIResponsesProvider>>();
            return new OpenAIResponsesProvider(httpClient, options, logger);
        });

        return services;
    }

    /// <summary>
    /// Adds the DeepSeek model provider.
    /// </summary>
    public static IServiceCollection AddDeepSeekProvider(this IServiceCollection services, Action<DeepSeekOptions> configure)
    {
        var options = new DeepSeekOptions { ApiKey = "" };
        configure(options);

        services.AddHttpClient<IModelProvider, DeepSeekProvider>()
            .ConfigureHttpClient(client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan);
        services.AddSingleton(options);
        services.AddSingleton<IModelProvider>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient(nameof(DeepSeekProvider));
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<DeepSeekProvider>>();
            return new DeepSeekProvider(httpClient, options, logger);
        });

        return services;
    }

    /// <summary>
    /// Registers a tool with the tool registry.
    /// </summary>
    public static IServiceCollection AddTool<TTool>(this IServiceCollection services) where TTool : class, ITool, new()
    {
        services.AddSingleton<ITool, TTool>();
        return services;
    }

    /// <summary>
    /// Registers a toolkit with the tool registry.
    /// </summary>
    public static IServiceCollection AddToolKit<TToolKit>(this IServiceCollection services) where TToolKit : ToolKit, new()
    {
        // Register the toolkit itself
        services.AddSingleton<TToolKit>();

        // Register all tools from the toolkit
        services.AddSingleton<IEnumerable<ITool>>(sp =>
        {
            var toolkit = sp.GetRequiredService<TToolKit>();
            toolkit.Initialize();
            return toolkit.Tools;
        });

        return services;
    }

    /// <summary>
    /// Builds the AgentDependencies from the service provider.
    /// </summary>
    public static Core.Types.AgentDependencies BuildAgentDependencies(this IServiceProvider services)
    {
        return new Core.Types.AgentDependencies
        {
            Store = services.GetRequiredService<IAgentStore>(),
            SandboxFactory = services.GetRequiredService<ISandboxFactory>(),
            ToolRegistry = services.GetRequiredService<IToolRegistry>(),
            ModelProvider = services.GetRequiredService<IModelProvider>(),
            LoggerFactory = services.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
        };
    }
}

/// <summary>
/// Options for configuring the Agent SDK.
/// </summary>
public class AgentSdkOptions
{
    /// <summary>
    /// Default working directory for sandboxes.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Maximum tool concurrency.
    /// </summary>
    public int MaxToolConcurrency { get; set; } = 3;

    /// <summary>
    /// Default model to use.
    /// </summary>
    public string? DefaultModel { get; set; }
}
