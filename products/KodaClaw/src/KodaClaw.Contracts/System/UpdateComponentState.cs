namespace KodaClaw.Contracts.System;

public sealed record UpdateComponentState(
    string Component,
    string DisplayName,
    string CurrentVersion,
    UpdateReleaseChannel ReleaseChannel,
    DateTimeOffset? LastCheckedAt,
    string? LatestKnownVersion,
    UpdateAvailability UpdateAvailability,
    string? DownloadUrl,
    string? ReleaseNotesUrl,
    IReadOnlyList<string> ReleaseNotes,
    string Guidance);
