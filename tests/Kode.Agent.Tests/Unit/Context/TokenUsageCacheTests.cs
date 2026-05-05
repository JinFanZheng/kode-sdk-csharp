using System.Net;
using System.Text;
using FluentAssertions;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Sdk.Infrastructure.Providers;
using Xunit;

namespace Kode.Agent.Tests.Unit.Context;

/// <summary>
/// Tests for cache token tracking in TokenUsage and provider parsing.
/// </summary>
public sealed class TokenUsageCacheTests
{
    // ── TokenUsage record ────────────────────────────────────────────────────

    [Fact]
    public void TokenUsage_CacheHitTokens_DefaultsToNull()
    {
        var usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 };
        usage.CacheHitTokens.Should().BeNull();
    }

    [Fact]
    public void TokenUsage_CacheMissTokens_DefaultsToNull()
    {
        var usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 };
        usage.CacheMissTokens.Should().BeNull();
    }

    [Fact]
    public void TokenUsage_CacheTokens_Settable()
    {
        var usage = new TokenUsage
        {
            InputTokens = 100,
            OutputTokens = 50,
            CacheHitTokens = 80,
            CacheMissTokens = 20
        };

        usage.CacheHitTokens.Should().Be(80);
        usage.CacheMissTokens.Should().Be(20);
        usage.InputTokens.Should().Be(100);
    }

    // ── DeepSeekProvider parses cache tokens from API ────────────────────────

    private const string UsageWithCacheJson = """
        {
          "id": "chatcmpl-test",
          "object": "chat.completion",
          "created": 1700000000,
          "model": "deepseek-v4-pro",
          "choices": [{
            "index": 0,
            "message": { "role": "assistant", "content": "Hello" },
            "finish_reason": "stop"
          }],
          "usage": {
            "prompt_tokens": 100,
            "completion_tokens": 50,
            "total_tokens": 150,
            "prompt_cache_hit_tokens": 80,
            "prompt_cache_miss_tokens": 20
          }
        }
        """;

    [Fact]
    public async Task DeepSeekProvider_CompleteAsync_ParsesCacheTokens()
    {
        var handler = new StaticHttpMessageHandler(UsageWithCacheJson, "application/json");
        var provider = new DeepSeekProvider(handler, new DeepSeekOptions { ApiKey = "test-key" });

        var response = await provider.CompleteAsync(new ModelRequest
        {
            Model = "deepseek-v4-pro",
            Messages = [Message.User("Hi")],
            MaxTokens = 100
        });

        response.Usage.CacheHitTokens.Should().Be(80);
        response.Usage.CacheMissTokens.Should().Be(20);
        response.Usage.InputTokens.Should().Be(100);
    }

    [Fact]
    public async Task DeepSeekProvider_StreamAsync_ParsesCacheTokens()
    {
        var sseResponse = """
            data: {"id":"s1","object":"chat.completion.chunk","created":1,"model":"deepseek-v4-pro","choices":[{"index":0,"delta":{"content":"Hi"},"finish_reason":null}]}

            data: {"id":"s1","object":"chat.completion.chunk","created":1,"model":"deepseek-v4-pro","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":100,"completion_tokens":5,"total_tokens":105,"prompt_cache_hit_tokens":90,"prompt_cache_miss_tokens":10}}

            data: [DONE]

            """;

        var handler = new StaticHttpMessageHandler(sseResponse, "text/event-stream");
        var provider = new DeepSeekProvider(handler, new DeepSeekOptions { ApiKey = "test-key" });

        var chunks = new List<StreamChunk>();
        await foreach (var chunk in provider.StreamAsync(new ModelRequest
        {
            Model = "deepseek-v4-pro",
            Messages = [Message.User("Hi")],
            MaxTokens = 100
        }))
        {
            chunks.Add(chunk);
        }

        var stopChunk = chunks.Should().Contain(c => c.Type == StreamChunkType.MessageStop
            && c.Usage != null).Which;
        stopChunk.Usage!.CacheHitTokens.Should().Be(90);
        stopChunk.Usage.CacheMissTokens.Should().Be(10);
    }

    // ── Usage without cache tokens (backward compatibility) ──────────────────

    private const string UsageWithoutCacheJson = """
        {
          "id": "chatcmpl-test",
          "object": "chat.completion",
          "created": 1700000000,
          "model": "deepseek-chat",
          "choices": [{
            "index": 0,
            "message": { "role": "assistant", "content": "Hello" },
            "finish_reason": "stop"
          }],
          "usage": {
            "prompt_tokens": 50,
            "completion_tokens": 25,
            "total_tokens": 75
          }
        }
        """;

    [Fact]
    public async Task DeepSeekProvider_UsageWithoutCache_LeavesCacheNull()
    {
        var handler = new StaticHttpMessageHandler(UsageWithoutCacheJson, "application/json");
        var provider = new DeepSeekProvider(handler, new DeepSeekOptions { ApiKey = "test-key" });

        var response = await provider.CompleteAsync(new ModelRequest
        {
            Model = "deepseek-chat",
            Messages = [Message.User("Hi")],
            MaxTokens = 100
        });

        response.Usage.CacheHitTokens.Should().BeNull();
        response.Usage.CacheMissTokens.Should().BeNull();
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private sealed class StaticHttpMessageHandler(string responseJson, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, mediaType)
            });
        }
    }
}
