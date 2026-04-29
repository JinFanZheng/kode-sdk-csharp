using KodaClaw.ChannelHub;
using KodaClaw.ChannelHub.Inbound;
using KodaClaw.ChannelHub.Turn;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Runtime;

namespace KodaClaw.Gateway.Channels;

internal sealed class ChannelInboundGatewayService
{
    private readonly ChannelEventIngestionService _channelEventIngestionService;
    private readonly ChannelTurnOrchestrator _channelTurnOrchestrator;
    private readonly ChannelConnectorKindResolver _resolver;
    private readonly IRuntimeConfigurationResolver? _runtimeConfigurationResolver;
    private readonly IDiagnosticsService? _diagnosticsService;

    public ChannelInboundGatewayService(
        ChannelEventIngestionService channelEventIngestionService,
        ChannelTurnOrchestrator channelTurnOrchestrator,
        ChannelConnectorKindResolver resolver,
        IRuntimeConfigurationResolver? runtimeConfigurationResolver = null,
        IDiagnosticsService? diagnosticsService = null)
    {
        _channelEventIngestionService = channelEventIngestionService ?? throw new ArgumentNullException(nameof(channelEventIngestionService));
        _channelTurnOrchestrator = channelTurnOrchestrator ?? throw new ArgumentNullException(nameof(channelTurnOrchestrator));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _runtimeConfigurationResolver = runtimeConfigurationResolver;
        _diagnosticsService = diagnosticsService;
    }

    public async Task<ChannelInboundHandlingResult> ProcessAsync(
        ChannelEventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var runtimeSnapshot = _runtimeConfigurationResolver?.Resolve();
        if (!IsRuntimeSnapshotReady(runtimeSnapshot))
        {
            var processing = await _channelEventIngestionService.IngestAsync(envelope, cancellationToken);
            RecordRuntimeSkipped(processing.Binding, "Skipped channel turn execution because runtime is not configured.");
            return new ChannelInboundHandlingResult(
                Processing: processing,
                RuntimeExecuted: false,
                SkipReason: "runtime_not_configured");
        }

        try
        {
            var orchestration = await _channelTurnOrchestrator.ProcessInboundAsync(envelope, cancellationToken);
            return new ChannelInboundHandlingResult(
                Processing: orchestration.Processing,
                Turn: orchestration,
                RuntimeExecuted: true);
        }
        catch (InvalidOperationException ex) when (LooksLikeRuntimeConfigurationError(ex.Message))
        {
            var processing = await _channelEventIngestionService.IngestAsync(envelope, cancellationToken);
            RecordRuntimeSkipped(processing.Binding, ex.Message);
            return new ChannelInboundHandlingResult(
                Processing: processing,
                RuntimeExecuted: false,
                SkipReason: ex.Message);
        }
    }

    public async Task StartAccountAsync(
        ChannelAccount account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (!_resolver.TryGet(account.ConnectorKind, out var connector))
        {
            return;
        }

        try
        {
            await connector!.StartAsync(
                account,
                async (envelope, token) => await ProcessAsync(envelope, token),
                cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already started", StringComparison.OrdinalIgnoreCase))
        {
            // Account is already live; keep current connection.
        }
    }

    public async Task StopAccountAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        foreach (var connector in _resolver.GetAll())
        {
            await connector.StopAsync(accountId, cancellationToken);
        }
    }

    private void RecordChannelEvent(string eventType, string level, string message, string? accountId = null)
    {
        _diagnosticsService?.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: "gateway.channels",
            EventType: eventType,
            Level: level,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow,
            Attributes: accountId is not null
                ? new Dictionary<string, string?> { ["accountId"] = accountId }
                : null));
    }

    private void RecordRuntimeSkipped(ThreadBinding binding, string message)
    {
        if (_diagnosticsService is null)
        {
            return;
        }

        _diagnosticsService.Record(new DiagnosticEvent(
            Id: $"diag-channel-runtime-skip-{Guid.NewGuid():N}",
            Source: "gateway.channels",
            EventType: "gateway.channels.session_skipped",
            Level: "warning",
            Message: message,
            Timestamp: DateTimeOffset.UtcNow,
            SessionId: binding.SessionId,
            Attributes: new Dictionary<string, string?>
            {
                ["accountId"] = binding.AccountId,
                ["bindingId"] = binding.Id,
                ["threadType"] = binding.ThreadType.ToString(),
            }));
    }

    private static bool IsRuntimeSnapshotReady(RuntimeConfigurationSnapshot? snapshot)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.DefaultModel))
        {
            return false;
        }

        var hasOpenAi = !string.IsNullOrWhiteSpace(snapshot.OpenAIApiKey);
        var hasAnthropic = !string.IsNullOrWhiteSpace(snapshot.AnthropicApiKey);
        if (!hasOpenAi && !hasAnthropic)
        {
            return false;
        }

        var model = snapshot.DefaultModel!;
        var looksLikeOpenAi = model.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("o4", StringComparison.OrdinalIgnoreCase);
        var looksLikeAnthropic = model.StartsWith("claude", StringComparison.OrdinalIgnoreCase);

        if (hasOpenAi && !hasAnthropic)
        {
            return !looksLikeAnthropic;
        }

        if (hasAnthropic && !hasOpenAi)
        {
            return !looksLikeOpenAi;
        }

        return looksLikeOpenAi || looksLikeAnthropic;
    }

    private static bool LooksLikeRuntimeConfigurationError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("KodaClaw chat is not configured", StringComparison.Ordinal)
            || message.Contains("KODACLAW_DEFAULT_MODEL", StringComparison.Ordinal)
            || message.Contains("OPENAI_API_KEY", StringComparison.Ordinal)
            || message.Contains("ANTHROPIC_API_KEY", StringComparison.Ordinal);
    }
}
