using System.Text.Json;
using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.System;
using Xunit;

namespace KodaClaw.ContractTests.Workspace;

public sealed class UpdateStateContractsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Update_state_should_json_round_trip()
    {
        var payload = new UpdateStateResponse(
            GeneratedAt: new DateTimeOffset(2026, 3, 19, 2, 30, 0, TimeSpan.Zero),
            ArtifactPath: "/tmp/.kodaclaw/config/update-state.json",
            ManifestSource: "/tmp/fixtures/update-manifest.json",
            Components:
            [
                new UpdateComponentState(
                    Component: "gateway",
                    DisplayName: "Gateway",
                    CurrentVersion: "0.1.0",
                    ReleaseChannel: UpdateReleaseChannel.Stable,
                    LastCheckedAt: new DateTimeOffset(2026, 3, 19, 2, 30, 0, TimeSpan.Zero),
                    LatestKnownVersion: "0.1.2",
                    UpdateAvailability: UpdateAvailability.UpdateAvailable,
                    DownloadUrl: "https://example.com/download",
                    ReleaseNotesUrl: "https://example.com/release-notes",
                    ReleaseNotes:
                    [
                        "Adds the manual-first update desk.",
                        "Improves operator release visibility.",
                    ],
                    Guidance: "Review the release notes, then complete the guided handoff manually."),
                new UpdateComponentState(
                    Component: "desktop",
                    DisplayName: "Desktop Shell",
                    CurrentVersion: "0.1.0",
                    ReleaseChannel: UpdateReleaseChannel.Stable,
                    LastCheckedAt: new DateTimeOffset(2026, 3, 19, 2, 30, 0, TimeSpan.Zero),
                    LatestKnownVersion: "0.1.3",
                    UpdateAvailability: UpdateAvailability.UpdateAvailable,
                    DownloadUrl: "https://example.com/desktop-download",
                    ReleaseNotesUrl: "https://example.com/desktop-release-notes",
                    ReleaseNotes:
                    [
                        "Desktop bridge now reports app version and release channel.",
                    ],
                    Guidance: "Download the packaged shell build and relaunch after the installer finishes."),
            ],
            OperatorNotes:
            [
                "KodaClaw uses a manual-first update flow. No background download or silent install is performed.",
            ]);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<UpdateStateResponse>(json, JsonOptions);

        json.Should().Contain("\"artifactPath\"");
        json.Should().Contain("\"updateAvailability\":\"UpdateAvailable\"");
        json.Should().Contain("\"releaseChannel\":\"Stable\"");
        roundTrip.Should().NotBeNull();
        roundTrip!.Components.Should().HaveCount(2);
        roundTrip.Components[0].Component.Should().Be("gateway");
        roundTrip.Components[1].DisplayName.Should().Be("Desktop Shell");
        roundTrip.Components[0].UpdateAvailability.Should().Be(UpdateAvailability.UpdateAvailable);
        roundTrip.OperatorNotes.Should().ContainSingle();
    }
}
