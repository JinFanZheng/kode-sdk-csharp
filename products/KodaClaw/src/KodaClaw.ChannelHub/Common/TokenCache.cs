namespace KodaClaw.ChannelHub.Common;

internal sealed class TokenCache
{
    private static readonly TimeSpan DefaultRefreshEarlyMargin = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _refreshEarlyMargin;
    private readonly Dictionary<string, (string Token, DateTimeOffset ExpiresAt)> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TokenCache(TimeSpan? refreshEarlyMargin = null)
    {
        _refreshEarlyMargin = refreshEarlyMargin ?? DefaultRefreshEarlyMargin;
    }

    public async Task<string> GetOrRefreshAsync(
        string cacheKey,
        Func<CancellationToken, Task<(string Token, int ExpireSeconds)>> fetchAsync,
        CancellationToken cancellationToken)
    {
        if (_entries.TryGetValue(cacheKey, out var cached)
            && cached.ExpiresAt > DateTimeOffset.UtcNow + _refreshEarlyMargin)
        {
            return cached.Token;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(cacheKey, out cached)
                && cached.ExpiresAt > DateTimeOffset.UtcNow + _refreshEarlyMargin)
            {
                return cached.Token;
            }

            var (token, expireSeconds) = await fetchAsync(cancellationToken).ConfigureAwait(false);
            _entries[cacheKey] = (token, DateTimeOffset.UtcNow.AddSeconds(expireSeconds));
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }
}
