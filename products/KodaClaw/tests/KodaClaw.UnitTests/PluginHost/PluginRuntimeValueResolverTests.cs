using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Secrets;
using KodaClaw.PluginHost.Hosting;
using Xunit;

namespace KodaClaw.UnitTests.PluginHost;

public sealed class PluginRuntimeValueResolverTests
{
    [Fact]
    public void Resolve_should_merge_literal_values_and_reference_values_with_reference_precedence()
    {
        const string environmentKey = "KODACLAW_PLUGIN_RESOLVER_TEST_ENV";
        Environment.SetEnvironmentVariable(environmentKey, "host-value");

        try
        {
            var secretRef = new SecretRef("memory", "plugins", "fixture-token");
            var resolver = new PluginRuntimeValueResolver(new FakeSecretStore(new Dictionary<string, string?>
            {
                [secretRef.ToReferenceString()] = "secret-value",
            }));

            var resolved = resolver.Resolve(
                literalValues: new Dictionary<string, string>
                {
                    ["PLAIN"] = "literal-value",
                    ["SHARED"] = "literal-shared",
                },
                referenceValues: new Dictionary<string, string>
                {
                    ["FROM_SECRET"] = secretRef.ToReferenceString(),
                    ["FROM_ENV"] = $"env:{environmentKey}",
                    ["SHARED"] = "value:resolved-shared",
                });

            resolved.Should().BeEquivalentTo(new Dictionary<string, string>
            {
                ["PLAIN"] = "literal-value",
                ["FROM_SECRET"] = "secret-value",
                ["FROM_ENV"] = "host-value",
                ["SHARED"] = "resolved-shared",
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentKey, null);
        }
    }

    [Fact]
    public void Resolve_should_skip_missing_secret_refs_and_allow_header_reference_fallbacks()
    {
        var resolver = new PluginRuntimeValueResolver(new FakeSecretStore(new Dictionary<string, string?>()));

        var resolved = resolver.Resolve(
            literalValues: new Dictionary<string, string>
            {
                ["X-Static"] = "always-here",
            },
            referenceValues: new Dictionary<string, string>
            {
                ["Authorization"] = "inline:Bearer fixture-token",
                ["X-Missing"] = "memory:plugins:missing",
            });

        resolved.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["X-Static"] = "always-here",
            ["Authorization"] = "Bearer fixture-token",
        });
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly IReadOnlyDictionary<string, string?> _values;

        public FakeSecretStore(IReadOnlyDictionary<string, string?> values)
        {
            _values = values;
        }

        public Task<string?> GetAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
        {
            _values.TryGetValue(secretRef.ToReferenceString(), out var value);
            return Task.FromResult(value);
        }

        public Task UpsertAsync(SecretRef secretRef, string secretValue, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task DeleteAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SecretDescriptor> DescribeAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
