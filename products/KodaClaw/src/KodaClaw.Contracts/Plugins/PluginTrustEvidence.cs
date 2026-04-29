using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Plugins;

[JsonConverter(typeof(JsonStringEnumConverter<PluginTrustEvidenceSource>))]
public enum PluginTrustEvidenceSource
{
    LocalDigest = 0,
    SignatureSidecar = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter<PluginTrustVerificationState>))]
public enum PluginTrustVerificationState
{
    DigestOnly = 0,
    Verified = 1,
    Mismatch = 2,
    Invalid = 3,
}

public sealed record PluginTrustEvidence(
    PluginTrustEvidenceSource Source,
    PluginTrustVerificationState VerificationState,
    string Summary,
    DateTimeOffset VerifiedAt,
    string? ManifestDigestSha256 = null,
    string? PackageDigestSha256 = null,
    string? Signer = null,
    string? SignatureFilePath = null);
