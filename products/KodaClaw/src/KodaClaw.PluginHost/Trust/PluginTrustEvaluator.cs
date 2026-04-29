using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Plugins;

namespace KodaClaw.PluginHost.Trust;

public interface IPluginTrustEvaluator
{
    Task<PluginTrustEvidence> EvaluateAsync(
        string pluginRoot,
        CancellationToken cancellationToken = default);
}

public sealed class PluginTrustEvaluator : IPluginTrustEvaluator
{
    public const string ManifestFileName = "plugin.json";
    public const string SignatureSidecarFileName = "plugin.signature.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PluginTrustEvidence> EvaluateAsync(
        string pluginRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pluginRoot))
        {
            throw new ArgumentException("Plugin root path is required.", nameof(pluginRoot));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var normalizedRoot = Path.GetFullPath(pluginRoot);
        if (!Directory.Exists(normalizedRoot))
        {
            return BuildInvalidEvidence(
                PluginTrustEvidenceSource.LocalDigest,
                summary: $"Plugin root was not found: '{normalizedRoot}'.");
        }

        var manifestPath = Path.Combine(normalizedRoot, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return BuildInvalidEvidence(
                PluginTrustEvidenceSource.LocalDigest,
                summary: $"Plugin manifest was not found: '{manifestPath}'.");
        }

        try
        {
            var manifestDigest = await ComputeFileDigestAsync(manifestPath, cancellationToken);
            var packageDigest = await ComputePackageDigestAsync(normalizedRoot, cancellationToken);
            var signaturePath = Path.Combine(normalizedRoot, SignatureSidecarFileName);

            if (!File.Exists(signaturePath))
            {
                return new PluginTrustEvidence(
                    Source: PluginTrustEvidenceSource.LocalDigest,
                    VerificationState: PluginTrustVerificationState.DigestOnly,
                    Summary: "Local digest captured. No signature sidecar was found.",
                    VerifiedAt: DateTimeOffset.UtcNow,
                    ManifestDigestSha256: manifestDigest,
                    PackageDigestSha256: packageDigest);
            }

            PluginSignatureSidecar? sidecar;
            try
            {
                await using var signatureStream = File.OpenRead(signaturePath);
                sidecar = await JsonSerializer.DeserializeAsync<PluginSignatureSidecar>(
                    signatureStream,
                    JsonOptions,
                    cancellationToken);
            }
            catch (JsonException)
            {
                return BuildInvalidEvidence(
                    PluginTrustEvidenceSource.SignatureSidecar,
                    summary: $"Signature sidecar could not be parsed: '{signaturePath}'.",
                    manifestDigestSha256: manifestDigest,
                    packageDigestSha256: packageDigest,
                    signatureFilePath: signaturePath);
            }

            if (sidecar is null ||
                string.IsNullOrWhiteSpace(sidecar.ManifestDigestSha256) ||
                string.IsNullOrWhiteSpace(sidecar.PackageDigestSha256))
            {
                return BuildInvalidEvidence(
                    PluginTrustEvidenceSource.SignatureSidecar,
                    summary: $"Signature sidecar is missing required digest fields: '{signaturePath}'.",
                    manifestDigestSha256: manifestDigest,
                    packageDigestSha256: packageDigest,
                    signer: sidecar?.Signer,
                    signatureFilePath: signaturePath);
            }

            var manifestMatches = DigestsMatch(sidecar.ManifestDigestSha256, manifestDigest);
            var packageMatches = DigestsMatch(sidecar.PackageDigestSha256, packageDigest);
            var verificationState = manifestMatches && packageMatches
                ? PluginTrustVerificationState.Verified
                : PluginTrustVerificationState.Mismatch;
            var summary = verificationState == PluginTrustVerificationState.Verified
                ? "Signature sidecar matched the current manifest and package digests."
                : "Signature sidecar did not match the current plugin contents.";

            return new PluginTrustEvidence(
                Source: PluginTrustEvidenceSource.SignatureSidecar,
                VerificationState: verificationState,
                Summary: summary,
                VerifiedAt: DateTimeOffset.UtcNow,
                ManifestDigestSha256: manifestDigest,
                PackageDigestSha256: packageDigest,
                Signer: NormalizeNullableText(sidecar.Signer),
                SignatureFilePath: signaturePath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TaskCanceledException)
        {
            return BuildInvalidEvidence(
                PluginTrustEvidenceSource.LocalDigest,
                summary: ex.GetBaseException().Message);
        }
    }

    private static PluginTrustEvidence BuildInvalidEvidence(
        PluginTrustEvidenceSource source,
        string summary,
        string? manifestDigestSha256 = null,
        string? packageDigestSha256 = null,
        string? signer = null,
        string? signatureFilePath = null)
    {
        return new PluginTrustEvidence(
            Source: source,
            VerificationState: PluginTrustVerificationState.Invalid,
            Summary: summary,
            VerifiedAt: DateTimeOffset.UtcNow,
            ManifestDigestSha256: manifestDigestSha256,
            PackageDigestSha256: packageDigestSha256,
            Signer: signer,
            SignatureFilePath: signatureFilePath);
    }

    private static bool DigestsMatch(string expected, string actual)
    {
        return string.Equals(NormalizeDigest(expected), NormalizeDigest(actual), StringComparison.Ordinal);
    }

    private static string NormalizeDigest(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private static string? NormalizeNullableText(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static async Task<string> ComputeFileDigestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string> ComputePackageDigestAsync(
        string pluginRoot,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = Directory.EnumerateFiles(pluginRoot, "*", SearchOption.AllDirectories)
            .Select(path => new
            {
                FullPath = path,
                RelativePath = Path.GetRelativePath(pluginRoot, path).Replace('\\', '/'),
            })
            .Where(file => !string.Equals(file.RelativePath, SignatureSidecarFileName, StringComparison.Ordinal))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileDigest = await ComputeFileDigestAsync(file.FullPath, cancellationToken);
            AppendUtf8(hash, file.RelativePath);
            AppendUtf8(hash, "\n");
            AppendUtf8(hash, fileDigest);
            AppendUtf8(hash, "\n");
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendUtf8(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
    }

    private sealed record PluginSignatureSidecar(
        string? ManifestDigestSha256,
        string? PackageDigestSha256,
        string? Signer);
}
