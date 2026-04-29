using System.Text.RegularExpressions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Plugins;

namespace KodaClaw.PluginHost.Permissions;

public static partial class PluginPermissionPolicy
{
    private const int DefaultListLimit = 128;

    public static PluginPermissionSet Normalize(PluginPermissionSet permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        return permissions with
        {
            Filesystem = NormalizePathList(permissions.Filesystem),
            Channels = NormalizeTokenList(permissions.Channels),
            UiPanels = NormalizeTokenList(permissions.UiPanels),
            Secrets = NormalizeTokenList(permissions.Secrets),
        };
    }

    public static PluginPermissionValidationResult Validate(PluginPermissionSet permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        var normalized = Normalize(permissions);
        var errors = new List<string>();

        ValidatePathList(normalized.Filesystem, "filesystem", errors);
        ValidateTokenList(normalized.Channels, "channels", errors);
        ValidateTokenList(normalized.UiPanels, "uiPanels", errors);
        ValidateTokenList(normalized.Secrets, "secrets", errors);

        return errors.Count == 0
            ? PluginPermissionValidationResult.Success
            : new PluginPermissionValidationResult(errors);
    }

    public static PluginPermissionRiskSummary SummarizeRisk(PluginPermissionSet permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        var normalized = Normalize(permissions);
        var high = new List<string>();
        var medium = new List<string>();

        if (normalized.Network)
        {
            high.Add("Requests network access.");
        }

        if (normalized.Background)
        {
            high.Add("Can run in background.");
        }

        if (normalized.Secrets is { Count: > 0 })
        {
            high.Add($"Requests {normalized.Secrets.Count} secret scope(s).");
        }

        if (normalized.Filesystem is { Count: > 0 })
        {
            if (normalized.Filesystem.Any(IsBroadFilesystemScope))
            {
                high.Add("Requests broad filesystem scope.");
            }
            else
            {
                medium.Add($"Requests {normalized.Filesystem.Count} filesystem scope(s).");
            }
        }

        if (normalized.Notifications)
        {
            medium.Add("Can send notifications.");
        }

        if (normalized.Channels is { Count: > 0 })
        {
            medium.Add($"Requests {normalized.Channels.Count} channel scope(s).");
        }

        if (normalized.UiPanels is { Count: > 0 })
        {
            medium.Add($"Requests {normalized.UiPanels.Count} UI panel scope(s).");
        }

        return new PluginPermissionRiskSummary(high, medium);
    }

    private static IReadOnlyList<string>? NormalizePathList(IReadOnlyList<string>? list)
    {
        var normalized = NormalizeTokenList(list, transform: NormalizePathToken);
        return normalized;
    }

    private static IReadOnlyList<string>? NormalizeTokenList(
        IReadOnlyList<string>? list,
        Func<string, string>? transform = null)
    {
        if (list is null || list.Count == 0)
        {
            return null;
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>(list.Count);

        foreach (var item in list)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            var value = transform is null
                ? item.Trim()
                : transform(item);

            if (value.Length == 0)
            {
                continue;
            }

            if (set.Add(value))
            {
                normalized.Add(value);
            }

            if (normalized.Count >= DefaultListLimit)
            {
                break;
            }
        }

        return normalized.Count == 0
            ? null
            : normalized;
    }

    private static string NormalizePathToken(string token)
    {
        var value = token.Trim().Replace('\\', '/');

        while (value.Contains("//", StringComparison.Ordinal))
        {
            value = value.Replace("//", "/", StringComparison.Ordinal);
        }

        if (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        if (value.EndsWith("/", StringComparison.Ordinal))
        {
            value = value.TrimEnd('/');
        }

        return value;
    }

    private static void ValidatePathList(
        IReadOnlyList<string>? paths,
        string fieldName,
        List<string> errors)
    {
        if (paths is null)
        {
            return;
        }

        foreach (var path in paths)
        {
            if (Path.IsPathRooted(path))
            {
                errors.Add($"permissions.{fieldName} path '{path}' must be relative.");
                continue;
            }

            if (path.StartsWith("~", StringComparison.Ordinal))
            {
                errors.Add($"permissions.{fieldName} path '{path}' must not use home-prefix.");
                continue;
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Any(static segment => segment == ".."))
            {
                errors.Add($"permissions.{fieldName} path '{path}' must not contain traversal segments.");
            }
        }
    }

    private static void ValidateTokenList(
        IReadOnlyList<string>? list,
        string fieldName,
        List<string> errors)
    {
        if (list is null)
        {
            return;
        }

        foreach (var token in list)
        {
            if (!PermissionTokenRegex().IsMatch(token))
            {
                errors.Add($"permissions.{fieldName} token '{token}' is invalid.");
            }
        }
    }

    private static bool IsBroadFilesystemScope(string path)
    {
        return string.Equals(path, "workspace", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, ".", StringComparison.Ordinal)
            || string.Equals(path, "*", StringComparison.Ordinal);
    }

    [GeneratedRegex("^[a-zA-Z0-9._:-]+$")]
    private static partial Regex PermissionTokenRegex();
}
