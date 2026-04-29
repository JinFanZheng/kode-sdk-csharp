using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Secrets;

namespace KodaClaw.ChannelHub.Connectors.Webhook;

public sealed class GenericWebhookConnector : IChannelConnector
{
    private readonly ConcurrentDictionary<string, RegisteredWebhookAccount> _accounts = new(StringComparer.Ordinal);
    private readonly ChannelSecretResolver _secretResolver;

    public GenericWebhookConnector(ISecretStore? secretStore = null)
    {
        _secretResolver = new ChannelSecretResolver(secretStore);
    }

    public ChannelConnectorKind Kind => ChannelConnectorKind.GenericWebhook;

    public async Task StartAsync(
        ChannelAccount account,
        Func<ChannelEventEnvelope, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(onEvent);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateAccount(account);
        var configuration = await GenericWebhookConnectorConfiguration
            .FromAccountAsync(account, _secretResolver, cancellationToken)
            .ConfigureAwait(false);
        _accounts[account.Id] = new RegisteredWebhookAccount(account, configuration, onEvent);
    }

    public Task StopAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new ArgumentException("A non-empty account id is required.", nameof(accountId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        _accounts.TryRemove(accountId.Trim(), out _);
        return Task.CompletedTask;
    }

    public Task SendAsync(ChannelOutboundDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("Generic webhook connector does not support outbound delivery.");
    }

    public async Task<GenericWebhookInboundDispatchResult> HandleInboundAsync(
        string accountId,
        string payloadJson,
        string? presentedSharedSecret,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new ArgumentException("A non-empty account id is required.", nameof(accountId));
        }

        if (!_accounts.TryGetValue(accountId.Trim(), out var registered))
        {
            return Rejected(
                code: "account_not_started",
                message: $"Webhook account '{accountId}' is not started.");
        }

        return await HandleInboundAsync(
            registered.Account,
            payloadJson,
            presentedSharedSecret,
            registered.OnEvent,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GenericWebhookInboundDispatchResult> HandleInboundAsync(
        ChannelAccount account,
        string payloadJson,
        string? presentedSharedSecret,
        Func<ChannelEventEnvelope, CancellationToken, Task> onEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(onEvent);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateAccount(account);
        var configuration = await GenericWebhookConnectorConfiguration
            .FromAccountAsync(account, _secretResolver, cancellationToken)
            .ConfigureAwait(false);

        if (!SecretsMatch(configuration.SharedSecret, presentedSharedSecret))
        {
            return Rejected(
                code: "secret_mismatch",
                message: "Webhook shared secret did not match account configuration.");
        }

        ChannelEventEnvelope envelope;
        try
        {
            envelope = GenericWebhookPayloadParser.Parse(
                account,
                payloadJson,
                configuration.DefaultThreadType) with
            {
                DefaultDeliveryMode = configuration.DefaultDeliveryMode,
            };
        }
        catch (ArgumentException ex)
        {
            return Rejected("invalid_payload", ex.Message);
        }
        catch (JsonException ex)
        {
            return Rejected("invalid_payload", ex.Message);
        }

        await onEvent(envelope, cancellationToken).ConfigureAwait(false);
        return new GenericWebhookInboundDispatchResult(Accepted: true, Event: envelope);
    }

    private static void ValidateAccount(ChannelAccount account)
    {
        if (account.ConnectorKind != ChannelConnectorKind.GenericWebhook)
        {
            throw new ArgumentException(
                $"Generic webhook connector cannot start account with connector kind '{account.ConnectorKind}'.",
                nameof(account));
        }

        if (string.IsNullOrWhiteSpace(account.Id))
        {
            throw new ArgumentException("A non-empty account id is required.", nameof(account));
        }
    }

    private static GenericWebhookInboundDispatchResult Rejected(string code, string message)
    {
        return new GenericWebhookInboundDispatchResult(
            Accepted: false,
            RejectionCode: code,
            RejectionMessage: message);
    }

    private static bool SecretsMatch(string? expectedSharedSecret, string? presentedSharedSecret)
    {
        if (string.IsNullOrWhiteSpace(expectedSharedSecret))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(presentedSharedSecret))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expectedSharedSecret.Trim());
        var presentedBytes = Encoding.UTF8.GetBytes(presentedSharedSecret.Trim());
        return CryptographicOperations.FixedTimeEquals(expectedBytes, presentedBytes);
    }

    private sealed record RegisteredWebhookAccount(
        ChannelAccount Account,
        GenericWebhookConnectorConfiguration Configuration,
        Func<ChannelEventEnvelope, CancellationToken, Task> OnEvent);
}
