using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Models;
using KodaClaw.Contracts.Plugins;
using KodaClaw.Contracts.Secrets;
using KodaClaw.Contracts.Workspace;
using Microsoft.Extensions.Configuration;

namespace KodaClaw.Gateway;

internal sealed class SecretMigrationReportService
{
    private const int RepositoryScanPageSize = 200;
    private static readonly JsonSerializerOptions PersistenceJsonOptions = CreatePersistenceJsonOptions();

    private readonly IConfiguration _configuration;
    private readonly IWorkspaceService _workspaceService;
    private readonly ISecretStore _secretStore;
    private readonly IProviderAccountRepository _providerAccountRepository;
    private readonly IChannelAccountRepository _channelAccountRepository;
    private readonly IPluginRegistryRepository _pluginRegistryRepository;

    public SecretMigrationReportService(
        IConfiguration configuration,
        IWorkspaceService workspaceService,
        ISecretStore secretStore,
        IProviderAccountRepository providerAccountRepository,
        IChannelAccountRepository channelAccountRepository,
        IPluginRegistryRepository pluginRegistryRepository)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _providerAccountRepository = providerAccountRepository ?? throw new ArgumentNullException(nameof(providerAccountRepository));
        _channelAccountRepository = channelAccountRepository ?? throw new ArgumentNullException(nameof(channelAccountRepository));
        _pluginRegistryRepository = pluginRegistryRepository ?? throw new ArgumentNullException(nameof(pluginRegistryRepository));
    }

    public async Task<SecretMigrationReport> GenerateAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _workspaceService.EnsureInitializedAsync(cancellationToken);
        var items = new List<SecretMigrationItem>();
        var gatewayTokenItem = await BuildConfigurationItemAsync(
            id: "gateway-token",
            kind: "gatewayToken",
            displayName: "Gateway access token",
            location: "config/gateway",
            field: "token",
            secretRefKeys: ["KODACLAW_GATEWAY_TOKEN_SECRET_REF", "Gateway:TokenSecretRef"],
            valueKeys: ["KODACLAW_GATEWAY_TOKEN", "Gateway:Token"],
            cancellationToken);
        if (gatewayTokenItem is not null)
        {
            items.Add(gatewayTokenItem);
        }

        var openAiRuntimeItem = await BuildConfigurationItemAsync(
            id: "runtime-openai",
            kind: "runtimeProvider",
            displayName: "Runtime OpenAI API key",
            location: "config/runtime",
            field: "openAIApiKey",
            secretRefKeys: ["OPENAI_API_KEY_SECRET_REF", "Runtime:OpenAIApiKeySecretRef"],
            valueKeys: ["OPENAI_API_KEY", "Runtime:OpenAIApiKey"],
            cancellationToken,
            includeWhenUnconfigured: false);
        if (openAiRuntimeItem is not null)
        {
            items.Add(openAiRuntimeItem);
        }

        var anthropicRuntimeItem = await BuildConfigurationItemAsync(
            id: "runtime-anthropic",
            kind: "runtimeProvider",
            displayName: "Runtime Anthropic API key",
            location: "config/runtime",
            field: "anthropicApiKey",
            secretRefKeys: ["ANTHROPIC_API_KEY_SECRET_REF", "Runtime:AnthropicApiKeySecretRef"],
            valueKeys: ["ANTHROPIC_API_KEY", "Runtime:AnthropicApiKey"],
            cancellationToken,
            includeWhenUnconfigured: false);
        if (anthropicRuntimeItem is not null)
        {
            items.Add(anthropicRuntimeItem);
        }

        items.AddRange(await BuildProviderAccountItemsAsync(cancellationToken));
        items.AddRange(await BuildChannelItemsAsync(cancellationToken));
        items.AddRange(await BuildPluginItemsAsync(cancellationToken));

        var summary = BuildSummary(items);
        var report = new SecretMigrationReport(
            GeneratedAt: DateTimeOffset.UtcNow,
            WorkspaceRootPath: snapshot.RootPath,
            ArtifactPath: GetArtifactPath(),
            Summary: summary,
            Items: items);

        await PersistAsync(report, cancellationToken);
        return report;
    }

    private async Task<SecretMigrationItem?> BuildConfigurationItemAsync(
        string id,
        string kind,
        string displayName,
        string location,
        string field,
        IReadOnlyList<string> secretRefKeys,
        IReadOnlyList<string> valueKeys,
        CancellationToken cancellationToken,
        bool includeWhenUnconfigured = true)
    {
        var configuredSecretRef = GetFirstConfiguredValue(secretRefKeys, out _);
        var configuredValue = GetFirstConfiguredValue(valueKeys, out var configuredValueKey);
        if (!includeWhenUnconfigured && configuredSecretRef is null && configuredValue is null)
        {
            return null;
        }
        var legacyProbe = configuredValue is null || configuredValueKey is null
            ? LegacySourceProbe.None
            : ProbeConfiguredValue(configuredValueKey, configuredValue);
        var assessment = await EvaluateConfiguredSecretRefAsync(
            configuredSecretRef,
            legacyProbe,
            cancellationToken);

        return new SecretMigrationItem(
            Id: id,
            Kind: kind,
            DisplayName: displayName,
            State: assessment.State,
            Location: location,
            Field: field,
            ConfiguredSecretRef: configuredSecretRef,
            SecretRefExists: assessment.SecretRefExists,
            LegacySource: assessment.LegacySource,
            LegacySourceAvailable: assessment.LegacySourceAvailable,
            Notes: assessment.Notes);
    }

    private async Task<IReadOnlyList<SecretMigrationItem>> BuildProviderAccountItemsAsync(CancellationToken cancellationToken)
    {
        var accounts = await _providerAccountRepository.ListAccountsAsync(cancellationToken);
        var items = new List<SecretMigrationItem>(accounts.Count);

        foreach (var account in accounts)
        {
            var assessment = await EvaluateConfiguredSecretRefAsync(
                account.ApiKeySecretRef,
                ProbeEnvironmentVariable(account.ApiKeyEnvironmentVariable),
                cancellationToken);
            items.Add(new SecretMigrationItem(
                Id: $"provider-account:{account.Id}",
                Kind: "providerAccount",
                DisplayName: $"Provider account '{account.DisplayName}'",
                State: assessment.State,
                Location: $"accounts/{account.Id}",
                Field: "apiKey",
                ConfiguredSecretRef: account.ApiKeySecretRef,
                SecretRefExists: assessment.SecretRefExists,
                LegacySource: assessment.LegacySource,
                LegacySourceAvailable: assessment.LegacySourceAvailable,
                Notes: assessment.Notes));
        }

        return items;
    }

    private async Task<IReadOnlyList<SecretMigrationItem>> BuildChannelItemsAsync(CancellationToken cancellationToken)
    {
        var accounts = await ListAllChannelAccountsAsync(cancellationToken);
        var items = new List<SecretMigrationItem>(accounts.Count);

        foreach (var account in accounts)
        {
            var item = await BuildChannelItemAsync(account, cancellationToken);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private async Task<SecretMigrationItem?> BuildChannelItemAsync(
        ChannelAccount account,
        CancellationToken cancellationToken)
    {
        var configuration = ParseJsonObject(account.ConfigurationJson);
        var location = $"channel_accounts/{account.Id}";
        var displayName = $"Channel '{account.DisplayName}' ({account.ConnectorKind})";

        return account.ConnectorKind switch
        {
            ChannelConnectorKind.Telegram => await BuildTelegramChannelItemAsync(
                account,
                configuration,
                location,
                displayName,
                cancellationToken),
            ChannelConnectorKind.GenericWebhook => await BuildGenericWebhookChannelItemAsync(
                account,
                configuration,
                location,
                displayName,
                cancellationToken),
            ChannelConnectorKind.Feishu => await BuildFeishuChannelItemAsync(
                account,
                configuration,
                location,
                displayName,
                cancellationToken),
            ChannelConnectorKind.DingTalk => await BuildDingTalkChannelItemAsync(
                account,
                configuration,
                location,
                displayName,
                cancellationToken),
            _ => await BuildGenericChannelCredentialItemAsync(
                account,
                location,
                displayName,
                cancellationToken),
        };
    }

    private async Task<SecretMigrationItem> BuildTelegramChannelItemAsync(
        ChannelAccount account,
        JsonElement? configuration,
        string location,
        string displayName,
        CancellationToken cancellationToken)
    {
        var explicitToken = GetOptionalString(configuration, "botToken")
            ?? GetOptionalString(configuration, "token");
        if (explicitToken is not null)
        {
            return CreateLegacyLiteralItem(
                id: $"channel-account:{account.Id}",
                kind: "channelAccount",
                displayName: displayName,
                location: location,
                field: GetOptionalString(configuration, "botToken") is not null
                    ? "configurationJson.botToken"
                    : "configurationJson.token",
                literalSource: "literalConfigValue",
                literalValue: explicitToken,
                note: "Explicit Telegram token values remain legacy configuration and are not interpreted as secret references.");
        }

        var field = GetOptionalString(configuration, "credentialReference") is not null
            ? "configurationJson.credentialReference"
            : "credentialReference";
        var reference = GetOptionalString(configuration, "credentialReference") ?? account.CredentialReference;
        var assessment = await EvaluateReferenceValueAsync(reference, LegacySourceProbe.None, cancellationToken);
        return new SecretMigrationItem(
            Id: $"channel-account:{account.Id}",
            Kind: "channelAccount",
            DisplayName: displayName,
            State: assessment.State,
            Location: location,
            Field: field,
            ConfiguredSecretRef: assessment.ConfiguredSecretRef,
            SecretRefExists: assessment.SecretRefExists,
            LegacySource: assessment.LegacySource,
            LegacySourceAvailable: assessment.LegacySourceAvailable,
            Notes: assessment.Notes);
    }

    private async Task<SecretMigrationItem> BuildFeishuChannelItemAsync(
        ChannelAccount account,
        JsonElement? configuration,
        string location,
        string displayName,
        CancellationToken cancellationToken)
    {
        var explicitSecret = GetOptionalString(configuration, "appSecret");
        if (explicitSecret is not null)
        {
            return CreateLegacyLiteralItem(
                id: $"channel-account:{account.Id}",
                kind: "channelAccount",
                displayName: displayName,
                location: location,
                field: "configurationJson.appSecret",
                literalSource: "literalConfigValue",
                literalValue: explicitSecret,
                note: "Explicit Feishu appSecret values remain legacy configuration and are not interpreted as secret references.");
        }

        var field = GetOptionalString(configuration, "credentialReference") is not null
            ? "configurationJson.credentialReference"
            : "credentialReference";
        var reference = GetOptionalString(configuration, "credentialReference") ?? account.CredentialReference;
        var assessment = await EvaluateReferenceValueAsync(reference, LegacySourceProbe.None, cancellationToken);
        return new SecretMigrationItem(
            Id: $"channel-account:{account.Id}",
            Kind: "channelAccount",
            DisplayName: displayName,
            State: assessment.State,
            Location: location,
            Field: field,
            ConfiguredSecretRef: assessment.ConfiguredSecretRef,
            SecretRefExists: assessment.SecretRefExists,
            LegacySource: assessment.LegacySource,
            LegacySourceAvailable: assessment.LegacySourceAvailable,
            Notes: assessment.Notes);
    }

    private async Task<SecretMigrationItem> BuildDingTalkChannelItemAsync(
        ChannelAccount account,
        JsonElement? configuration,
        string location,
        string displayName,
        CancellationToken cancellationToken)
    {
        var explicitSecret = GetOptionalString(configuration, "appSecret");
        if (explicitSecret is not null)
        {
            return CreateLegacyLiteralItem(
                id: $"channel-account:{account.Id}",
                kind: "channelAccount",
                displayName: displayName,
                location: location,
                field: "configurationJson.appSecret",
                literalSource: "literalConfigValue",
                literalValue: explicitSecret,
                note: "Explicit DingTalk appSecret values remain legacy configuration and are not interpreted as secret references.");
        }

        var field = GetOptionalString(configuration, "credentialReference") is not null
            ? "configurationJson.credentialReference"
            : "credentialReference";
        var reference = GetOptionalString(configuration, "credentialReference") ?? account.CredentialReference;
        var assessment = await EvaluateReferenceValueAsync(reference, LegacySourceProbe.None, cancellationToken);
        return new SecretMigrationItem(
            Id: $"channel-account:{account.Id}",
            Kind: "channelAccount",
            DisplayName: displayName,
            State: assessment.State,
            Location: location,
            Field: field,
            ConfiguredSecretRef: assessment.ConfiguredSecretRef,
            SecretRefExists: assessment.SecretRefExists,
            LegacySource: assessment.LegacySource,
            LegacySourceAvailable: assessment.LegacySourceAvailable,
            Notes: assessment.Notes);
    }

    private async Task<SecretMigrationItem?> BuildGenericWebhookChannelItemAsync(
        ChannelAccount account,
        JsonElement? configuration,
        string location,
        string displayName,
        CancellationToken cancellationToken)
    {
        var explicitSecret = GetOptionalString(configuration, "sharedSecret")
            ?? GetOptionalString(configuration, "secret");
        if (explicitSecret is not null)
        {
            return CreateLegacyLiteralItem(
                id: $"channel-account:{account.Id}",
                kind: "channelAccount",
                displayName: displayName,
                location: location,
                field: GetOptionalString(configuration, "sharedSecret") is not null
                    ? "configurationJson.sharedSecret"
                    : "configurationJson.secret",
                literalSource: "literalConfigValue",
                literalValue: explicitSecret,
                note: "Explicit webhook shared secrets remain legacy configuration and are not interpreted as secret references.");
        }

        var credentialReferenceFromConfig = GetOptionalString(configuration, "credentialReference");
        var reference = credentialReferenceFromConfig ?? account.CredentialReference;
        if (reference is null)
        {
            return null;
        }

        var assessment = await EvaluateReferenceValueAsync(reference, LegacySourceProbe.None, cancellationToken);
        return new SecretMigrationItem(
            Id: $"channel-account:{account.Id}",
            Kind: "channelAccount",
            DisplayName: displayName,
            State: assessment.State,
            Location: location,
            Field: credentialReferenceFromConfig is not null
                ? "configurationJson.credentialReference"
                : "credentialReference",
            ConfiguredSecretRef: assessment.ConfiguredSecretRef,
            SecretRefExists: assessment.SecretRefExists,
            LegacySource: assessment.LegacySource,
            LegacySourceAvailable: assessment.LegacySourceAvailable,
            Notes: assessment.Notes);
    }

    private async Task<SecretMigrationItem?> BuildGenericChannelCredentialItemAsync(
        ChannelAccount account,
        string location,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.CredentialReference))
        {
            return null;
        }

        var assessment = await EvaluateReferenceValueAsync(account.CredentialReference, LegacySourceProbe.None, cancellationToken);
        return new SecretMigrationItem(
            Id: $"channel-account:{account.Id}",
            Kind: "channelAccount",
            DisplayName: displayName,
            State: assessment.State,
            Location: location,
            Field: "credentialReference",
            ConfiguredSecretRef: assessment.ConfiguredSecretRef,
            SecretRefExists: assessment.SecretRefExists,
            LegacySource: assessment.LegacySource,
            LegacySourceAvailable: assessment.LegacySourceAvailable,
            Notes: assessment.Notes);
    }

    private async Task<IReadOnlyList<SecretMigrationItem>> BuildPluginItemsAsync(CancellationToken cancellationToken)
    {
        var plugins = await ListAllPluginsAsync(cancellationToken);
        var items = new List<SecretMigrationItem>();

        foreach (var plugin in plugins)
        {
            var runtime = plugin.Manifest.Runtime;
            var literalEnvironment = runtime.Environment;
            var literalHeaders = runtime.Headers;

            if (runtime.EnvironmentReferences is not null)
            {
                foreach (var key in runtime.EnvironmentReferences.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase))
                {
                    var reference = runtime.EnvironmentReferences[key];
                    var literalValue = literalEnvironment is not null && literalEnvironment.TryGetValue(key, out var existingLiteralValue)
                        ? existingLiteralValue
                        : null;
                    var assessment = await EvaluateReferenceValueAsync(
                        reference,
                        ProbeLiteralValue($"literalEnvironmentValue:{key}", literalValue),
                        cancellationToken);
                    items.Add(new SecretMigrationItem(
                        Id: $"plugin-environment:{plugin.Id}:{key}",
                        Kind: "pluginRuntimeEnvironment",
                        DisplayName: $"Plugin '{plugin.Manifest.Name}' environment '{key}'",
                        State: assessment.State,
                        Location: $"plugins/{plugin.Id}",
                        Field: $"runtime.environmentReferences[{key}]",
                        ConfiguredSecretRef: assessment.ConfiguredSecretRef,
                        SecretRefExists: assessment.SecretRefExists,
                        LegacySource: assessment.LegacySource,
                        LegacySourceAvailable: assessment.LegacySourceAvailable,
                        Notes: assessment.Notes));
                }
            }

            if (literalEnvironment is not null)
            {
                foreach (var key in literalEnvironment.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase))
                {
                    if (runtime.EnvironmentReferences?.ContainsKey(key) == true || !IsLikelySecretEnvironmentKey(key))
                    {
                        continue;
                    }

                    items.Add(CreateLegacyLiteralItem(
                        id: $"plugin-environment:{plugin.Id}:{key}",
                        kind: "pluginRuntimeEnvironment",
                        displayName: $"Plugin '{plugin.Manifest.Name}' environment '{key}'",
                        location: $"plugins/{plugin.Id}",
                        field: $"runtime.environment[{key}]",
                        literalSource: $"literalEnvironmentValue:{key}",
                        literalValue: literalEnvironment[key]));
                }
            }

            if (runtime.HeaderReferences is not null)
            {
                foreach (var key in runtime.HeaderReferences.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase))
                {
                    var reference = runtime.HeaderReferences[key];
                    var literalValue = literalHeaders is not null && literalHeaders.TryGetValue(key, out var existingLiteralValue)
                        ? existingLiteralValue
                        : null;
                    var assessment = await EvaluateReferenceValueAsync(
                        reference,
                        ProbeLiteralValue($"literalHeaderValue:{key}", literalValue),
                        cancellationToken);
                    items.Add(new SecretMigrationItem(
                        Id: $"plugin-header:{plugin.Id}:{key}",
                        Kind: "pluginRuntimeHeader",
                        DisplayName: $"Plugin '{plugin.Manifest.Name}' header '{key}'",
                        State: assessment.State,
                        Location: $"plugins/{plugin.Id}",
                        Field: $"runtime.headerReferences[{key}]",
                        ConfiguredSecretRef: assessment.ConfiguredSecretRef,
                        SecretRefExists: assessment.SecretRefExists,
                        LegacySource: assessment.LegacySource,
                        LegacySourceAvailable: assessment.LegacySourceAvailable,
                        Notes: assessment.Notes));
                }
            }

            if (literalHeaders is not null)
            {
                foreach (var key in literalHeaders.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase))
                {
                    if (runtime.HeaderReferences?.ContainsKey(key) == true || !IsLikelySecretHeaderKey(key))
                    {
                        continue;
                    }

                    items.Add(CreateLegacyLiteralItem(
                        id: $"plugin-header:{plugin.Id}:{key}",
                        kind: "pluginRuntimeHeader",
                        displayName: $"Plugin '{plugin.Manifest.Name}' header '{key}'",
                        location: $"plugins/{plugin.Id}",
                        field: $"runtime.headers[{key}]",
                        literalSource: $"literalHeaderValue:{key}",
                        literalValue: literalHeaders[key]));
                }
            }
        }

        return items;
    }

    private async Task<IReadOnlyList<ChannelAccount>> ListAllChannelAccountsAsync(CancellationToken cancellationToken)
    {
        var accounts = new List<ChannelAccount>();
        var offset = 0;

        while (true)
        {
            var batch = await _channelAccountRepository.ListAsync(
                new ChannelAccountQuery(Limit: RepositoryScanPageSize, Offset: offset),
                cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            accounts.AddRange(batch);
            if (batch.Count < RepositoryScanPageSize)
            {
                break;
            }

            offset += batch.Count;
        }

        return accounts;
    }

    private async Task<IReadOnlyList<PluginRecord>> ListAllPluginsAsync(CancellationToken cancellationToken)
    {
        var plugins = new List<PluginRecord>();
        var offset = 0;

        while (true)
        {
            var batch = await _pluginRegistryRepository.ListAsync(
                new PluginQuery(Limit: RepositoryScanPageSize, Offset: offset),
                cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            plugins.AddRange(batch);
            if (batch.Count < RepositoryScanPageSize)
            {
                break;
            }

            offset += batch.Count;
        }

        return plugins;
    }

    private async Task<MigrationAssessment> EvaluateConfiguredSecretRefAsync(
        string? configuredSecretRef,
        LegacySourceProbe legacyFallback,
        CancellationToken cancellationToken)
    {
        var normalizedSecretRef = NormalizeOptionalString(configuredSecretRef);
        var legacyProbe = legacyFallback.IsConfigured ? legacyFallback : LegacySourceProbe.None;

        if (normalizedSecretRef is null)
        {
            return legacyProbe.Available
                ? new MigrationAssessment(
                    SecretMigrationState.LegacyFallback,
                    ConfiguredSecretRef: null,
                    SecretRefExists: false,
                    LegacySource: legacyProbe.Description,
                    LegacySourceAvailable: true,
                    Notes: legacyProbe.Note)
                : new MigrationAssessment(
                    SecretMigrationState.Missing,
                    ConfiguredSecretRef: null,
                    SecretRefExists: false,
                    LegacySource: legacyProbe.Description,
                    LegacySourceAvailable: false,
                    Notes: legacyProbe.Note);
        }

        if (!SecretRef.TryParse(normalizedSecretRef, out var secretRef))
        {
            return legacyProbe.Available
                ? new MigrationAssessment(
                    SecretMigrationState.LegacyFallback,
                    ConfiguredSecretRef: normalizedSecretRef,
                    SecretRefExists: false,
                    LegacySource: legacyProbe.Description,
                    LegacySourceAvailable: true,
                    Notes: AppendNotes(
                        "Configured secret ref has an invalid format.",
                        legacyProbe.Note,
                        "Runtime falls back to the legacy source."))
                : new MigrationAssessment(
                    SecretMigrationState.Missing,
                    ConfiguredSecretRef: normalizedSecretRef,
                    SecretRefExists: false,
                    LegacySource: legacyProbe.Description,
                    LegacySourceAvailable: false,
                    Notes: AppendNotes(
                        "Configured secret ref has an invalid format.",
                        legacyProbe.Note));
        }

        var (secretExists, secretNote) = await TryDescribeSecretRefAsync(secretRef, cancellationToken);
        if (secretExists)
        {
            return new MigrationAssessment(
                SecretMigrationState.Migrated,
                ConfiguredSecretRef: normalizedSecretRef,
                SecretRefExists: true,
                LegacySource: legacyProbe.Description,
                LegacySourceAvailable: legacyProbe.Available,
                Notes: AppendNotes(
                    legacyProbe.Available ? "Legacy fallback remains configured." : null,
                    legacyProbe.Note));
        }

        return legacyProbe.Available
            ? new MigrationAssessment(
                SecretMigrationState.LegacyFallback,
                ConfiguredSecretRef: normalizedSecretRef,
                SecretRefExists: false,
                LegacySource: legacyProbe.Description,
                LegacySourceAvailable: true,
                Notes: AppendNotes(
                    secretNote ?? "Configured secret ref was not found in the secret store.",
                    legacyProbe.Note,
                    "Runtime falls back to the legacy source."))
            : new MigrationAssessment(
                SecretMigrationState.Missing,
                ConfiguredSecretRef: normalizedSecretRef,
                SecretRefExists: false,
                LegacySource: legacyProbe.Description,
                LegacySourceAvailable: false,
                Notes: AppendNotes(
                    secretNote ?? "Configured secret ref was not found in the secret store.",
                    legacyProbe.Note));
    }

    private async Task<MigrationAssessment> EvaluateReferenceValueAsync(
        string? referenceValue,
        LegacySourceProbe fallbackLegacySource,
        CancellationToken cancellationToken)
    {
        var normalizedReference = NormalizeOptionalString(referenceValue);
        var fallback = fallbackLegacySource.IsConfigured ? fallbackLegacySource : LegacySourceProbe.None;

        if (normalizedReference is null)
        {
            return fallback.Available
                ? new MigrationAssessment(
                    SecretMigrationState.LegacyFallback,
                    ConfiguredSecretRef: null,
                    SecretRefExists: false,
                    LegacySource: fallback.Description,
                    LegacySourceAvailable: true,
                    Notes: fallback.Note)
                : new MigrationAssessment(
                    SecretMigrationState.Missing,
                    ConfiguredSecretRef: null,
                    SecretRefExists: false,
                    LegacySource: fallback.Description,
                    LegacySourceAvailable: false,
                    Notes: fallback.Note);
        }

        if (SecretRef.TryParse(normalizedReference, out var secretRef))
        {
            var (secretExists, secretNote) = await TryDescribeSecretRefAsync(secretRef, cancellationToken);
            if (secretExists)
            {
                return new MigrationAssessment(
                    SecretMigrationState.Migrated,
                    ConfiguredSecretRef: normalizedReference,
                    SecretRefExists: true,
                    LegacySource: fallback.Description,
                    LegacySourceAvailable: fallback.Available,
                    Notes: AppendNotes(
                        fallback.Available ? "Legacy literal fallback remains configured." : null,
                        fallback.Note));
            }

            return fallback.Available
                ? new MigrationAssessment(
                    SecretMigrationState.LegacyFallback,
                    ConfiguredSecretRef: normalizedReference,
                    SecretRefExists: false,
                    LegacySource: fallback.Description,
                    LegacySourceAvailable: true,
                    Notes: AppendNotes(
                        secretNote ?? "Configured secret ref was not found in the secret store.",
                        fallback.Note,
                        "Runtime falls back to the legacy literal source."))
                : new MigrationAssessment(
                    SecretMigrationState.Missing,
                    ConfiguredSecretRef: normalizedReference,
                    SecretRefExists: false,
                    LegacySource: fallback.Description,
                    LegacySourceAvailable: false,
                    Notes: AppendNotes(
                        secretNote ?? "Configured secret ref was not found in the secret store.",
                        fallback.Note));
        }

        var legacyReference = ProbeLegacyReference(normalizedReference);
        if (legacyReference.Available)
        {
            return new MigrationAssessment(
                SecretMigrationState.LegacyFallback,
                ConfiguredSecretRef: null,
                SecretRefExists: false,
                LegacySource: legacyReference.Description,
                LegacySourceAvailable: true,
                Notes: AppendNotes(
                    legacyReference.Note,
                    fallback.Available ? "Legacy literal fallback remains configured." : null,
                    fallback.Note));
        }

        return fallback.Available
            ? new MigrationAssessment(
                SecretMigrationState.LegacyFallback,
                ConfiguredSecretRef: null,
                SecretRefExists: false,
                LegacySource: fallback.Description,
                LegacySourceAvailable: true,
                Notes: AppendNotes(
                    legacyReference.Note,
                    fallback.Note))
            : new MigrationAssessment(
                SecretMigrationState.Missing,
                ConfiguredSecretRef: null,
                SecretRefExists: false,
                LegacySource: legacyReference.Description,
                LegacySourceAvailable: false,
                Notes: AppendNotes(
                    legacyReference.Note,
                    fallback.Note));
    }

    private async Task<(bool Exists, string? Note)> TryDescribeSecretRefAsync(
        SecretRef secretRef,
        CancellationToken cancellationToken)
    {
        try
        {
            var descriptor = await _secretStore.DescribeAsync(secretRef, cancellationToken);
            return descriptor.Exists
                ? (true, null)
                : (false, $"Configured secret ref '{secretRef}' was not found in the secret store.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    private static SecretMigrationItem CreateLegacyLiteralItem(
        string id,
        string kind,
        string displayName,
        string location,
        string field,
        string literalSource,
        string? literalValue,
        string? note = null)
    {
        var probe = ProbeLiteralValue(literalSource, literalValue);
        return new SecretMigrationItem(
            Id: id,
            Kind: kind,
            DisplayName: displayName,
            State: probe.Available ? SecretMigrationState.LegacyFallback : SecretMigrationState.Missing,
            Location: location,
            Field: field,
            ConfiguredSecretRef: null,
            SecretRefExists: false,
            LegacySource: probe.Description,
            LegacySourceAvailable: probe.Available,
            Notes: AppendNotes(note, probe.Note));
    }

    private static SecretMigrationSummary BuildSummary(IReadOnlyCollection<SecretMigrationItem> items)
    {
        var migrated = items.Count(static item => item.State == SecretMigrationState.Migrated);
        var legacyFallback = items.Count(static item => item.State == SecretMigrationState.LegacyFallback);
        var missing = items.Count - migrated - legacyFallback;
        return new SecretMigrationSummary(
            TotalCount: items.Count,
            MigratedCount: migrated,
            LegacyFallbackCount: legacyFallback,
            MissingCount: missing);
    }

    private async Task PersistAsync(SecretMigrationReport report, CancellationToken cancellationToken)
    {
        var absolutePath = Path.Combine(report.WorkspaceRootPath, report.ArtifactPath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        await using var stream = File.Create(absolutePath);
        await JsonSerializer.SerializeAsync(stream, report, PersistenceJsonOptions, cancellationToken);
    }

    private static string GetArtifactPath()
    {
        return Path.Combine(
                KodaClawWorkspaceLayout.ConfigDirectory,
                KodaClawWorkspaceLayout.SecretMigrationReportFile)
            .Replace('\\', '/');
    }

    private string? GetFirstConfiguredValue(IReadOnlyList<string> keys, out string? configuredKey)
    {
        foreach (var key in keys)
        {
            var value = NormalizeOptionalString(_configuration[key]);
            if (value is null)
            {
                continue;
            }

            configuredKey = key;
            return value;
        }

        configuredKey = null;
        return null;
    }

    private static LegacySourceProbe ProbeConfiguredValue(string sourceKey, string? value)
    {
        var normalized = NormalizeOptionalString(value);
        return normalized is null
            ? new LegacySourceProbe($"configurationValue:{sourceKey}", Available: false, IsConfigured: false, Note: null)
            : new LegacySourceProbe($"configurationValue:{sourceKey}", Available: true, IsConfigured: true, Note: null);
    }

    private static LegacySourceProbe ProbeEnvironmentVariable(string? variableName)
    {
        var normalized = NormalizeOptionalString(variableName);
        if (normalized is null)
        {
            return LegacySourceProbe.None;
        }

        var available = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(normalized));
        return new LegacySourceProbe(
            $"environmentVariable:{normalized}",
            Available: available,
            IsConfigured: true,
            Note: available
                ? null
                : $"Environment variable '{normalized}' is not set.");
    }

    private static LegacySourceProbe ProbeLiteralValue(string description, string? value)
    {
        var normalized = NormalizeOptionalString(value);
        if (normalized is null)
        {
            return LegacySourceProbe.None;
        }

        return new LegacySourceProbe(
            description,
            Available: true,
            IsConfigured: true,
            Note: null);
    }

    private static LegacySourceProbe ProbeLegacyReference(string? reference)
    {
        var normalized = NormalizeOptionalString(reference);
        if (normalized is null || SecretRef.TryParse(normalized, out _))
        {
            return LegacySourceProbe.None;
        }

        if (normalized.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var environmentKey = NormalizeOptionalString(normalized["env:".Length..]);
            if (environmentKey is null)
            {
                return new LegacySourceProbe(
                    "environmentVariable",
                    Available: false,
                    IsConfigured: true,
                    Note: "Legacy environment reference is missing a variable name.");
            }

            var available = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentKey));
            return new LegacySourceProbe(
                $"environmentVariable:{environmentKey}",
                Available: available,
                IsConfigured: true,
                Note: available
                    ? null
                    : $"Environment variable '{environmentKey}' is not set.");
        }

        if (normalized.StartsWith("inline:", StringComparison.OrdinalIgnoreCase))
        {
            var inlineValue = NormalizeOptionalString(normalized["inline:".Length..]);
            return new LegacySourceProbe(
                "inlineReference",
                Available: inlineValue is not null,
                IsConfigured: true,
                Note: inlineValue is null
                    ? "Inline legacy reference is empty."
                    : null);
        }

        if (normalized.StartsWith("value:", StringComparison.OrdinalIgnoreCase))
        {
            var valueReference = NormalizeOptionalString(normalized["value:".Length..]);
            return new LegacySourceProbe(
                "valueReference",
                Available: valueReference is not null,
                IsConfigured: true,
                Note: valueReference is null
                    ? "Value legacy reference is empty."
                    : null);
        }

        return new LegacySourceProbe(
            "rawReference",
            Available: true,
            IsConfigured: true,
            Note: null);
    }

    private static JsonElement? ParseJsonObject(string? json)
    {
        var normalized = NormalizeOptionalString(json);
        if (normalized is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(normalized);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetOptionalString(JsonElement? element, string propertyName)
    {
        if (element is null)
        {
            return null;
        }

        foreach (var property in element.Value.EnumerateObject())
        {
            if (!property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                return NormalizeOptionalString(property.Value.GetString());
            }

            return null;
        }

        return null;
    }

    private static bool IsLikelySecretEnvironmentKey(string key)
    {
        return ContainsSecretMarker(key);
    }

    private static bool IsLikelySecretHeaderKey(string key)
    {
        return ContainsSecretMarker(key)
            || key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Cookie", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsSecretMarker(string value)
    {
        var normalized = NormalizeOptionalString(value)?.Replace('-', '_').ToLowerInvariant();
        if (normalized is null)
        {
            return false;
        }

        return normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("authorization", StringComparison.Ordinal)
            || normalized.Contains("apikey", StringComparison.Ordinal)
            || normalized.Contains("api_key", StringComparison.Ordinal)
            || normalized.Contains("access_key", StringComparison.Ordinal)
            || normalized.Contains("client_key", StringComparison.Ordinal)
            || normalized.Contains("private_key", StringComparison.Ordinal)
            || normalized.Contains("license_key", StringComparison.Ordinal)
            || normalized.Contains("credential", StringComparison.Ordinal)
            || normalized.Contains("bearer", StringComparison.Ordinal)
            || normalized.Contains("cookie", StringComparison.Ordinal);
    }

    private static string? AppendNotes(params string?[] notes)
    {
        var filtered = notes
            .Where(static note => !string.IsNullOrWhiteSpace(note))
            .Select(static note => note!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return filtered.Length == 0
            ? null
            : string.Join(" ", filtered);
    }

    private static string? NormalizeOptionalString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static JsonSerializerOptions CreatePersistenceJsonOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
    }

    private sealed record MigrationAssessment(
        SecretMigrationState State,
        string? ConfiguredSecretRef,
        bool SecretRefExists,
        string? LegacySource,
        bool LegacySourceAvailable,
        string? Notes);

    private sealed record LegacySourceProbe(
        string? Description,
        bool Available,
        bool IsConfigured,
        string? Note)
    {
        public static LegacySourceProbe None { get; } = new(null, Available: false, IsConfigured: false, Note: null);
    }
}
