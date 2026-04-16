using System.Net;
using FluentAssertions;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Infrastructure.Providers;
using Xunit;

namespace Kode.Agent.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="ProviderRetryHelper"/>.
/// Covers: IsRetryable detection, ComputeDelay back-off, ExecuteWithRetryAsync, StreamWithRetryAsync.
/// </summary>
public sealed class ProviderRetryHelperTests
{
    // ── IsRetryable ───────────────────────────────────────────────────────────

    [Fact]
    public void IsRetryable_OperationCanceled_ReturnsFalse()
    {
        ProviderRetryHelper.IsRetryable(new OperationCanceledException())
            .Should().BeFalse();
    }

    [Fact]
    public void IsRetryable_TaskCanceled_ReturnsFalse()
    {
        ProviderRetryHelper.IsRetryable(new TaskCanceledException())
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("429")]
    [InlineData("529")]
    [InlineData("503")]
    [InlineData("rate limit exceeded")]
    [InlineData("Rate Limit")]
    [InlineData("model overloaded")]
    [InlineData("Overloaded")]
    [InlineData("访问量过大")]
    [InlineData("您的账户已达到速率限制")]
    [InlineData("request timed out")]
    [InlineData("Timeout")]
    public void IsRetryable_RetryableMessagePatterns_ReturnsTrue(string message)
    {
        ProviderRetryHelper.IsRetryable(new HttpRequestException(message))
            .Should().BeTrue(because: $"message '{message}' is a retryable pattern");
    }

    [Theory]
    [InlineData("401 Unauthorized")]
    [InlineData("400 Bad Request")]
    [InlineData("invalid_api_key")]
    [InlineData("content policy violation")]
    public void IsRetryable_NonRetryableMessages_ReturnsFalse(string message)
    {
        ProviderRetryHelper.IsRetryable(new HttpRequestException(message))
            .Should().BeFalse(because: $"message '{message}' is not retryable");
    }

    [Fact]
    public void IsRetryable_HttpRequestException_With429StatusCode_ReturnsTrue()
    {
        var ex = new HttpRequestException("Too Many Requests", null, HttpStatusCode.TooManyRequests);
        ProviderRetryHelper.IsRetryable(ex).Should().BeTrue();
    }

    [Fact]
    public void IsRetryable_HttpRequestException_With503StatusCode_ReturnsTrue()
    {
        var ex = new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable);
        ProviderRetryHelper.IsRetryable(ex).Should().BeTrue();
    }

    [Fact]
    public void IsRetryable_HttpRequestException_With401StatusCode_ReturnsFalse()
    {
        var ex = new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);
        ProviderRetryHelper.IsRetryable(ex).Should().BeFalse();
    }

    [Fact]
    public void IsRetryable_ClientResultException_With429InMessage_ReturnsTrue()
    {
        // ClientResultException.Status comes from PipelineResponse, which is hard to construct in tests.
        // Fall back to verifying the string-pattern path covers OpenAI error messages.
        var ex = new System.ClientModel.ClientResultException("429 rate_limit_exceeded", null, null);
        ProviderRetryHelper.IsRetryable(ex).Should().BeTrue();
    }

    // ── ComputeDelay ──────────────────────────────────────────────────────────

    [Fact]
    public void ComputeDelay_Attempt0_ReturnsApproximatelyInitialDelay()
    {
        var policy = new RetryPolicy { InitialDelay = TimeSpan.FromSeconds(2), JitterFactor = 0 };
        var delay = ProviderRetryHelper.ComputeDelay(0, new Exception("err"), policy);
        delay.Should().BeCloseTo(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void ComputeDelay_ExponentialGrowth_DoublesEachAttempt()
    {
        var policy = new RetryPolicy
        {
            InitialDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 2.0,
            MaxDelay = TimeSpan.FromSeconds(60),
            JitterFactor = 0
        };
        var d0 = ProviderRetryHelper.ComputeDelay(0, new Exception(), policy);
        var d1 = ProviderRetryHelper.ComputeDelay(1, new Exception(), policy);
        var d2 = ProviderRetryHelper.ComputeDelay(2, new Exception(), policy);

        d1.Should().BeCloseTo(d0 * 2, TimeSpan.FromMilliseconds(50));
        d2.Should().BeCloseTo(d0 * 4, TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void ComputeDelay_NeverExceedsMaxDelay()
    {
        var policy = new RetryPolicy
        {
            InitialDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(10),
            JitterFactor = 0
        };
        for (var attempt = 0; attempt <= 15; attempt++)
        {
            var delay = ProviderRetryHelper.ComputeDelay(attempt, new Exception(), policy);
            delay.Should().BeLessThanOrEqualTo(policy.MaxDelay + TimeSpan.FromMilliseconds(50));
        }
    }

    [Fact]
    public void ComputeDelay_RetryAfterInMessage_TakesPriority()
    {
        var policy = new RetryPolicy { InitialDelay = TimeSpan.FromSeconds(1), JitterFactor = 0 };
        var ex = new Exception("rate limit exceeded. Retry-After: 15");
        var delay = ProviderRetryHelper.ComputeDelay(0, ex, policy);
        delay.Should().BeCloseTo(TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void ComputeDelay_RetryAfterExceedsMaxDelay_ClampedToMax()
    {
        var policy = new RetryPolicy { MaxDelay = TimeSpan.FromSeconds(10), JitterFactor = 0 };
        var ex = new Exception("Retry-After: 999");
        var delay = ProviderRetryHelper.ComputeDelay(0, ex, policy);
        delay.Should().BeCloseTo(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(100));
    }

    // ── ExecuteWithRetryAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteWithRetry_SuccessOnFirstAttempt_ReturnsResult()
    {
        var calls = 0;
        var result = await ProviderRetryHelper.ExecuteWithRetryAsync(
            _ => { calls++; return Task.FromResult(42); },
            RetryPolicy.NoRetry, logger: null, "test", CancellationToken.None);

        result.Should().Be(42);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteWithRetry_FailsThenSucceeds_ReturnsOnSuccess()
    {
        var policy = new RetryPolicy
        {
            MaxRetries = 3,
            InitialDelay = TimeSpan.FromMilliseconds(1),
            JitterFactor = 0,
            MaxTotalRetryDuration = TimeSpan.FromSeconds(30)
        };

        var calls = 0;
        var result = await ProviderRetryHelper.ExecuteWithRetryAsync(
            _ =>
            {
                calls++;
                if (calls < 3)
                    throw new HttpRequestException("429 rate limit", null, HttpStatusCode.TooManyRequests);
                return Task.FromResult("ok");
            },
            policy, logger: null, "test", CancellationToken.None);

        result.Should().Be("ok");
        calls.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteWithRetry_NonRetryableError_ThrowsImmediately()
    {
        var policy = new RetryPolicy
        {
            MaxRetries = 5,
            InitialDelay = TimeSpan.FromMilliseconds(1),
            JitterFactor = 0,
            MaxTotalRetryDuration = TimeSpan.FromSeconds(30)
        };

        var calls = 0;
        var act = () => ProviderRetryHelper.ExecuteWithRetryAsync<string>(
            _ =>
            {
                calls++;
                throw new HttpRequestException("401 Unauthorized", null, HttpStatusCode.Unauthorized);
            },
            policy, logger: null, "test", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        calls.Should().Be(1, "non-retryable error should not retry");
    }

    [Fact]
    public async Task ExecuteWithRetry_ExceedsMaxRetries_ThrowsLastException()
    {
        var policy = new RetryPolicy
        {
            MaxRetries = 2,
            InitialDelay = TimeSpan.FromMilliseconds(1),
            JitterFactor = 0,
            MaxTotalRetryDuration = TimeSpan.FromSeconds(30)
        };

        var calls = 0;
        var act = () => ProviderRetryHelper.ExecuteWithRetryAsync<int>(
            _ =>
            {
                calls++;
                throw new HttpRequestException("429", null, HttpStatusCode.TooManyRequests);
            },
            policy, logger: null, "test", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        calls.Should().Be(3, "1 initial + 2 retries");
    }

    [Fact]
    public async Task ExecuteWithRetry_CancellationRequested_ThrowsOperationCanceled()
    {
        var policy = new RetryPolicy
        {
            MaxRetries = 5,
            InitialDelay = TimeSpan.FromMilliseconds(1),
            JitterFactor = 0,
            MaxTotalRetryDuration = TimeSpan.FromSeconds(30)
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => ProviderRetryHelper.ExecuteWithRetryAsync<int>(
            ct => { ct.ThrowIfCancellationRequested(); return Task.FromResult(0); },
            policy, logger: null, "test", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteWithRetry_MaxTotalDurationExceeded_StopsRetrying()
    {
        var policy = new RetryPolicy
        {
            MaxRetries = 100,
            InitialDelay = TimeSpan.FromMilliseconds(1),
            MaxDelay = TimeSpan.FromMilliseconds(1),
            JitterFactor = 0,
            MaxTotalRetryDuration = TimeSpan.FromMilliseconds(50)   // very short budget
        };

        var calls = 0;
        var act = () => ProviderRetryHelper.ExecuteWithRetryAsync<int>(
            _ =>
            {
                calls++;
                throw new HttpRequestException("429", null, HttpStatusCode.TooManyRequests);
            },
            policy, logger: null, "test", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        calls.Should().BeLessThan(100, "retry should stop when duration budget is exhausted");
    }

    // ── StreamWithRetryAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task StreamWithRetry_SuccessFirstAttempt_YieldsAllChunks()
    {
        var chunks = new[] { Chunk("a"), Chunk("b"), Chunk("c") };
        var attempts = 0;

        var results = await CollectAsync(ProviderRetryHelper.StreamWithRetryAsync(
            _ => { attempts++; return AsyncSeq(chunks); },
            RetryPolicy.NoRetry, logger: null, "test", CancellationToken.None));

        results.Should().HaveCount(3);
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task StreamWithRetry_PreStreamErrorThenSuccess_RetriesAndYieldsChunks()
    {
        var policy = new RetryPolicy
        {
            MaxRetries = 3,
            InitialDelay = TimeSpan.FromMilliseconds(1),
            JitterFactor = 0,
            MaxTotalRetryDuration = TimeSpan.FromSeconds(30)
        };

        var attempts = 0;
        var results = await CollectAsync(ProviderRetryHelper.StreamWithRetryAsync(
            _ =>
            {
                attempts++;
                if (attempts < 2)
                    return ThrowBeforeFirstChunk(new HttpRequestException("429", null, HttpStatusCode.TooManyRequests));
                return AsyncSeq([Chunk("hello")]);
            },
            policy, logger: null, "test", CancellationToken.None));

        results.Should().HaveCount(1);
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task StreamWithRetry_ErrorAfterChunkYielded_DoesNotRetry()
    {
        var policy = new RetryPolicy
        {
            MaxRetries = 5,
            InitialDelay = TimeSpan.FromMilliseconds(1),
            JitterFactor = 0,
            MaxTotalRetryDuration = TimeSpan.FromSeconds(30)
        };

        var attempts = 0;
        var act = async () => await CollectAsync(ProviderRetryHelper.StreamWithRetryAsync(
            _ =>
            {
                attempts++;
                return ThrowAfterOneChunk(new HttpRequestException("429", null, HttpStatusCode.TooManyRequests));
            },
            policy, logger: null, "test", CancellationToken.None));

        await act.Should().ThrowAsync<HttpRequestException>();
        attempts.Should().Be(1, "mid-stream error must not retry");
    }

    [Fact]
    public async Task StreamWithRetry_NonRetryablePreStreamError_DoesNotRetry()
    {
        var policy = new RetryPolicy
        {
            MaxRetries = 5,
            InitialDelay = TimeSpan.FromMilliseconds(1),
            JitterFactor = 0,
            MaxTotalRetryDuration = TimeSpan.FromSeconds(30)
        };

        var attempts = 0;
        var act = async () => await CollectAsync(ProviderRetryHelper.StreamWithRetryAsync(
            _ =>
            {
                attempts++;
                return ThrowBeforeFirstChunk(new HttpRequestException("401", null, HttpStatusCode.Unauthorized));
            },
            policy, logger: null, "test", CancellationToken.None));

        await act.Should().ThrowAsync<HttpRequestException>();
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task StreamWithRetry_AllAttemptsExhausted_Throws()
    {
        var policy = new RetryPolicy
        {
            MaxRetries = 2,
            InitialDelay = TimeSpan.FromMilliseconds(1),
            JitterFactor = 0,
            MaxTotalRetryDuration = TimeSpan.FromSeconds(30)
        };

        var attempts = 0;
        var act = async () => await CollectAsync(ProviderRetryHelper.StreamWithRetryAsync(
            _ =>
            {
                attempts++;
                return ThrowBeforeFirstChunk(new HttpRequestException("429", null, HttpStatusCode.TooManyRequests));
            },
            policy, logger: null, "test", CancellationToken.None));

        await act.Should().ThrowAsync<HttpRequestException>();
        attempts.Should().Be(3, "1 initial + 2 retries");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static StreamChunk Chunk(string text) =>
        new() { Type = StreamChunkType.TextDelta, TextDelta = text };

    private static async IAsyncEnumerable<StreamChunk> AsyncSeq(
        IEnumerable<StreamChunk> chunks,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _ = default)
    {
        foreach (var c in chunks)
        {
            await Task.Yield();
            yield return c;
        }
    }

    private static async IAsyncEnumerable<StreamChunk> ThrowBeforeFirstChunk(
        Exception ex,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _ = default)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162 // Unreachable code — required to make this an iterator method
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<StreamChunk> ThrowAfterOneChunk(
        Exception ex,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _ = default)
    {
        await Task.Yield();
        yield return Chunk("first");
        await Task.Yield();
        throw ex;
    }

    private static async Task<List<StreamChunk>> CollectAsync(IAsyncEnumerable<StreamChunk> source)
    {
        var list = new List<StreamChunk>();
        await foreach (var chunk in source)
            list.Add(chunk);
        return list;
    }
}
