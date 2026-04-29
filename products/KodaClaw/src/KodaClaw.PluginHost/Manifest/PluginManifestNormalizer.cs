using KodaClaw.Contracts;
using KodaClaw.Contracts.Plugins;
using KodaClaw.PluginHost.Permissions;

namespace KodaClaw.PluginHost.Manifest;

public static class PluginManifestNormalizer
{
    public static PluginManifest Normalize(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return manifest with
        {
            Id = manifest.Id.Trim().ToLowerInvariant(),
            Name = manifest.Name.Trim(),
            Version = manifest.Version.Trim(),
            Types = NormalizeTypes(manifest.Types),
            Runtime = NormalizeRuntime(manifest.Runtime),
            Permissions = PluginPermissionPolicy.Normalize(manifest.Permissions),
            Capabilities = NormalizeCapabilities(manifest.Capabilities),
            Display = NormalizeDisplay(manifest.Display),
            Healthcheck = NormalizeHealthcheck(manifest.Healthcheck),
        };
    }

    private static IReadOnlyList<PluginType> NormalizeTypes(IReadOnlyList<PluginType> types)
    {
        if (types.Count == 0)
        {
            return [];
        }

        var set = new HashSet<PluginType>();
        var normalized = new List<PluginType>(types.Count);
        foreach (var type in types)
        {
            if (set.Add(type))
            {
                normalized.Add(type);
            }
        }

        return normalized;
    }

    private static PluginRuntimeSpec NormalizeRuntime(PluginRuntimeSpec runtime)
    {
        return runtime with
        {
            Command = NormalizeOptionalText(runtime.Command),
            Args = NormalizeStringList(runtime.Args),
            Environment = NormalizeDictionary(runtime.Environment),
            Url = NormalizeOptionalText(runtime.Url),
            Headers = NormalizeDictionary(runtime.Headers),
            EnvironmentReferences = NormalizeDictionary(runtime.EnvironmentReferences),
            HeaderReferences = NormalizeDictionary(runtime.HeaderReferences),
        };
    }

    private static PluginCapabilitySet NormalizeCapabilities(PluginCapabilitySet capabilities)
    {
        return capabilities with
        {
            Tools = NormalizeStringList(capabilities.Tools),
            Channels = NormalizeStringList(capabilities.Channels),
            UiPanels = NormalizeStringList(capabilities.UiPanels),
            MemoryProviders = NormalizeStringList(capabilities.MemoryProviders),
        };
    }

    private static PluginDisplaySpec? NormalizeDisplay(PluginDisplaySpec? display)
    {
        if (display is null)
        {
            return null;
        }

        return display with
        {
            Description = NormalizeOptionalText(display.Description),
            Icon = NormalizeOptionalText(display.Icon),
            AccentColor = NormalizeOptionalText(display.AccentColor),
        };
    }

    private static PluginHealthcheckSpec? NormalizeHealthcheck(PluginHealthcheckSpec? healthcheck)
    {
        if (healthcheck is null)
        {
            return null;
        }

        return healthcheck with
        {
            ToolName = NormalizeOptionalText(healthcheck.ToolName),
        };
    }

    private static IReadOnlyList<string>? NormalizeStringList(IReadOnlyList<string>? list)
    {
        if (list is null || list.Count == 0)
        {
            return null;
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>(list.Count);
        foreach (var value in list)
        {
            var item = NormalizeOptionalText(value);
            if (item is null)
            {
                continue;
            }

            if (set.Add(item))
            {
                normalized.Add(item);
            }
        }

        return normalized.Count == 0
            ? null
            : normalized;
    }

    private static IReadOnlyDictionary<string, string>? NormalizeDictionary(IReadOnlyDictionary<string, string>? map)
    {
        if (map is null || map.Count == 0)
        {
            return null;
        }

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawKey, rawValue) in map)
        {
            var key = NormalizeOptionalText(rawKey);
            var value = NormalizeOptionalText(rawValue);
            if (key is null || value is null)
            {
                continue;
            }

            normalized[key] = value;
        }

        return normalized.Count == 0
            ? null
            : normalized;
    }

    private static string? NormalizeOptionalText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }
}
