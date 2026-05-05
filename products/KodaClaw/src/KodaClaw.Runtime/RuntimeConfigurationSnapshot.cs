using Kode.Agent.Sdk.Core.Abstractions;

namespace KodaClaw.Runtime;

public sealed record RuntimeConfigurationSnapshot(
    string? DefaultModel,
    string? OpenAIApiKey,
    string? OpenAIBaseUrl,
    string? AnthropicApiKey,
    string? AnthropicBaseUrl,
    string? DeepSeekApiKey,
    string? DeepSeekBaseUrl,
    IReadOnlyDictionary<string, string>? CustomHeaders = null,
    IReadOnlyDictionary<string, ModelCapabilities>? ModelCapabilitiesOverride = null)
{
    public static RuntimeConfigurationSnapshot FromOptions(KodaClawRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new RuntimeConfigurationSnapshot(
            DefaultModel: Normalize(options.DefaultModel),
            OpenAIApiKey: Normalize(options.OpenAIApiKey),
            OpenAIBaseUrl: NormalizeBaseUrl(options.OpenAIBaseUrl),
            AnthropicApiKey: Normalize(options.AnthropicApiKey),
            AnthropicBaseUrl: NormalizeBaseUrl(options.AnthropicBaseUrl),
            DeepSeekApiKey: Normalize(options.DeepSeekApiKey),
            DeepSeekBaseUrl: NormalizeBaseUrl(options.DeepSeekBaseUrl),
            ModelCapabilitiesOverride: options.ModelCapabilitiesOverride);
    }

    private static string? Normalize(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static string? NormalizeBaseUrl(string? value)
    {
        return Normalize(value)?.TrimEnd('/');
    }
}

public interface IRuntimeConfigurationResolver
{
    RuntimeConfigurationSnapshot Resolve();
}

internal sealed class StaticRuntimeConfigurationResolver : IRuntimeConfigurationResolver
{
    private readonly RuntimeConfigurationSnapshot _snapshot;

    public StaticRuntimeConfigurationResolver(RuntimeConfigurationSnapshot snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public RuntimeConfigurationSnapshot Resolve()
    {
        return _snapshot;
    }
}
