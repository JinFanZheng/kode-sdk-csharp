using System.Text.Json;
using System.Text.RegularExpressions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Plugins;
using KodaClaw.PluginHost.Permissions;

namespace KodaClaw.PluginHost.Manifest;

public static partial class PluginManifestValidator
{
    public static PluginManifestValidationResult Validate(
        PluginManifest manifest,
        bool requireStdioTransport = true)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            errors.Add("id is required.");
        }
        else if (!PluginIdRegex().IsMatch(manifest.Id))
        {
            errors.Add("id is invalid. Use lowercase letters, numbers, dot, dash, or underscore.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            errors.Add("name is required.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            errors.Add("version is required.");
        }

        if (manifest.Types is null || manifest.Types.Count == 0)
        {
            errors.Add("types must contain at least one plugin type.");
        }

        ValidateRuntime(manifest.Runtime, requireStdioTransport, errors);
        ValidateCapabilities(manifest.Types, manifest.Capabilities, errors);
        ValidateConfigSchema(manifest.ConfigSchema, errors);

        if (manifest.Permissions is null)
        {
            errors.Add("permissions is required.");
        }
        else
        {
            var permissionResult = PluginPermissionPolicy.Validate(manifest.Permissions);
            foreach (var permissionError in permissionResult.Errors)
            {
                errors.Add(permissionError);
            }
        }

        return errors.Count == 0
            ? PluginManifestValidationResult.Success
            : new PluginManifestValidationResult(errors);
    }

    private static void ValidateRuntime(
        PluginRuntimeSpec? runtime,
        bool requireStdioTransport,
        List<string> errors)
    {
        if (runtime is null)
        {
            errors.Add("runtime is required.");
            return;
        }

        if (requireStdioTransport && runtime.Transport != PluginTransportKind.Stdio)
        {
            errors.Add("runtime.transport must be 'Stdio' in Iteration 4.");
            return;
        }

        if (runtime.Transport == PluginTransportKind.Stdio)
        {
            if (string.IsNullOrWhiteSpace(runtime.Command))
            {
                errors.Add("runtime.command is required for stdio transport.");
            }

            if (!string.IsNullOrWhiteSpace(runtime.Url))
            {
                errors.Add("runtime.url must be empty for stdio transport.");
            }
        }
    }

    private static void ValidateCapabilities(
        IReadOnlyList<PluginType>? types,
        PluginCapabilitySet? capabilities,
        List<string> errors)
    {
        if (capabilities is null)
        {
            errors.Add("capabilities is required.");
            return;
        }

        if (types is null || types.Count == 0)
        {
            return;
        }

        if (types.Contains(PluginType.Tool))
        {
            if (capabilities.Tools is null || capabilities.Tools.Count == 0)
            {
                errors.Add("capabilities.tools must not be empty for tool plugins.");
            }
            else
            {
                foreach (var tool in capabilities.Tools)
                {
                    if (!CapabilityTokenRegex().IsMatch(tool))
                    {
                        errors.Add($"capabilities.tools token '{tool}' is invalid.");
                    }
                }
            }
        }
    }

    private static void ValidateConfigSchema(JsonElement? configSchema, List<string> errors)
    {
        if (configSchema is not { ValueKind: var kind })
        {
            return;
        }

        if (kind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        if (kind is not JsonValueKind.Object and not JsonValueKind.Array)
        {
            errors.Add("configSchema must be a JSON object or array when provided.");
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,63}$")]
    private static partial Regex PluginIdRegex();

    [GeneratedRegex("^[a-zA-Z0-9._:-]+$")]
    private static partial Regex CapabilityTokenRegex();
}
