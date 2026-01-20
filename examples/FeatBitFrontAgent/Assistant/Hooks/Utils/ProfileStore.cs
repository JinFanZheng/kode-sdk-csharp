using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Kode.Agent.WebApiAssistant.Assistant.Hooks.Utils;

/// <summary>
/// Profile store for managing user profile in memory.
/// </summary>
public class ProfileStore
{
    private readonly string? _dataDir;
    private readonly ILogger _logger;
    private const string ProfileFileName = ".memory/profile.json";

    public ProfileStore(string? dataDir, ILogger logger)
    {
        _dataDir = dataDir;
        _logger = logger;
    }

    /// <summary>
    /// Get the profile path, checking both dataDir and default locations.
    /// </summary>
    private string? GetProfilePath()
    {
        var possiblePaths = new List<string?>();

        if (!string.IsNullOrEmpty(_dataDir))
        {
            possiblePaths.Add(Path.Combine(_dataDir, ".memory", "profile.json"));
        }

        // Check current working directory
        possiblePaths.Add(Path.Combine(Directory.GetCurrentDirectory(), ".memory", "profile.json"));

        foreach (var path in possiblePaths)
        {
            if (path != null && File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// Load the user profile.
    /// </summary>
    public async Task<JsonDocument?> LoadProfileAsync()
    {
        var profilePath = GetProfilePath();
        if (profilePath == null || !File.Exists(profilePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(profilePath);
            return JsonDocument.Parse(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load profile from {Path}", profilePath);
            return null;
        }
    }

    /// <summary>
    /// Save the user profile.
    /// </summary>
    public async Task SaveProfileAsync(JsonDocument profile)
    {
        if (string.IsNullOrEmpty(_dataDir))
        {
            _logger.LogWarning("Cannot save profile: dataDir is not set");
            return;
        }

        var memoryDir = Path.Combine(_dataDir, ".memory");
        Directory.CreateDirectory(memoryDir);

        var profilePath = Path.Combine(memoryDir, "profile.json");
        var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(profilePath, json);
    }

    /// <summary>
    /// Get the default location from profile.
    /// </summary>
    public async Task<string?> GetDefaultLocationAsync()
    {
        var profile = await LoadProfileAsync();
        if (profile == null) return null;

        if (profile.RootElement.TryGetProperty("defaultLocation", out var location))
        {
            return location.GetString();
        }

        return null;
    }

    /// <summary>
    /// Set the default location in profile.
    /// </summary>
    public async Task SetDefaultLocationAsync(string location)
    {
        var profile = await LoadProfileAsync();
        var profileDict = new Dictionary<string, object>();

        if (profile != null)
        {
            foreach (var prop in profile.RootElement.EnumerateObject())
            {
                profileDict[prop.Name] = prop.Value;
            }
        }

        profileDict["defaultLocation"] = location;
        profileDict["updatedAt"] = DateTimeOffset.UtcNow.ToString("O");

        // Create new JsonDocument
        var json = JsonSerializer.Serialize(profileDict, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        var newProfile = JsonDocument.Parse(json);

        await SaveProfileAsync(newProfile);
    }
}
