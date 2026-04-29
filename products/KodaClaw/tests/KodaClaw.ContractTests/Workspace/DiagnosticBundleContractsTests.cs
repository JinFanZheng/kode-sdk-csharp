using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.System;
using Xunit;

namespace KodaClaw.ContractTests.Workspace;

public sealed class DiagnosticBundleContractsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Diagnostic_bundle_export_should_json_round_trip()
    {
        var payload = new DiagnosticBundleExportResponse(
            GeneratedAt: new DateTimeOffset(2026, 3, 19, 3, 0, 0, TimeSpan.Zero),
            WorkspaceRootPath: "/tmp/.kodaclaw",
            BundlePath: "/tmp/.kodaclaw/cache/diagnostics/kodaclaw-diagnostic-bundle-20260319-030000.zip",
            Manifest: new DiagnosticBundleManifest(
                Product: "KodaClaw",
                FormatVersion: 1,
                GeneratedAt: new DateTimeOffset(2026, 3, 19, 3, 0, 0, TimeSpan.Zero),
                ArchiveName: "kodaclaw-diagnostic-bundle-20260319-030000.zip",
                SourceWorkspaceRoot: "/tmp/.kodaclaw",
                RequestedSessionId: "main-001",
                DesktopContext: new DiagnosticBundleDesktopContext(
                    DesktopMode: true,
                    Platform: "darwin",
                    AppVersion: "0.1.0",
                    ReleaseChannel: UpdateReleaseChannel.Stable,
                    GatewayLifecycleMode: "ManagedChild"),
                RedactionSummary: new DiagnosticBundleRedactionSummary(
                    IncludesRawSecrets: false,
                    IncludesMessageBodies: false,
                    AppliedRules:
                    [
                        "secret-like diagnostics attribute values are replaced with [REDACTED]",
                        "session exports include meta.json only; messages.json and tool-calls.json stay excluded",
                    ],
                    Notes:
                    [
                        "Timeline export is scoped to session 'main-001'.",
                    ]),
                Entries:
                [
                    new DiagnosticBundleManifestEntry(
                        Path: "snapshot/diagnostics/timeline.json",
                        Sha256: "abc123",
                        SizeBytes: 512,
                        Category: "diagnostics"),
                    new DiagnosticBundleManifestEntry(
                        Path: "snapshot/sessions/main-001/meta.json",
                        Sha256: "def456",
                        SizeBytes: 256,
                        Category: "sessionMeta"),
                ],
                Includes:
                [
                    "diagnostics recent/timeline exports",
                    "session meta.json files",
                ],
                Excludes:
                [
                    "raw secrets and secret values",
                    "messages.json and tool-calls.json session payloads",
                ],
                Notes:
                [
                    "Desktop runtime context was supplied by the renderer.",
                ]));

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<DiagnosticBundleExportResponse>(json, JsonOptions);

        json.Should().Contain("\"bundlePath\"");
        json.Should().Contain("\"releaseChannel\":\"Stable\"");
        json.Should().Contain("\"includesRawSecrets\":false");
        roundTrip.Should().NotBeNull();
        roundTrip!.Manifest.Entries.Should().HaveCount(2);
        roundTrip.Manifest.RequestedSessionId.Should().Be("main-001");
        roundTrip.Manifest.RedactionSummary.IncludesMessageBodies.Should().BeFalse();
        roundTrip.Manifest.DesktopContext!.Platform.Should().Be("darwin");
    }
}
