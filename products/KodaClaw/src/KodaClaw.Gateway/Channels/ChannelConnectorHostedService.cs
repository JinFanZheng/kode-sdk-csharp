using KodaClaw.ChannelHub;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using Microsoft.Extensions.Hosting;

namespace KodaClaw.Gateway.Channels;

internal sealed class ChannelConnectorHostedService : IHostedService, IChannelConnectorRegistry
{
    private readonly IChannelAccountRepository _channelAccountRepository;
    private readonly ChannelInboundGatewayService _channelInboundGatewayService;
    private readonly ChannelConnectorKindResolver _resolver;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ChannelConnectorHostedService> _logger;

    public ChannelConnectorHostedService(
        IChannelAccountRepository channelAccountRepository,
        ChannelInboundGatewayService channelInboundGatewayService,
        ChannelConnectorKindResolver resolver,
        IHostApplicationLifetime lifetime,
        ILogger<ChannelConnectorHostedService> logger)
    {
        _channelAccountRepository = channelAccountRepository ?? throw new ArgumentNullException(nameof(channelAccountRepository));
        _channelInboundGatewayService = channelInboundGatewayService ?? throw new ArgumentNullException(nameof(channelInboundGatewayService));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Use ApplicationStopping so connectors' polling loops are cancelled immediately
        // on Ctrl+C, rather than waiting for StopAsync to call Cancel() after a DB query.
        var pollingToken = _lifetime.ApplicationStopping;
        foreach (var connector in _resolver.GetAll())
        {
            await StartAccountsByKindAsync(connector.Kind, pollingToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ChannelAccount> allAccounts;
        try
        {
            // Use CancellationToken.None: the shutdown token may already be cancelled
            // (linked to ApplicationStopping in .NET 10), but a local SQLite list is
            // fast and must complete regardless of token state.
            allAccounts = await _channelAccountRepository.ListAsync(
                new ChannelAccountQuery(Limit: 200),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Repository may be unavailable during shutdown (e.g. after a backup import
            // replaced the database file). Best-effort: log and return.
            _logger.LogWarning(ex, "Failed to list channel accounts during shutdown; skipping connector stop.");
            return;
        }

        foreach (var account in allAccounts)
        {
            try
            {
                await _channelInboundGatewayService.StopAccountAsync(account.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Channel account '{AccountId}' failed to stop cleanly.",
                    account.Id);
            }
        }
    }

    public async Task ReloadAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        var account = await _channelAccountRepository.GetByIdAsync(accountId, cancellationToken);
        if (account is null)
        {
            _logger.LogWarning("Channel account '{AccountId}' not found during reload.", accountId);
            return;
        }

        // Stop first (best-effort) then re-start.
        try
        {
            await _channelInboundGatewayService.StopAccountAsync(accountId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Channel account '{AccountId}' failed to stop before reload; continuing with start.",
                accountId);
        }

        if (!account.InboundEnabled || account.State != ChannelAccountState.Connected)
        {
            return;
        }

        try
        {
            await _channelInboundGatewayService.StartAccountAsync(account, _lifetime.ApplicationStopping);
        }
        catch (OperationCanceledException) when (_lifetime.ApplicationStopping.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Channel account '{AccountId}' failed to start after reload.",
                accountId);
        }
    }

    public async Task StopAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        try
        {
            await _channelInboundGatewayService.StopAccountAsync(accountId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Channel account '{AccountId}' failed to stop.",
                accountId);
        }
    }

    private async Task StartAccountsByKindAsync(
        ChannelConnectorKind kind,
        CancellationToken cancellationToken)
    {
        // Relay accounts should be started regardless of state (they auto-reconnect)
        // Other connector types only start when explicitly Connected
        var stateFilter = kind == ChannelConnectorKind.Relay
            ? (ChannelAccountState?)null
            : ChannelAccountState.Connected;

        var accounts = await _channelAccountRepository.ListAsync(
            new ChannelAccountQuery(
                ConnectorKind: kind,
                State: stateFilter,
                Limit: 200),
            cancellationToken);

        foreach (var account in accounts.Where(static item => item.InboundEnabled))
        {
            try
            {
                await _channelInboundGatewayService.StartAccountAsync(account, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Channel account '{AccountId}' failed to start; skipping.",
                    account.Id);
            }
        }
    }
}
