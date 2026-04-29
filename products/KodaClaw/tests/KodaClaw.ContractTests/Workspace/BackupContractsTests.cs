using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Backup;
using KodaClaw.Contracts.Repair;
using Xunit;

namespace KodaClaw.ContractTests.Workspace;

public sealed class BackupContractsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Backup_import_preflight_should_json_round_trip()
    {
        var payload = new BackupImportPreflightResponse(
            EvaluatedAt: new DateTimeOffset(2026, 3, 19, 12, 0, 0, TimeSpan.Zero),
            WorkspaceRootPath: "/tmp/kodaclaw-target",
            ArchivePath: "/tmp/kodaclaw-target/config/backups/kodaclaw-backup-20260319-120000.zip",
            Manifest: new BackupManifest(
                Product: "KodaClaw",
                FormatVersion: 1,
                WorkspaceVersion: 1,
                GeneratedAt: new DateTimeOffset(2026, 3, 19, 11, 55, 0, TimeSpan.Zero),
                ArchiveName: "kodaclaw-backup-20260319-120000.zip",
                SourceWorkspaceRoot: "/tmp/kodaclaw-source",
                SourceDevice: new BackupDeviceIdentitySnapshot(
                    DeviceId: "device-backup-001",
                    FingerprintHash: "fingerprint-backup-001",
                    AppVersion: "1.0.0",
                    LastSeenAtUtc: "2026-03-19T11:50:00.0000000+00:00"),
                Entries:
                [
                    new BackupManifestEntry(
                        Path: "config/control-plane.db",
                        Sha256: "abc123",
                        SizeBytes: 1024,
                        Category: "config"),
                    new BackupManifestEntry(
                        Path: "sessions/main-001/meta.json",
                        Sha256: "def456",
                        SizeBytes: 256,
                        Category: "sessionMeta"),
                ],
                Includes:
                [
                    "config/control-plane.db (sanitized)",
                    "sessions/*/meta.json",
                ],
                Excludes:
                [
                    "raw secrets and secret values",
                    "messages.json and tool-calls.json session payloads",
                ]),
            ChecksumVerified: true,
            CanImport: false,
            Checklist: new RepairChecklist(
                GeneratedAt: new DateTimeOffset(2026, 3, 19, 12, 0, 0, TimeSpan.Zero),
                Scope: "backupImport",
                Summary: new RepairChecklistSummary(
                    TotalCount: 2,
                    BlockingCount: 1,
                    ActionRequiredCount: 1,
                    WarningCount: 0,
                    InfoCount: 0),
                Items:
                [
                    new RepairChecklistItem(
                        Id: "workspace-pristine-required",
                        Severity: RepairChecklistSeverity.Blocking,
                        State: RepairChecklistState.Pending,
                        Category: "workspace",
                        Title: "Restore target must be a pristine KodaClaw workspace",
                        Summary: "Import v1 restores into an empty workspace only.",
                        Action: "Create a new workspace and retry."),
                    new RepairChecklistItem(
                        Id: "secret-ref-missing:model:model-primary",
                        Severity: RepairChecklistSeverity.ActionRequired,
                        State: RepairChecklistState.Pending,
                        Category: "secrets",
                        Title: "Imported secret reference is not available on this device",
                        Summary: "The model endpoint secret ref must be recreated locally.",
                        Resource: "model_endpoints/model-primary"),
                ]));

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<BackupImportPreflightResponse>(json, JsonOptions);

        json.Should().Contain("\"formatVersion\":1");
        json.Should().Contain("\"severity\":\"Blocking\"");
        json.Should().Contain("\"checksumVerified\":true");
        roundTrip.Should().BeEquivalentTo(payload);
    }
}
