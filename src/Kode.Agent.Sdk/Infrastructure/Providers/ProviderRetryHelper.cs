using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using Anthropic.Exceptions;
using Kode.Agent.Sdk.Core.Types;
using Microsoft.Extensions.Logging;

namespace Kode.Agent.Sdk.Infrastructure.Providers;

/// <summary>
/// Retry helper for model provider HTTP calls.
/// Supports two modes:
///   - <see cref="ExecuteWithRetryAsync{T}"/> for one-shot calls (CompleteAsync, ValidateAsync).
///   - <see cref="StreamWithRetryAsync"/> for streaming calls: only retries if no chunk has been
///     yielded yet, preventing duplicate / interleaved content from reaching the Agent.
/// </summary>
internal static class ProviderRetryHelper
{
    // ── Retryable error detection ─────────────────────────────────────────────

    /// <summary>
    /// Returns true when <paramref name="ex"/> represents a transient error that is safe to retry.
    /// Uses typed exception checks first (Anthropic SDK, OpenAI System.ClientModel),
    /// then falls back to string-pattern matching for third-party compatible APIs.
    /// <see cref="OperationCanceledException"/> is never retryable.
    /// </summary>
    internal static bool IsRetryable(Exception ex)
    {
        if (ex is OperationCanceledException) return false;

        // Anthropic SDK typed exceptions
        if (ex is AnthropicRateLimitException) return true;
        if (ex is Anthropic5xxException) return true;     // 529 Overloaded, 503, etc.
        if (ex is AnthropicIOException) return true;      // network / connection errors

        // OpenAI SDK (System.ClientModel)
        if (ex is System.ClientModel.ClientResultException cre)
            return cre.Status is 429 or 503 or 529 || ContainsRetryableKeyword(cre.Message);

        // HttpRequestException — covers OpenAIResponsesProvider (raw HTTP) and third-party APIs
        if (ex is HttpRequestException hre)
        {
            if (hre.StatusCode is HttpStatusCode.TooManyRequests      // 429
                               or HttpStatusCode.ServiceUnavailable)  // 503
                return true;
            return ContainsRetryableKeyword(hre.Message);
        }

        // String-pattern fallback for any other wrapped exceptions from compatible APIs
        return ContainsRetryableKeyword(ex.Message);
    }

    private static bool ContainsRetryableKeyword(string? msg)
    {
        if (msg is null) return false;
        return msg.Contains("429") ||
               msg.Contains("529") ||
               msg.Contains("503") ||
               msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("overloaded", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("访问量过大") ||
               msg.Contains("您的账户已达到速率限制") ||
               msg.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }

    // ── Delay computation ─────────────────────────────────────────────────────

    /// <summary>
    /// Computes the back-off delay for <paramref name="attempt"/> (0-indexed).
    /// Respects a <c>Retry-After: N</c> value embedded in the exception message,
    /// then falls back to exponential back-off with configurable jitter.
    /// </summary>
    internal static TimeSpan ComputeDelay(int attempt, Exception ex, RetryPolicy policy)
    {
        if (TryParseRetryAfter(ex.Message, out var retryAfterSeconds))
            return TimeSpan.FromSeconds(Math.Min(retryAfterSeconds, policy.MaxDelay.TotalSeconds));

        var exponential = policy.InitialDelay.TotalSeconds * Math.Pow(policy.BackoffMultiplier, attempt);
        var capped = Math.Min(exponential, policy.MaxDelay.TotalSeconds);
        var jitter = capped * policy.JitterFactor * (Random.Shared.NextDouble() * 2 - 1);
        return TimeSpan.FromSeconds(Math.Max(0.1, capped + jitter));
    }

    // ── Retry callback ────────────────────────────────────────────────────────

    private static void InvokeOnRetry(RetryPolicy policy, string providerName, int attempt, TimeSpan delay, Exception ex)
    {
        var msg = ex.Message;
        var ctx = new RetryAttemptContext
        {
            ProviderName = providerName,
            Attempt = attempt,
            MaxRetries = policy.MaxRetries,
            Delay = delay,
            ErrorMessage = msg.Length > 200 ? msg[..200] : msg,
        };

        // SDK-level: notify the hosting Agent so it can emit a ModelRetryingEvent
        // on its EventBus without any wiring required from the application layer.
        try { AgentRetryContext.Current.Value?.Invoke(ctx); }
        catch { }

        // Application-level: optional custom callback supplied via RetryPolicy.OnRetry.
        if (policy.OnRetry is null) return;
        try { policy.OnRetry(ctx); }
        catch { }
    }

    private static bool TryParseRetryAfter(string? message, out double seconds)
    {
        seconds = 0;
        if (message is null) return false;

        var idx = message.IndexOf("retry-after", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        var start = idx + "retry-after".Length;
        while (start < message.Length && (message[start] == ':' || message[start] == ' '))
            start++;

        var end = start;
        while (end < message.Length && (char.IsDigit(message[end]) || message[end] == '.'))
            end++;

        return end > start
            && double.TryParse(message[start..end], out seconds)
            && seconds > 0;
    }

    // ── One-shot retry ────────────────────────────────────────────────────────

    /// <summary>
    /// Executes <paramref name="operation"/> with retry. Re-invokes the operation with the
    /// same <see cref="CancellationToken"/> on each attempt so call-site setup
    /// (e.g. header flags) runs fresh every time.
    /// </summary>
    internal static async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        RetryPolicy policy,
        ILogger? logger,
        string providerName,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        for (var attempt = 0; attempt <= policy.MaxRetries; attempt++)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException
                && IsRetryable(ex)
                && attempt < policy.MaxRetries
                && sw.Elapsed < policy.MaxTotalRetryDuration)
            {
                var delay = ComputeDelay(attempt, ex, policy);
                logger?.LogWarning(
                    "[{Provider}] Retryable error on attempt {Attempt}/{Max}, waiting {Delay:F1}s: {Message}",
                    providerName, attempt + 1, policy.MaxRetries, delay.TotalSeconds, ex.Message);
                InvokeOnRetry(policy, providerName, attempt + 1, delay, ex);
                await Task.Delay(delay, cancellationToken);
            }
        }

        // Unreachable: the last attempt either returned or threw (catch guard excludes it).
        throw new InvalidOperationException("Retry loop exited without completing.");
    }

    // ── Streaming retry ───────────────────────────────────────────────────────

    /// <summary>
    /// Wraps a streaming factory with retry semantics.
    /// A new enumerator is created for each attempt by calling <paramref name="factory"/>.
    /// Retry is only attempted when <c>chunksYielded == 0</c>: if any chunk has already been
    /// forwarded to the caller, retrying would produce duplicate/interleaved content,
    /// so the exception propagates instead.
    /// </summary>
    internal static async IAsyncEnumerable<StreamChunk> StreamWithRetryAsync(
        Func<CancellationToken, IAsyncEnumerable<StreamChunk>> factory,
        RetryPolicy policy,
        ILogger? logger,
        string providerName,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        for (var attempt = 0; attempt <= policy.MaxRetries; attempt++)
        {
            var chunksYielded = 0;
            var shouldRetry = false;
            TimeSpan retryDelay = default;

            var enumerator = factory(cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = await enumerator.MoveNextAsync();
                    }
                    catch (Exception ex) when (
                        ex is not OperationCanceledException
                        && chunksYielded == 0
                        && IsRetryable(ex)
                        && attempt < policy.MaxRetries
                        && sw.Elapsed < policy.MaxTotalRetryDuration)
                    {
                        retryDelay = ComputeDelay(attempt, ex, policy);
                        logger?.LogWarning(
                            "[{Provider}] Retryable stream error on attempt {Attempt}/{Max}, waiting {Delay:F1}s: {Message}",
                            providerName, attempt + 1, policy.MaxRetries, retryDelay.TotalSeconds, ex.Message);
                        InvokeOnRetry(policy, providerName, attempt + 1, retryDelay, ex);
                        shouldRetry = true;
                        break;
                    }

                    if (!hasNext) yield break;
                    yield return enumerator.Current;
                    chunksYielded++;
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            if (!shouldRetry) yield break;
            await Task.Delay(retryDelay, cancellationToken);
        }
    }
}
