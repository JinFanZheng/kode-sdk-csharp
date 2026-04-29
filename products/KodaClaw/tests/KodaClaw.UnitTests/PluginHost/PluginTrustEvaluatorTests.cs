using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Plugins;
using KodaClaw.PluginHost.Trust;
using Xunit;

namespace KodaClaw.UnitTests.PluginHost;

public sealed class PluginTrustEvaluatorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Evaluate_should_capture_local_digest_when_signature_sidecar_is_missing()
    {
        using var pluginRoot = new TempPluginRoot();
        await WriteManifestAsync(pluginRoot.Path, "plugin.digest-only");
        var evaluator = new PluginTrustEvaluator();

        var evidence = await evaluator.EvaluateAsync(pluginRoot.Path);

        evidence.Source.Should().Be(PluginTrustEvidenceSource.LocalDigest);
        evidence.VerificationState.Should().Be(PluginTrustVerificationState.DigestOnly);
        evidence.ManifestDigestSha256.Should().NotBeNullOrWhiteSpace();
        evidence.PackageDigestSha256.Should().NotBeNullOrWhiteSpace();
        evidence.Signer.Should().BeNull();
    }

    [Fact]
    public async Task Evaluate_should_mark_signature_sidecar_as_verified_when_digests_match()
    {
        using var pluginRoot = new TempPluginRoot();
        await WriteManifestAsync(pluginRoot.Path, "plugin.signed");
        await WriteSignatureSidecarAsync(pluginRoot.Path, signer: "Fixture Publisher");
        var evaluator = new PluginTrustEvaluator();

        var evidence = await evaluator.EvaluateAsync(pluginRoot.Path);

        evidence.Source.Should().Be(PluginTrustEvidenceSource.SignatureSidecar);
        evidence.VerificationState.Should().Be(PluginTrustVerificationState.Verified);
        evidence.Signer.Should().Be("Fixture Publisher");
        evidence.SignatureFilePath.Should().EndWith(PluginTrustEvaluator.SignatureSidecarFileName);
    }

    [Fact]
    public async Task Evaluate_should_mark_signature_sidecar_as_mismatch_when_plugin_changes()
    {
        using var pluginRoot = new TempPluginRoot();
        await WriteManifestAsync(pluginRoot.Path, "plugin.mismatch");
        await WriteSignatureSidecarAsync(pluginRoot.Path, signer: "Fixture Publisher");
        await File.AppendAllTextAsync(
            Path.Combine(pluginRoot.Path, "extra.txt"),
            "tampered");
        var evaluator = new PluginTrustEvaluator();

        var evidence = await evaluator.EvaluateAsync(pluginRoot.Path);

        evidence.Source.Should().Be(PluginTrustEvidenceSource.SignatureSidecar);
        evidence.VerificationState.Should().Be(PluginTrustVerificationState.Mismatch);
    }

    private static async Task WriteManifestAsync(string pluginRoot, string pluginId)
    {
        Directory.CreateDirectory(pluginRoot);

        var manifest = new PluginManifest(
            Id: pluginId,
            Name: $"Plugin {pluginId}",
            Version: "0.1.0",
            Types: [PluginType.Tool],
            Runtime: new PluginRuntimeSpec(
                Transport: PluginTransportKind.Stdio,
                Command: "dotnet",
                Args: ["fixture.dll"]),
            Permissions: new PluginPermissionSet(Network: true),
            Capabilities: new PluginCapabilitySet(Tools: ["echo"]));

        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        await File.WriteAllTextAsync(
            Path.Combine(pluginRoot, PluginTrustEvaluator.ManifestFileName),
            json);
    }

    private static async Task WriteSignatureSidecarAsync(string pluginRoot, string signer)
    {
        var evaluator = new PluginTrustEvaluator();
        var digestOnly = await evaluator.EvaluateAsync(pluginRoot);

        var payload = new
        {
            manifestDigestSha256 = digestOnly.ManifestDigestSha256,
            packageDigestSha256 = digestOnly.PackageDigestSha256,
            signer,
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await File.WriteAllTextAsync(
            Path.Combine(pluginRoot, PluginTrustEvaluator.SignatureSidecarFileName),
            json);
    }

    private sealed class TempPluginRoot : IDisposable
    {
        public TempPluginRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "kodaclaw-plugin-trust-evaluator",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
