using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KodaClaw.Gateway;

/// <summary>
/// ENV-var bootstrap path: reads KODACLAW_ANTHROPIC_API_KEY / KODACLAW_OPENAI_API_KEY
/// from configuration and delegates to ConfigBootstrapWriter.
///
/// This service is meant for CI / automated deployments where API keys are passed as
/// environment variables. For interactive first-run setup, the Setup Wizard
/// (POST /setup/complete) calls ConfigBootstrapWriter directly.
///
/// Registration order matters: must start BEFORE ModelRegistrySeedService so the
/// keychain-backed account is in place before the env-var SeedService runs.
/// </summary>
internal sealed class ConfigBootstrapService : IHostedService
{
    private readonly ConfigBootstrapWriter _writer;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigBootstrapService>? _logger;

    public ConfigBootstrapService(
        ConfigBootstrapWriter writer,
        IConfiguration configuration,
        ILogger<ConfigBootstrapService>? logger = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var anthropicKey = _configuration["KODACLAW_ANTHROPIC_API_KEY"];
        var openaiKey = _configuration["KODACLAW_OPENAI_API_KEY"];

        if (string.IsNullOrWhiteSpace(anthropicKey) && string.IsNullOrWhiteSpace(openaiKey))
        {
            _logger?.LogDebug(
                "ConfigBootstrapService: neither KODACLAW_ANTHROPIC_API_KEY nor " +
                "KODACLAW_OPENAI_API_KEY is set — Setup Wizard path will handle onboarding.");
            return;
        }

        await _writer.WriteIfAbsentAsync(anthropicKey, openaiKey, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
