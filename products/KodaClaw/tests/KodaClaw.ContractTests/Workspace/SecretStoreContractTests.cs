using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Secrets;
using KodaClaw.Workspace;
using KodaClaw.Workspace.Secrets;
using Xunit;

namespace KodaClaw.ContractTests.Workspace;

public sealed class SecretStoreContractTests
{
    [Fact]
    public void Secret_ref_should_round_trip_reference_string()
    {
        var secretRef = new SecretRef("keychain", "gateway", "desktop-token", "Desktop Gateway Token");

        secretRef.ToReferenceString().Should().Be("keychain:gateway:desktop-token");
        SecretRef.TryParse(secretRef.ToReferenceString(), out var reparsed).Should().BeTrue();
        reparsed.Should().Be(new SecretRef("keychain", "gateway", "desktop-token"));
    }

    [Fact]
    public async Task Platform_secret_store_should_round_trip_memory_provider()
    {
        var store = new PlatformSecretStore(new FakePlatformKeychain());
        var secretRef = new SecretRef("memory", "tests", "gateway-token");

        await store.UpsertAsync(secretRef, "memory-secret");

        var resolved = await store.GetAsync(secretRef);
        var descriptor = await store.DescribeAsync(secretRef);

        resolved.Should().Be("memory-secret");
        descriptor.Exists.Should().BeTrue();
        descriptor.IsReadOnly.Should().BeFalse();
        descriptor.UpdatedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Platform_secret_store_should_read_environment_provider_but_refuse_mutation()
    {
        var secretRef = new SecretRef("env", "process", "KODACLAW_SECRET_STORE_TEST_ENV");
        Environment.SetEnvironmentVariable(secretRef.Key, "env-secret");

        try
        {
            var store = new PlatformSecretStore(new FakePlatformKeychain());

            var resolved = await store.GetAsync(secretRef);
            var descriptor = await store.DescribeAsync(secretRef);
            var upsert = () => store.UpsertAsync(secretRef, "new-secret");
            var delete = () => store.DeleteAsync(secretRef);

            resolved.Should().Be("env-secret");
            descriptor.Exists.Should().BeTrue();
            descriptor.IsReadOnly.Should().BeTrue();
            await upsert.Should().ThrowAsync<InvalidOperationException>();
            await delete.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretRef.Key, null);
        }
    }

    [Fact]
    public async Task Platform_secret_store_should_delegate_keychain_provider_to_runner()
    {
        var runner = new FakePlatformKeychain();
        var store = new PlatformSecretStore(runner);
        var secretRef = new SecretRef("keychain", "models", "default-openai", "Default OpenAI Key");

        await store.UpsertAsync(secretRef, "keychain-secret");
        var resolved = await store.GetAsync(secretRef);
        var descriptor = await store.DescribeAsync(secretRef);
        await store.DeleteAsync(secretRef);

        runner.Writes.Should().ContainSingle();
        runner.Reads.Should().ContainSingle();
        runner.Deletes.Should().ContainSingle();
        resolved.Should().Be("keychain-secret");
        descriptor.Exists.Should().BeTrue();
        // StorageDisplayName comes from the keychain implementation; verify it is non-empty.
        descriptor.StorageDisplayName.Should().NotBeNullOrWhiteSpace();
    }

    private sealed class FakePlatformKeychain : IPlatformKeychain
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string StorageDisplayName => "Test Keychain";

        public List<SecretRef> Reads { get; } = [];

        public List<SecretRef> Writes { get; } = [];

        public List<SecretRef> Deletes { get; } = [];

        public Task<string?> ReadAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
        {
            Reads.Add(secretRef);
            _values.TryGetValue(secretRef.ToReferenceString(), out var value);
            return Task.FromResult<string?>(value);
        }

        public Task WriteAsync(SecretRef secretRef, string secretValue, CancellationToken cancellationToken = default)
        {
            Writes.Add(secretRef);
            _values[secretRef.ToReferenceString()] = secretValue;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
        {
            Deletes.Add(secretRef);
            _values.Remove(secretRef.ToReferenceString());
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_values.ContainsKey(secretRef.ToReferenceString()));
        }
    }
}
