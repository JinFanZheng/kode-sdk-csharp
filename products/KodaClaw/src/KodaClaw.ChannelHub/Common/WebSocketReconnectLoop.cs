namespace KodaClaw.ChannelHub.Common;

internal static class WebSocketReconnectLoop
{
    public static async Task RunAsync(
        WebSocketReconnectLoopOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var retryDelay = options.BaseDelay;
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            attempt++;
            try
            {
                await options.RunSingleConnectionAsync(cancellationToken).ConfigureAwait(false);
                options.OnReconnecting?.Invoke(attempt + 1);
                retryDelay = options.BaseDelay;
                attempt = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (options.IsTerminalException is { } predicate && predicate(ex))
            {
                options.OnTerminalException?.Invoke(ex);
                return;
            }
            catch (Exception ex)
            {
                options.OnConnectionFailed?.Invoke(attempt, ex);
            }

            try
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            retryDelay = retryDelay * 2 < options.MaxDelay
                ? retryDelay * 2
                : options.MaxDelay;
        }
    }
}

internal sealed class WebSocketReconnectLoopOptions
{
    public required TimeSpan BaseDelay { get; init; }
    public required TimeSpan MaxDelay { get; init; }
    public required Func<CancellationToken, Task> RunSingleConnectionAsync { get; init; }

    public Action<int>? OnReconnecting { get; init; }
    public Action<int, Exception>? OnConnectionFailed { get; init; }
    public Func<Exception, bool>? IsTerminalException { get; init; }
    public Action<Exception>? OnTerminalException { get; init; }
}
