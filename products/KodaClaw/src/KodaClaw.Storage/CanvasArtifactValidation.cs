using KodaClaw.Contracts;
using KodaClaw.Contracts.Canvas;
using KodaClaw.Contracts.Workspace;

namespace KodaClaw.Storage;

internal static class CanvasArtifactValidation
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public static void ValidateArtifact(CanvasArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ValidateRequired(artifact.Id, nameof(artifact.Id));
        ValidateRequired(artifact.Title, nameof(artifact.Title));
        ValidateRequired(artifact.Summary, nameof(artifact.Summary));
        ValidateRequired(artifact.Source, nameof(artifact.Source));
        if (artifact.Kind == CanvasArtifactKind.Image)
        {
            ValidateMediaPath(artifact.EntryPath, nameof(artifact.EntryPath));
        }
        else
        {
            ValidateWorkspaceCanvasPath(artifact.EntryPath, nameof(artifact.EntryPath));
            ValidateWorkspaceCanvasPath(artifact.AssetDirectory, nameof(artifact.AssetDirectory));
        }

        if (artifact.CreatedAt == default)
        {
            throw new ArgumentException("CreatedAt is required.", nameof(artifact.CreatedAt));
        }

        if (artifact.UpdatedAt == default)
        {
            throw new ArgumentException("UpdatedAt is required.", nameof(artifact.UpdatedAt));
        }
    }

    public static void ValidateId(string? id, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Id is required.", parameterName);
        }
    }

    public static int NormalizeLimit(int limit)
    {
        if (limit <= 0)
        {
            return DefaultLimit;
        }

        return Math.Min(limit, MaxLimit);
    }

    public static string NormalizeOptionalText(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private static void ValidateRequired(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        }
    }

    private static void ValidateMediaPath(string? rawPath, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        }

        var normalized = rawPath.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{parameterName} must be a workspace-relative path.", parameterName);
        }

        if (!normalized.StartsWith("media/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{parameterName} for Image artifacts must be under media/.", parameterName);
        }
    }

    private static void ValidateWorkspaceCanvasPath(string? rawPath, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        }

        var normalized = rawPath.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{parameterName} must be a workspace-relative path.", parameterName);
        }

        var segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Length < 2)
        {
            throw new ArgumentException($"{parameterName} must be under workspace/canvas.", parameterName);
        }

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                throw new ArgumentException($"{parameterName} contains an invalid path segment.", parameterName);
            }
        }

        var isWorkspacePrefix =
            string.Equals(segments[0], KodaClawWorkspaceLayout.WorkspaceDirectory, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segments[1], "canvas", StringComparison.OrdinalIgnoreCase);
        if (!isWorkspacePrefix)
        {
            throw new ArgumentException(
                $"{parameterName} must be under workspace/canvas or workspace/canvas/artifacts.",
                parameterName);
        }
    }
}
