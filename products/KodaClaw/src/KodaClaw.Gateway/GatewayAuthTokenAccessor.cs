using KodaClaw.Contracts;
using KodaClaw.Contracts.Secrets;
using KodaClaw.Contracts.Workspace;
using Microsoft.Extensions.Configuration;

namespace KodaClaw.Gateway;

internal sealed class GatewayAuthTokenAccessor
{
    private readonly IConfiguration _configuration;
    private readonly ISecretStore _secretStore;
    private readonly IWorkspaceService _workspaceService;
    private readonly object _sync = new();
    private string? _cachedToken;
    private bool _loaded;

    public GatewayAuthTokenAccessor(IConfiguration configuration, ISecretStore secretStore, IWorkspaceService workspaceService)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
    }

    public string? GetConfiguredToken()
    {
        if (_loaded)
        {
            return _cachedToken;
        }

        lock (_sync)
        {
            if (_loaded)
            {
                return _cachedToken;
            }

            _cachedToken = LoadConfiguredToken();
            _loaded = true;
            return _cachedToken;
        }
    }

    private string? LoadConfiguredToken()
    {
        // Priority 1: OS Keychain via secret ref
        var secretRefValue = _configuration["KODACLAW_GATEWAY_TOKEN_SECRET_REF"]
            ?? _configuration["Gateway:TokenSecretRef"];
        if (SecretRef.TryParse(secretRefValue, out var secretRef))
        {
            var resolvedFromSecretStore = _secretStore.GetAsync(secretRef).GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(resolvedFromSecretStore))
            {
                return resolvedFromSecretStore.Trim();
            }
        }

        // Priority 2: Environment variable / appsettings
        var configuredToken = _configuration["KODACLAW_GATEWAY_TOKEN"] ?? _configuration["Gateway:Token"];
        if (!string.IsNullOrWhiteSpace(configuredToken))
        {
            return configuredToken.Trim();
        }

        // Priority 3: gateway.json in workspace (lowest precedence, useful for local dev / Electron pairing)
        var gatewayConfig = _workspaceService.ReadGatewayConfigAsync().GetAwaiter().GetResult();
        return string.IsNullOrWhiteSpace(gatewayConfig.AccessToken)
            ? null
            : gatewayConfig.AccessToken.Trim();
    }
}
