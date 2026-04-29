using System.Collections.Generic;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Repair;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.Settings;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KodaClaw.IntegrationTests.Gateway;

public sealed class DiagnosticBundleApiIntegrationTests
{
    private const string GatewayToken = "diagnostic-token";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Diagnostic_bundle_export_should_require_token()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);

        var response = await hosted.Client.PostAsJsonAsync(
            "/api/diagnostics/bundle-export",
            new DiagnosticBundleExportRequest(SessionId: "main-001"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Diagnostic_bundle_export_should_reject_archive_path_outside_workspace()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);
        await SeedWorkspaceAsync(hosted.Services, workspace.Path);

        var outsideArchivePath = Path.Combine(Path.GetTempPath(), $"kodaclaw-diagnostic-outside-{Guid.NewGuid():N}.zip");
        var response = await hosted.Client.PostAsJsonAsync(
            "/api/diagnostics/bundle-export",
            new DiagnosticBundleExportRequest(ArchivePath: outsideArchivePath));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        payload.Should().NotBeNull();
        payload!.Code.Should().Be("validation.archive_path_invalid");
    }

    [Fact]
    public async Task Diagnostic_bundle_export_should_write_redacted_zip_with_manifest_and_selected_session_context()
    {
        using var workspace = new TempWorkspaceRoot();
        await using var hosted = await StartGatewayAsync(workspace.Path);
        hosted.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);

        await SeedWorkspaceAsync(hosted.Services, workspace.Path);
        var diagnosticsService = hosted.Services.GetRequiredService<IDiagnosticsService>();
        diagnosticsService.Record(new DiagnosticEvent(
            Id: "evt-gateway-001",
            Source: "gateway.update",
            EventType: "gateway.update.checked",
            Level: "warning",
            Message: "authorization=Bearer secret-token update check completed",
            Timestamp: new DateTimeOffset(2026, 3, 19, 4, 0, 0, TimeSpan.Zero),
            SessionId: "main-001",
            Attributes: new Dictionary<string, string?>
            {
                ["apiKey"] = "sk-test-secret",
                ["safe"] = "visible",
            }));
        diagnosticsService.Record(new DiagnosticEvent(
            Id: "evt-plugin-001",
            Source: "plugin.health",
            EventType: "plugin.health.degraded",
            Level: "warning",
            Message: "Plugin runtime degraded.",
            Timestamp: new DateTimeOffset(2026, 3, 19, 4, 1, 0, TimeSpan.Zero),
            SessionId: "main-001"));
        diagnosticsService.Record(new DiagnosticEvent(
            Id: "evt-plugin-quoted-001",
            Source: "plugin.health",
            EventType: "plugin.health.quoted_secret",
            Level: "warning",
            Message: "{\"apiKey\":\"sk-json-secret\",\"token\":\"quoted-token\",\"authorization\":\"Bearer quoted-bearer\"}",
            Timestamp: new DateTimeOffset(2026, 3, 19, 4, 1, 30, TimeSpan.Zero),
            SessionId: "main-001"));
        diagnosticsService.Record(new DiagnosticEvent(
            Id: "evt-automation-001",
            Source: "automation.scheduler",
            EventType: "automation.scheduler.tick",
            Level: "info",
            Message: "Scheduler tick completed.",
            Timestamp: new DateTimeOffset(2026, 3, 19, 4, 2, 0, TimeSpan.Zero),
            SessionId: "main-001"));
        diagnosticsService.Record(new DiagnosticEvent(
            Id: "evt-channel-001",
            Source: "channel.delivery",
            EventType: "channel.delivery.approval_created",
            Level: "info",
            Message: "Channel delivery approval queued.",
            Timestamp: new DateTimeOffset(2026, 3, 19, 4, 3, 0, TimeSpan.Zero),
            SessionId: "main-001"));

        var response = await hosted.Client.PostAsJsonAsync(
            "/api/diagnostics/bundle-export",
            new DiagnosticBundleExportRequest(
                SessionId: "main-001",
                TimelineLimit: 80,
                DesktopContext: new DiagnosticBundleDesktopContext(
                    DesktopMode: true,
                    Platform: "darwin",
                    AppVersion: "0.1.0",
                    ReleaseChannel: UpdateReleaseChannel.Stable,
                    GatewayLifecycleMode: "ManagedChild")));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<DiagnosticBundleExportResponse>();

        payload.Should().NotBeNull();
        payload!.Manifest.RequestedSessionId.Should().Be("main-001");
        payload.Manifest.RedactionSummary.IncludesRawSecrets.Should().BeFalse();
        payload.Manifest.DesktopContext.Should().NotBeNull();
        payload.Manifest.Entries.Should().NotBeEmpty();
        File.Exists(payload.BundlePath).Should().BeTrue();

        using var extracted = new TempWorkspaceRoot("kodaclaw-diagnostic-bundle-extract");
        ZipFile.ExtractToDirectory(payload.BundlePath, extracted.Path);

        var manifestPath = Path.Combine(extracted.Path, "manifest.json");
        var redactionPath = Path.Combine(extracted.Path, "redaction-summary.json");
        var settingsPath = Path.Combine(extracted.Path, "snapshot", "control-plane", "settings.json");
        var timelinePath = Path.Combine(extracted.Path, "snapshot", "diagnostics", "timeline.json");
        var sourceSummaryPath = Path.Combine(extracted.Path, "snapshot", "diagnostics", "source-summary.json");
        var logSummaryPath = Path.Combine(extracted.Path, "snapshot", "logs", "log-summary.json");
        var updateStatePath = Path.Combine(extracted.Path, "snapshot", "evidence", KodaClawWorkspaceLayout.UpdateStateFile);
        var desktopContextPath = Path.Combine(extracted.Path, "snapshot", "desktop", "runtime-context.json");
        var sessionMetaPath = Path.Combine(extracted.Path, "snapshot", "sessions", "main-001", "meta.json");

        File.Exists(manifestPath).Should().BeTrue();
        File.Exists(redactionPath).Should().BeTrue();
        File.Exists(settingsPath).Should().BeTrue();
        File.Exists(timelinePath).Should().BeTrue();
        File.Exists(sourceSummaryPath).Should().BeTrue();
        File.Exists(logSummaryPath).Should().BeTrue();
        File.Exists(updateStatePath).Should().BeTrue();
        File.Exists(desktopContextPath).Should().BeTrue();
        File.Exists(sessionMetaPath).Should().BeTrue();
        File.Exists(Path.Combine(extracted.Path, "snapshot", "sessions", "main-001", "messages.json")).Should().BeFalse();

        var timeline = await JsonSerializer.DeserializeAsync<DiagnosticsQueryResponse>(File.OpenRead(timelinePath), JsonOptions);
        timeline.Should().NotBeNull();
        timeline!.Events.Should().ContainSingle(item => item.EventType == "channel.delivery.approval_created");
        timeline.Events.Should().Contain(item => item.Message.Contains("[REDACTED]", StringComparison.Ordinal));
        timeline.Events.Should().Contain(item =>
            item.Attributes != null &&
            item.Attributes.ContainsKey("apiKey") &&
            item.Attributes["apiKey"] == "[REDACTED]");

        var timelineJson = await File.ReadAllTextAsync(timelinePath);
        timelineJson.Contains("sk-json-secret", StringComparison.Ordinal).Should().BeFalse();
        timelineJson.Contains("quoted-token", StringComparison.Ordinal).Should().BeFalse();
        timelineJson.Contains("quoted-bearer", StringComparison.Ordinal).Should().BeFalse();
        timelineJson.Should().Contain("[REDACTED]");

        var sourceSummaryJson = await File.ReadAllTextAsync(sourceSummaryPath);
        sourceSummaryJson.Should().Contain("\"area\":\"gateway\"");
        sourceSummaryJson.Should().Contain("\"area\":\"plugin\"");
        sourceSummaryJson.Should().Contain("\"area\":\"automation\"");
        sourceSummaryJson.Should().Contain("\"area\":\"channel\"");

        var logSummaryJson = await File.ReadAllTextAsync(logSummaryPath);
        logSummaryJson.Should().Contain("gateway.log");
        logSummaryJson.Contains("secret-token", StringComparison.Ordinal).Should().BeFalse();

        var manifest = await JsonSerializer.DeserializeAsync<DiagnosticBundleManifest>(File.OpenRead(manifestPath), JsonOptions);
        manifest.Should().NotBeNull();
        manifest!.Entries.Should().Contain(entry => entry.Path == "snapshot/diagnostics/timeline.json");
        manifest.RedactionSummary.AppliedRules.Should().Contain(rule => rule.Contains("meta.json", StringComparison.Ordinal));
    }

    private static async Task SeedWorkspaceAsync(IServiceProvider services, string workspaceRoot)
    {
        var workspaceService = services.GetRequiredService<IWorkspaceService>();
        await workspaceService.EnsureInitializedAsync();
        await workspaceService.SaveAppConfigAsync(new WorkspaceAppConfig
        {
            WorkspaceVersion = KodaClawWorkspaceLayout.CurrentWorkspaceVersion,
            BootstrapCompleted = true,
            ActiveMainSessionId = "main-001",
        });

        var settingsRepository = services.GetRequiredService<ISettingsRepository>();
        await settingsRepository.SaveAsync(new KodaClawSettings(
            DefaultLandingRoute: "/sessions",
            Theme: ThemeMode.System,
            RequireApprovalForExternalActions: true,
            NotificationsEnabled: true,
            QuietHoursEnabled: false,
            QuietHoursStartLocalTime: null,
            QuietHoursEndLocalTime: null,
            UpdatedAt: new DateTimeOffset(2026, 3, 19, 3, 30, 0, TimeSpan.Zero)));

        await WriteJsonAsync(
            Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.UpdateStateFile),
            new UpdateStateResponse(
                GeneratedAt: new DateTimeOffset(2026, 3, 19, 3, 45, 0, TimeSpan.Zero),
                ArtifactPath: Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.UpdateStateFile),
                ManifestSource: "/tmp/update-manifest.fixture.json",
                Components:
                [
                    new UpdateComponentState(
                        Component: "gateway",
                        DisplayName: "Gateway",
                        CurrentVersion: "0.1.0",
                        ReleaseChannel: UpdateReleaseChannel.Stable,
                        LastCheckedAt: new DateTimeOffset(2026, 3, 19, 3, 45, 0, TimeSpan.Zero),
                        LatestKnownVersion: "0.1.2",
                        UpdateAvailability: UpdateAvailability.UpdateAvailable,
                        DownloadUrl: "https://example.com/download",
                        ReleaseNotesUrl: "https://example.com/release-notes",
                        ReleaseNotes: ["Manual-first update desk is available."],
                        Guidance: "Review release notes before downloading."),
                ],
                OperatorNotes: ["Manual-first update flow only."]),
            JsonOptions);
        await WriteJsonAsync(
            Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.StartupRepairReportFile),
            new StartupRepairReportResponse(
                GeneratedAt: new DateTimeOffset(2026, 3, 19, 3, 40, 0, TimeSpan.Zero),
                WorkspaceRootPath: workspaceRoot,
                ReportPath: Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.ConfigDirectory, KodaClawWorkspaceLayout.StartupRepairReportFile),
                Checklist: new RepairChecklist(
                    GeneratedAt: new DateTimeOffset(2026, 3, 19, 3, 40, 0, TimeSpan.Zero),
                    Scope: "startupRepair",
                    Summary: new RepairChecklistSummary(
                        TotalCount: 1,
                        BlockingCount: 0,
                        ActionRequiredCount: 0,
                        WarningCount: 0,
                        InfoCount: 1),
                    Items:
                    [
                        new RepairChecklistItem(
                            Id: "startup-repair-complete",
                            Severity: RepairChecklistSeverity.Info,
                            State: RepairChecklistState.Completed,
                            Category: "repair",
                            Title: "Workspace repair completed",
                            Summary: "No further action required."),
                    ])),
            JsonOptions);

        Directory.CreateDirectory(Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.LogsDirectory));
        await File.WriteAllTextAsync(
            Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.LogsDirectory, "gateway.log"),
            "2026-03-19T03:50:00Z gateway diagnostics export prepared\nsecret-token should not leave the raw log file\n");

        await WriteSessionMetaAsync(workspaceRoot, "main-001", SessionKind.Main);
        await WriteSessionMetaAsync(workspaceRoot, "auto-002", SessionKind.Automation);
    }

    private static async Task WriteSessionMetaAsync(string workspaceRoot, string sessionId, SessionKind kind)
    {
        var sessionDirectory = Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.SessionsDirectory, sessionId);
        Directory.CreateDirectory(sessionDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(sessionDirectory, "meta.json"),
            $$"""
            {
              "sessionId": "{{sessionId}}",
              "sessionKind": "{{kind}}",
              "messageCount": 3,
              "lastSfpIndex": 12,
              "createdAt": "2026-03-19T03:20:00Z"
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(sessionDirectory, "messages.json"), "[]");
    }

    private static async Task WriteJsonAsync<T>(string path, T payload, JsonSerializerOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, payload, options);
    }

    private static Task<HostedGateway> StartGatewayAsync(string workspaceRoot)
    {
        return HostedGateway.StartAsync(
            gatewayToken: GatewayToken,
            workspaceSnapshot: GatewayAuthIntegrationTests.CreateSnapshot(
                requiresBootstrap: false,
                rootPath: workspaceRoot),
            configureConfiguration: configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KODACLAW_WORKSPACE_ROOT"] = workspaceRoot,
                });
            },
            useTestWorkspaceService: false);
    }

    private sealed class TempWorkspaceRoot : IDisposable
    {
        public TempWorkspaceRoot(string prefix = "kodaclaw-diagnostic-bundle")
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                prefix,
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
