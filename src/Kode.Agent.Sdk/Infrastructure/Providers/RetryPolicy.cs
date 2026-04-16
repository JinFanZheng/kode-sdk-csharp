namespace Kode.Agent.Sdk.Infrastructure.Providers;

/// <summary>
/// Context passed to <see cref="RetryPolicy.OnRetry"/> on each retry attempt.
/// </summary>
public sealed record RetryAttemptContext
{
    /// <summary>Name of the model provider (e.g. "anthropic", "openai").</summary>
    public required string ProviderName { get; init; }

    /// <summary>1-based retry attempt number.</summary>
    public required int Attempt { get; init; }

    /// <summary>Maximum retries configured for this policy.</summary>
    public required int MaxRetries { get; init; }

    /// <summary>Computed back-off delay before the next attempt.</summary>
    public required TimeSpan Delay { get; init; }

    /// <summary>Truncated error message that triggered the retry.</summary>
    public required string ErrorMessage { get; init; }
}

/// <summary>
/// Configuration for provider-level retry with exponential back-off.
/// </summary>
public sealed record RetryPolicy
{
    /// <summary>Maximum number of retry attempts (not counting the initial attempt).</summary>
    public int MaxRetries { get; init; } = 10;

    /// <summary>Delay before the first retry.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound on the computed back-off delay per attempt.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Wall-clock budget for all retry attempts combined.
    /// Once elapsed, no further retries are attempted regardless of <see cref="MaxRetries"/>.
    /// Guards against session-lock starvation when the model API is slow to recover.
    /// </summary>
    public TimeSpan MaxTotalRetryDuration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Exponential growth factor applied between attempts.</summary>
    public double BackoffMultiplier { get; init; } = 2.0;

    /// <summary>Fractional jitter applied to the computed delay (0.1 = ±10 %).</summary>
    public double JitterFactor { get; init; } = 0.1;

    /// <summary>Default policy: 10 retries, 1 s → 30 s cap, 60 s total budget.</summary>
    public static RetryPolicy Default { get; } = new();

    /// <summary>Disables all retries (pass to opt out per-provider).</summary>
    public static RetryPolicy NoRetry { get; } = new() { MaxRetries = 0 };

    /// <summary>
    /// Optional callback invoked before each retry delay.
    /// Receives a <see cref="RetryAttemptContext"/> with attempt number, delay, and error details.
    /// Use this to emit diagnostics, surface retry state to users, or send typing indicators.
    /// Exceptions thrown by the callback are silently swallowed to protect the retry loop.
    /// </summary>
    public Action<RetryAttemptContext>? OnRetry { get; init; }
}
