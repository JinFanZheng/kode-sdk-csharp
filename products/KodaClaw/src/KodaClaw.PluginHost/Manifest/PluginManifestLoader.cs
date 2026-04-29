using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Plugins;

namespace KodaClaw.PluginHost.Manifest;

public sealed class PluginManifestLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PluginManifest> LoadFromFileAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new ArgumentException("Manifest path is required.", nameof(manifestPath));
        }

        var fullPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Manifest file was not found.", fullPath);
        }

        await using var stream = File.OpenRead(fullPath);
        var manifest = await JsonSerializer.DeserializeAsync<PluginManifest>(
            stream,
            JsonOptions,
            cancellationToken);

        return ValidateAndNormalize(
            manifest,
            source: fullPath);
    }

    public PluginManifest ParseAndValidate(
        string json,
        string source = "<memory>")
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Manifest json is required.", nameof(json));
        }

        var manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);
        return ValidateAndNormalize(manifest, source);
    }

    private static PluginManifest ValidateAndNormalize(PluginManifest? manifest, string source)
    {
        if (manifest is null)
        {
            throw new PluginManifestValidationException(
                $"Manifest '{source}' is empty or invalid json.",
                ["Manifest payload could not be deserialized."]);
        }

        var normalized = PluginManifestNormalizer.Normalize(manifest);
        var result = PluginManifestValidator.Validate(normalized);
        if (!result.IsValid)
        {
            throw new PluginManifestValidationException(
                $"Manifest '{source}' is invalid.",
                result.Errors);
        }

        return normalized;
    }
}
