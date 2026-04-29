using System.Reflection;
using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using Microsoft.Extensions.Configuration;

namespace KodaClaw.Gateway;

internal sealed class UpdateStateService
{
    private static readonly JsonSerializerOptions PersistenceJsonOptions = CreatePersistenceJsonOptions();
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly IConfiguration _configuration;
    private readonly IWorkspaceService _workspaceService;

    public UpdateStateService(IConfiguration configuration, IWorkspaceService workspaceService)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
    }

    public async Task<UpdateStateResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await _workspaceService.EnsureInitializedAsync(cancellationToken);
            var artifactPath = GetArtifactPath(snapshot.RootPath);
            if (File.Exists(artifactPath))
            {
                try
                {
                    await using var stream = File.OpenRead(artifactPath);
                    var persisted = await JsonSerializer.DeserializeAsync<UpdateStateResponse>(
                        stream,
                        PersistenceJsonOptions,
                        cancellationToken);
                    if (persisted is not null)
                    {
                        return persisted;
                    }
                }
                catch (JsonException)
                {
                    // Fall back to a fresh check when the persisted artifact is unreadable.
                }
            }

            return await RefreshCoreAsync(snapshot, request: null, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<UpdateStateResponse> CheckAsync(
        UpdateCheckRequest? request,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await _workspaceService.EnsureInitializedAsync(cancellationToken);
            return await RefreshCoreAsync(snapshot, request, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<UpdateStateResponse> RefreshCoreAsync(
        WorkspaceSnapshot snapshot,
        UpdateCheckRequest? request,
        CancellationToken cancellationToken)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        var artifactPath = GetArtifactPath(snapshot.RootPath);
        var gatewayChannel = ResolveReleaseChannel(
            GetConfiguredValue(["KODACLAW_UPDATE_RELEASE_CHANNEL", "Update:ReleaseChannel"]));
        var manifest = await LoadManifestAsync(cancellationToken);
        var operatorNotes = new List<string>
        {
            "KodaClaw uses a manual-first update flow. No background download or silent install is performed.",
        };
        var components = new List<UpdateComponentState>
        {
            BuildComponentState(
                component: "gateway",
                displayName: "Gateway",
                currentVersion: ResolveGatewayVersion(),
                releaseChannel: gatewayChannel,
                checkedAt,
                manifest,
                operatorNotes),
        };

        var desktopVersion = request?.DesktopCurrentVersion?.Trim();
        if (!string.IsNullOrWhiteSpace(desktopVersion))
        {
            components.Add(BuildComponentState(
                component: "desktop",
                displayName: "Desktop Shell",
                currentVersion: desktopVersion,
                releaseChannel: request?.DesktopReleaseChannel ?? gatewayChannel,
                checkedAt,
                manifest,
                operatorNotes));
        }
        else
        {
            operatorNotes.Add("Desktop shell context was not supplied for this check, so only the Gateway version was evaluated.");
        }

        var response = new UpdateStateResponse(
            GeneratedAt: checkedAt,
            ArtifactPath: artifactPath,
            ManifestSource: manifest.Source,
            Components: components,
            OperatorNotes: operatorNotes
                .Where(note => !string.IsNullOrWhiteSpace(note))
                .Distinct(StringComparer.Ordinal)
                .ToArray());

        await PersistAsync(response, cancellationToken);
        return response;
    }

    private UpdateComponentState BuildComponentState(
        string component,
        string displayName,
        string currentVersion,
        UpdateReleaseChannel releaseChannel,
        DateTimeOffset checkedAt,
        ManifestLoadResult manifest,
        ICollection<string> operatorNotes)
    {
        const string defaultGuidance =
            "Review the published release notes, then follow the download handoff for a manual upgrade.";

        if (manifest.ErrorMessage is not null)
        {
            operatorNotes.Add(manifest.ErrorMessage);
            return new UpdateComponentState(
                Component: component,
                DisplayName: displayName,
                CurrentVersion: currentVersion,
                ReleaseChannel: releaseChannel,
                LastCheckedAt: checkedAt,
                LatestKnownVersion: null,
                UpdateAvailability: UpdateAvailability.CheckFailed,
                DownloadUrl: null,
                ReleaseNotesUrl: null,
                ReleaseNotes: Array.Empty<string>(),
                Guidance: "Configure a valid fixture manifest before relying on update status for this installation.");
        }

        if (!manifest.Channels.TryGetValue(releaseChannel, out var channelManifest))
        {
            operatorNotes.Add($"Release channel '{releaseChannel}' is not present in the configured update manifest.");
            return new UpdateComponentState(
                Component: component,
                DisplayName: displayName,
                CurrentVersion: currentVersion,
                ReleaseChannel: releaseChannel,
                LastCheckedAt: checkedAt,
                LatestKnownVersion: null,
                UpdateAvailability: UpdateAvailability.Unknown,
                DownloadUrl: null,
                ReleaseNotesUrl: null,
                ReleaseNotes: Array.Empty<string>(),
                Guidance: "The selected release channel is missing from the manifest. Publish or point to a matching channel feed first.");
        }

        var latestKnownVersion = component == "desktop"
            ? CoalesceVersion(channelManifest.DesktopVersion, channelManifest.LatestVersion)
            : CoalesceVersion(channelManifest.GatewayVersion, channelManifest.LatestVersion);
        var releaseNotes = channelManifest.ReleaseNotes
            ?.Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray()
            ?? Array.Empty<string>();
        var guidance = string.IsNullOrWhiteSpace(channelManifest.Guidance)
            ? defaultGuidance
            : channelManifest.Guidance.Trim();
        var downloadUrl = SanitizeExternalUrl(channelManifest.DownloadUrl, displayName, "downloadUrl", operatorNotes);
        var releaseNotesUrl = SanitizeExternalUrl(channelManifest.ReleaseNotesUrl, displayName, "releaseNotesUrl", operatorNotes);
        var availability = ResolveAvailability(currentVersion, latestKnownVersion, component, operatorNotes);

        if (availability == UpdateAvailability.UpdateAvailable && string.IsNullOrWhiteSpace(downloadUrl))
        {
            operatorNotes.Add($"{displayName} is behind the latest known version, but the manifest does not publish a download URL yet.");
        }

        return new UpdateComponentState(
            Component: component,
            DisplayName: displayName,
            CurrentVersion: currentVersion,
            ReleaseChannel: releaseChannel,
            LastCheckedAt: checkedAt,
            LatestKnownVersion: latestKnownVersion,
            UpdateAvailability: availability,
            DownloadUrl: downloadUrl,
            ReleaseNotesUrl: releaseNotesUrl,
            ReleaseNotes: releaseNotes,
            Guidance: guidance);
    }

    private static string? SanitizeExternalUrl(
        string? rawUrl,
        string displayName,
        string fieldName,
        ICollection<string> operatorNotes)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }

        var candidate = rawUrl.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            operatorNotes.Add(
                $"{displayName} manifest '{fieldName}' was ignored because only absolute http/https URLs are allowed.");
            return null;
        }

        return uri.AbsoluteUri;
    }

    private static UpdateAvailability ResolveAvailability(
        string currentVersion,
        string? latestKnownVersion,
        string component,
        ICollection<string> operatorNotes)
    {
        if (string.IsNullOrWhiteSpace(latestKnownVersion))
        {
            operatorNotes.Add($"The update manifest did not declare a latest known version for '{component}'.");
            return UpdateAvailability.Unknown;
        }

        if (TryCompareVersions(currentVersion, latestKnownVersion, out var comparison))
        {
            return comparison < 0
                ? UpdateAvailability.UpdateAvailable
                : UpdateAvailability.UpToDate;
        }

        operatorNotes.Add(
            $"Version comparison for '{component}' fell back to advisory mode because '{currentVersion}' and '{latestKnownVersion}' are not comparable semver values.");
        return string.Equals(currentVersion, latestKnownVersion, StringComparison.OrdinalIgnoreCase)
            ? UpdateAvailability.UpToDate
            : UpdateAvailability.Unknown;
    }

    private async Task<ManifestLoadResult> LoadManifestAsync(CancellationToken cancellationToken)
    {
        var configuredPath = GetConfiguredValue(["KODACLAW_UPDATE_MANIFEST_PATH", "Update:ManifestPath"]);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return new ManifestLoadResult(
                Source: "unconfigured",
                Channels: new Dictionary<UpdateReleaseChannel, UpdateManifestChannel>(),
                ErrorMessage: "No update manifest is configured. Set KODACLAW_UPDATE_MANIFEST_PATH before running update checks.");
        }

        var manifestPath = Path.GetFullPath(configuredPath);
        if (!File.Exists(manifestPath))
        {
            return new ManifestLoadResult(
                Source: manifestPath,
                Channels: new Dictionary<UpdateReleaseChannel, UpdateManifestChannel>(),
                ErrorMessage: $"Configured update manifest was not found at '{manifestPath}'.");
        }

        try
        {
            await using var stream = File.OpenRead(manifestPath);
            var document = await JsonSerializer.DeserializeAsync<UpdateManifestDocument>(
                stream,
                PersistenceJsonOptions,
                cancellationToken);
            if (document?.Channels is not { Count: > 0 })
            {
                return new ManifestLoadResult(
                    Source: manifestPath,
                    Channels: new Dictionary<UpdateReleaseChannel, UpdateManifestChannel>(),
                    ErrorMessage: $"Configured update manifest '{manifestPath}' does not declare any channels.");
            }

            var channels = document.Channels
                .GroupBy(item => item.Channel)
                .ToDictionary(group => group.Key, group => group.Last());
            return new ManifestLoadResult(
                Source: string.IsNullOrWhiteSpace(document.Source) ? manifestPath : document.Source.Trim(),
                Channels: channels,
                ErrorMessage: null);
        }
        catch (JsonException ex)
        {
            return new ManifestLoadResult(
                Source: manifestPath,
                Channels: new Dictionary<UpdateReleaseChannel, UpdateManifestChannel>(),
                ErrorMessage: $"Configured update manifest '{manifestPath}' is invalid JSON: {ex.GetBaseException().Message}");
        }
    }

    private async Task PersistAsync(UpdateStateResponse response, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(response.ArtifactPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(response.ArtifactPath);
        await JsonSerializer.SerializeAsync(stream, response, PersistenceJsonOptions, cancellationToken);
    }

    private string ResolveGatewayVersion()
    {
        var configuredVersion = GetConfiguredValue(["KODACLAW_GATEWAY_CURRENT_VERSION", "Update:GatewayCurrentVersion"]);
        if (!string.IsNullOrWhiteSpace(configuredVersion))
        {
            return configuredVersion.Trim();
        }

        return FormatAssemblyVersion(typeof(GatewayApp).Assembly);
    }

    private string GetConfiguredValue(IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            var value = _configuration[key]?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string GetArtifactPath(string workspaceRoot)
    {
        return Path.Combine(
            workspaceRoot,
            KodaClawWorkspaceLayout.ConfigDirectory,
            KodaClawWorkspaceLayout.UpdateStateFile);
    }

    private static string FormatAssemblyVersion(Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?.Split('+', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational;
        }

        var version = assembly.GetName().Version;
        if (version is null)
        {
            return "0.0.0";
        }

        if (version.Build < 0)
        {
            return $"{version.Major}.{version.Minor}.0";
        }

        if (version.Revision <= 0)
        {
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }

        return version.ToString();
    }

    private static UpdateReleaseChannel ResolveReleaseChannel(string? rawValue)
    {
        if (Enum.TryParse<UpdateReleaseChannel>(rawValue, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return string.IsNullOrWhiteSpace(rawValue)
            ? UpdateReleaseChannel.Stable
            : UpdateReleaseChannel.Custom;
    }

    private static string? CoalesceVersion(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return null;
    }

    private static bool TryCompareVersions(string left, string right, out int comparison)
    {
        comparison = 0;
        if (!SemanticVersion.TryParse(left, out var leftVersion) || !SemanticVersion.TryParse(right, out var rightVersion))
        {
            return false;
        }

        comparison = leftVersion!.CompareTo(rightVersion);
        return true;
    }

    private static JsonSerializerOptions CreatePersistenceJsonOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
    }

    private sealed record ManifestLoadResult(
        string Source,
        IReadOnlyDictionary<UpdateReleaseChannel, UpdateManifestChannel> Channels,
        string? ErrorMessage);

    private sealed record UpdateManifestDocument(
        DateTimeOffset? GeneratedAt,
        string? Source,
        IReadOnlyList<UpdateManifestChannel>? Channels);

    private sealed record UpdateManifestChannel(
        UpdateReleaseChannel Channel,
        string? LatestVersion,
        string? GatewayVersion,
        string? DesktopVersion,
        string? DownloadUrl,
        string? ReleaseNotesUrl,
        IReadOnlyList<string>? ReleaseNotes,
        string? Guidance);

    private sealed class SemanticVersion : IComparable<SemanticVersion>
    {
        private SemanticVersion(IReadOnlyList<int> core, IReadOnlyList<string> preRelease)
        {
            Core = core;
            PreRelease = preRelease;
        }

        public IReadOnlyList<int> Core { get; }

        public IReadOnlyList<string> PreRelease { get; }

        public static bool TryParse(string raw, out SemanticVersion version)
        {
            version = null!;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var normalized = raw.Trim();
            if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            {
                normalized = normalized[1..];
            }

            var buildSeparatorIndex = normalized.IndexOf('+');
            if (buildSeparatorIndex >= 0)
            {
                normalized = normalized[..buildSeparatorIndex];
            }

            var versionParts = normalized.Split('-', 2, StringSplitOptions.TrimEntries);
            var coreParts = versionParts[0].Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (coreParts.Length == 0)
            {
                return false;
            }

            var core = new int[coreParts.Length];
            for (var index = 0; index < coreParts.Length; index++)
            {
                if (!int.TryParse(coreParts[index], out core[index]))
                {
                    return false;
                }
            }

            var preRelease = versionParts.Length == 2
                ? versionParts[1]
                    .Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                : Array.Empty<string>();

            version = new SemanticVersion(core, preRelease);
            return true;
        }

        public int CompareTo(SemanticVersion? other)
        {
            if (other is null)
            {
                return 1;
            }

            var coreLength = Math.Max(Core.Count, other.Core.Count);
            for (var index = 0; index < coreLength; index++)
            {
                var left = index < Core.Count ? Core[index] : 0;
                var right = index < other.Core.Count ? other.Core[index] : 0;
                var coreComparison = left.CompareTo(right);
                if (coreComparison != 0)
                {
                    return coreComparison;
                }
            }

            var leftHasPreRelease = PreRelease.Count > 0;
            var rightHasPreRelease = other.PreRelease.Count > 0;
            if (!leftHasPreRelease && !rightHasPreRelease)
            {
                return 0;
            }

            if (!leftHasPreRelease)
            {
                return 1;
            }

            if (!rightHasPreRelease)
            {
                return -1;
            }

            var preReleaseLength = Math.Max(PreRelease.Count, other.PreRelease.Count);
            for (var index = 0; index < preReleaseLength; index++)
            {
                if (index >= PreRelease.Count)
                {
                    return -1;
                }

                if (index >= other.PreRelease.Count)
                {
                    return 1;
                }

                var identifierComparison = CompareIdentifier(PreRelease[index], other.PreRelease[index]);
                if (identifierComparison != 0)
                {
                    return identifierComparison;
                }
            }

            return 0;
        }

        private static int CompareIdentifier(string left, string right)
        {
            var leftIsNumeric = int.TryParse(left, out var leftNumber);
            var rightIsNumeric = int.TryParse(right, out var rightNumber);

            if (leftIsNumeric && rightIsNumeric)
            {
                return leftNumber.CompareTo(rightNumber);
            }

            if (leftIsNumeric)
            {
                return -1;
            }

            if (rightIsNumeric)
            {
                return 1;
            }

            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
