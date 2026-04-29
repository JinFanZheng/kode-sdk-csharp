using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Plugins;
using KodaClaw.Storage.Json.Repositories;
using Xunit;

namespace KodaClaw.UnitTests.PluginHost;

public sealed class JsonPluginRegistryRepositoryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private JsonPluginRegistryRepository CreateRepository() => new(_tempDir);

    [Fact]
    public async Task List_should_start_empty()
    {
        var repository = CreateRepository();

        var records = await repository.ListAsync();

        records.Should().BeEmpty();
    }

    [Fact]
    public async Task Upsert_and_get_should_round_trip()
    {
        var repository = CreateRepository();
        var record = BuildRecord(
            id: "plugin.todo",
            type: PluginType.Tool,
            trustState: PluginTrustState.Trusted,
            enabled: true,
            runtimeState: PluginRuntimeState.Running);

        await repository.UpsertAsync(record);
        var stored = await repository.GetByIdAsync(record.Id);

        stored.Should().NotBeNull();
        var storedRecord = stored ?? throw new InvalidOperationException("Stored record was unexpectedly null.");
        storedRecord.Should().BeEquivalentTo(record, options => options
            .Excluding(value => value.Manifest.ConfigSchema));

        storedRecord.Manifest.ConfigSchema.HasValue.Should().Be(record.Manifest.ConfigSchema.HasValue);
        if (storedRecord.Manifest.ConfigSchema.HasValue && record.Manifest.ConfigSchema.HasValue)
        {
            // JsonElement round-trips through JSON serialization; compare via normalised raw text
            var storedNormalized = JsonSerializer.Serialize(storedRecord.Manifest.ConfigSchema.Value);
            var expectedNormalized = JsonSerializer.Serialize(record.Manifest.ConfigSchema.Value);
            storedNormalized.Should().Be(expectedNormalized);
        }
    }

    [Fact]
    public async Task List_should_apply_filters_for_trust_enabled_runtime_and_type()
    {
        var repository = CreateRepository();
        var now = new DateTimeOffset(2026, 3, 19, 2, 0, 0, TimeSpan.Zero);

        await repository.UpsertAsync(BuildRecord(
            id: "plugin.todo",
            type: PluginType.Tool,
            trustState: PluginTrustState.Trusted,
            enabled: true,
            runtimeState: PluginRuntimeState.Running,
            timestamp: now));
        await repository.UpsertAsync(BuildRecord(
            id: "plugin.memory",
            type: PluginType.Memory,
            trustState: PluginTrustState.Untrusted,
            enabled: false,
            runtimeState: PluginRuntimeState.Stopped,
            timestamp: now.AddMinutes(1)));
        await repository.UpsertAsync(BuildRecord(
            id: "plugin.bridge",
            type: PluginType.Channel,
            trustState: PluginTrustState.Signed,
            enabled: true,
            runtimeState: PluginRuntimeState.Degraded,
            timestamp: now.AddMinutes(2)));

        var strictFiltered = await repository.ListAsync(new PluginQuery(
            Type: PluginType.Tool,
            TrustState: PluginTrustState.Trusted,
            Enabled: true,
            RuntimeState: PluginRuntimeState.Running,
            Limit: 20));

        strictFiltered.Should().ContainSingle();
        strictFiltered[0].Id.Should().Be("plugin.todo");

        var typeFiltered = await repository.ListAsync(new PluginQuery(
            Type: PluginType.Channel,
            Limit: 20));

        typeFiltered.Should().ContainSingle();
        typeFiltered[0].Id.Should().Be("plugin.bridge");

        var signedFiltered = await repository.ListAsync(new PluginQuery(
            TrustState: PluginTrustState.Signed,
            Limit: 20));

        signedFiltered.Should().ContainSingle();
        signedFiltered[0].Id.Should().Be("plugin.bridge");
    }

    [Fact]
    public async Task Delete_should_remove_record()
    {
        var repository = CreateRepository();
        var record = BuildRecord(
            id: "plugin.delete",
            type: PluginType.Tool,
            trustState: PluginTrustState.Trusted,
            enabled: false,
            runtimeState: PluginRuntimeState.Stopped);
        await repository.UpsertAsync(record);

        var deleted = await repository.DeleteAsync(record.Id);
        var list = await repository.ListAsync();

        deleted.Should().BeTrue();
        list.Should().BeEmpty();
    }

    [Fact]
    public async Task Repository_should_persist_plugin_records_to_filesystem()
    {
        var repository = CreateRepository();
        await repository.UpsertAsync(BuildRecord(
            id: "plugin.table-init",
            type: PluginType.Tool,
            trustState: PluginTrustState.Trusted,
            enabled: true,
            runtimeState: PluginRuntimeState.Running));

        var stored = await repository.GetByIdAsync("plugin.table-init");
        stored.Should().NotBeNull();
        stored!.Id.Should().Be("plugin.table-init");
    }

    private static PluginRecord BuildRecord(
        string id,
        PluginType type,
        PluginTrustState trustState,
        bool enabled,
        PluginRuntimeState runtimeState,
        DateTimeOffset? timestamp = null)
    {
        var now = timestamp ?? DateTimeOffset.UtcNow;
        using var document = JsonDocument.Parse("""{"type":"object","title":"PluginConfig"}""");

        var manifest = new PluginManifest(
            Id: id,
            Name: $"Plugin {id}",
            Version: "0.1.0",
            Types: [type],
            Runtime: new PluginRuntimeSpec(
                Transport: PluginTransportKind.Stdio,
                Command: "node",
                Args: ["index.js"],
                Environment: new Dictionary<string, string> { ["PLUGIN_ENV"] = "test" },
                Url: null,
                Headers: new Dictionary<string, string> { ["X-Plugin-Header"] = "static" },
                EnvironmentReferences: new Dictionary<string, string> { ["PLUGIN_SECRET"] = "keychain:plugins:test-secret" },
                HeaderReferences: new Dictionary<string, string> { ["Authorization"] = "env:PLUGIN_HEADER_TOKEN" }),
            Permissions: new PluginPermissionSet(
                Filesystem: ["workspace/plugins"],
                Network: true,
                Notifications: false,
                Background: true,
                Channels: null,
                UiPanels: null,
                Secrets: ["plugin.secret"]),
            Capabilities: new PluginCapabilitySet(
                Tools: ["run_task"],
                Channels: null,
                UiPanels: null,
                MemoryProviders: null),
            Display: new PluginDisplaySpec(
                Description: "Test plugin",
                Icon: "icon.png",
                AccentColor: "#333333"),
            Healthcheck: new PluginHealthcheckSpec(
                ToolName: "run_task",
                IntervalSeconds: 30,
                TimeoutSeconds: 5),
            ConfigSchema: document.RootElement.Clone());

        return new PluginRecord(
            Id: id,
            Manifest: manifest,
            InstallSource: PluginInstallSource.LocalDirectory,
            RootPath: $"workspace/plugins/{id}",
            TrustState: trustState,
            Enabled: enabled,
            RuntimeState: runtimeState,
            DiscoveredAt: now,
            InstalledAt: now,
            UpdatedAt: now,
            LastStartedAt: enabled ? now.AddMinutes(1) : null,
            LastStoppedAt: enabled ? null : now.AddMinutes(1),
            LastHealthAt: now.AddMinutes(2),
            RestartCount: enabled ? 1 : 0,
            LastError: runtimeState is PluginRuntimeState.Degraded ? "healthcheck failed" : null,
            TrustEvidence: new PluginTrustEvidence(
                Source: trustState == PluginTrustState.Signed
                    ? PluginTrustEvidenceSource.SignatureSidecar
                    : PluginTrustEvidenceSource.LocalDigest,
                VerificationState: trustState == PluginTrustState.Signed
                    ? PluginTrustVerificationState.Verified
                    : PluginTrustVerificationState.DigestOnly,
                Summary: trustState == PluginTrustState.Signed
                    ? "Signature sidecar matched the current manifest and package digests."
                    : "Local digest captured. No signature sidecar was found.",
                VerifiedAt: now.AddMinutes(3),
                ManifestDigestSha256: $"{id}-manifest",
                PackageDigestSha256: $"{id}-package",
                Signer: trustState == PluginTrustState.Signed ? "Fixture Publisher" : null,
                SignatureFilePath: trustState == PluginTrustState.Signed
                    ? $"workspace/plugins/{id}/plugin.signature.json"
                    : null));
    }
}
