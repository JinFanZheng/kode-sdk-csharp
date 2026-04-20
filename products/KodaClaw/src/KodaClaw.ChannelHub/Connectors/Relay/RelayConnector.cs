using System.Collections.Concurrent;
using System.Text.Json;
using KodaClaw.Contracts;
using Microsoft.Extensions.Logging;

namespace KodaClaw.ChannelHub.Connectors.Relay;

public sealed class RelayConnector : IChannelConnector
{
    private const string DiagnosticSource = "relay";

    private readonly ConcurrentDictionary<string, StartedAccount> _startedAccounts =
        new(StringComparer.Ordinal);

    private readonly RelayConnectorOptions _options;
    private readonly ChannelSecretResolver _secretResolver;
    private readonly IDiagnosticsService? _diagnosticsService;
    private readonly ILogger<RelayConnector> _logger;
    private readonly IInboxRepository? _inboxRepository;

    public RelayConnector(
        ILogger<RelayConnector> logger,
        RelayConnectorOptions? options = null,
        ISecretStore? secretStore = null,
        IDiagnosticsService? diagnosticsService = null,
        IInboxRepository? inboxRepository = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new RelayConnectorOptions();
        _secretResolver = new ChannelSecretResolver(secretStore);
        _diagnosticsService = diagnosticsService;
        _inboxRepository = inboxRepository;
    }

    public ChannelConnectorKind Kind => ChannelConnectorKind.Relay;

    public async Task StartAsync(
        ChannelAccount account,
        Func<ChannelEventEnvelope, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(onEvent);
        cancellationToken.ThrowIfCancellationRequested();

        if (account.ConnectorKind != ChannelConnectorKind.Relay)
        {
            throw new ArgumentException(
                $"Relay connector cannot start account with connector kind '{account.ConnectorKind}'.",
                nameof(account));
        }

        var configuration = await RelayConnectorConfiguration
            .FromAccountAsync(account, _secretResolver, cancellationToken)
            .ConfigureAwait(false);
        var externalAccountId = ValidateAndNormalizeAccountId(configuration.AccountId);

        var wsClient = new RelayWebSocketClient(
            externalAccountId,
            configuration.RelayUrl,
            configuration.SharedSecret,
            (eventJson, ct) =>
                DispatchEventAsync(account.Id, configuration, eventJson, onEvent, ct),
            _options,
            _diagnosticsService);

        var startedAccount = new StartedAccount(account, configuration, wsClient);

        if (!_startedAccounts.TryAdd(externalAccountId, startedAccount))
        {
            _ = wsClient.DisposeAsync();
            throw new InvalidOperationException($"Relay account '{externalAccountId}' is already started.");
        }

        wsClient.Start(cancellationToken);

        _logger.LogInformation(
            "Relay connector starting for account {AccountId} → {RelayUrl}", externalAccountId, configuration.RelayUrl);
        RecordDiagnosticEvent(
            "relay.account_started", "info",
            $"Relay account started: accountId={externalAccountId} url={configuration.RelayUrl}");
    }

    public async Task StopAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var normalizedAccountId = ValidateAndNormalizeAccountId(accountId);

        if (!_startedAccounts.TryRemove(normalizedAccountId, out var startedAccount))
        {
            return;
        }

        await startedAccount.WebSocketClient.StopAsync().ConfigureAwait(false);
        await startedAccount.WebSocketClient.DisposeAsync().ConfigureAwait(false);

        RecordDiagnosticEvent("relay.account_stopped", "info",
            $"Relay account stopped: accountId={accountId}");
    }

    public Task SendAsync(ChannelOutboundDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        _logger.LogInformation("Relay connector does not support outbound, reply discarded");
        return Task.CompletedTask;
    }

    // ── 事件映射 ──

    private async Task DispatchEventAsync(
        string accountId,
        RelayConnectorConfiguration configuration,
        JsonElement eventJson,
        Func<ChannelEventEnvelope, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken)
    {
        var eventType = GetOptionalString(eventJson, "eventType");

        if (RelayEventFrameParser.IsNotificationEventType(eventType))
        {
            RecordDiagnosticEvent(
                "relay.notification_received",
                "info",
                "Relay notification event received.",
                attributes: BuildEventAttributes(eventJson, eventType));
            await HandleNotificationAsync(eventJson, configuration).ConfigureAwait(false);
            return;
        }

        if (!RelayEventFrameParser.TryParse(accountId, configuration, eventJson, out var envelope, out var error))
        {
            _logger.LogWarning("Failed to map relay event to envelope: {Error}", error);
            RecordDiagnosticEvent(
                "relay.event_rejected",
                "warning",
                error ?? "Relay event rejected.",
                attributes: BuildEventAttributes(eventJson, eventType, extra: new Dictionary<string, string?>
                {
                    ["error"] = error,
                }));
            return;
        }

        RecordDiagnosticEvent(
            "relay.event_dispatched",
            "info",
            $"Relay event dispatched: eventId={envelope!.EventId} eventType={envelope.EventType}",
            attributes: new Dictionary<string, string?>
            {
                ["accountId"] = accountId,
                ["eventId"] = envelope.EventId,
                ["eventType"] = envelope.EventType.ToString(),
                ["threadType"] = envelope.ThreadType.ToString(),
                ["externalThreadId"] = envelope.ExternalThreadId,
                ["externalMessageId"] = envelope.ExternalMessageId,
                ["correlationId"] = envelope.CorrelationId,
            });
        await onEvent(envelope!, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleNotificationAsync(JsonElement root, RelayConnectorConfiguration configuration)
    {
        var eventId = GetOptionalString(root, "eventId") ?? $"relay-notif-{Guid.NewGuid():N}";
        var title = GetOptionalString(root, "text") ?? "社区通知";
        var metadataJson = root.GetRawText();
        var now = DateTimeOffset.UtcNow;

        if (_inboxRepository is not null)
        {
            try
            {
                await _inboxRepository.UpsertAsync(new InboxItem(
                    Id: $"relay-notif-{eventId}",
                    Kind: InboxItemKind.Information,
                    Status: InboxItemStatus.Open,
                    Title: title,
                    Summary: title,
                    Source: "community",
                    CreatedAt: now,
                    UpdatedAt: now,
                    RequiresAction: false,
                    PayloadJson: metadataJson)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write community notification to inbox");
            }
        }

        if (!string.IsNullOrWhiteSpace(configuration.NotifyChannelId))
        {
            _logger.LogInformation(
                "Community notification received; NotifyChannelId={ChannelId} (channel push not yet implemented at connector layer)",
                configuration.NotifyChannelId);
        }

        RecordDiagnosticEvent("relay.notification_handled", "info",
            $"Community notification handled: {title}");
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }

        return null;
    }

    private static string ValidateAndNormalizeAccountId(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new ArgumentException("A non-empty account id is required.", nameof(accountId));
        }
        return accountId.Trim();
    }

    private static IReadOnlyDictionary<string, string?> BuildEventAttributes(
        JsonElement eventJson,
        string? eventType,
        IReadOnlyDictionary<string, string?>? extra = null)
    {
        var attributes = new Dictionary<string, string?>
        {
            ["eventId"] = GetOptionalString(eventJson, "eventId"),
            ["eventType"] = eventType,
            ["threadType"] = GetOptionalString(eventJson, "threadType"),
            ["externalThreadId"] = GetOptionalString(eventJson, "externalThreadId"),
            ["externalMessageId"] = GetOptionalString(eventJson, "externalMessageId"),
            ["correlationId"] = GetOptionalString(eventJson, "correlationId"),
        };

        if (extra is not null)
        {
            foreach (var pair in extra)
            {
                attributes[pair.Key] = pair.Value;
            }
        }

        return attributes;
    }

    private void RecordDiagnosticEvent(
        string eventType,
        string level,
        string message,
        IReadOnlyDictionary<string, string?>? attributes = null)
    {
        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: DiagnosticSource,
            EventType: eventType,
            Level: level,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow,
            Attributes: attributes));
    }

    private sealed record StartedAccount(
        ChannelAccount Account,
        RelayConnectorConfiguration Configuration,
        RelayWebSocketClient WebSocketClient);
}
