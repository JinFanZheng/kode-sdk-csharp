using Kode.Agent.Sdk.Core.Skills;
using System.Text.Json;

namespace KodaClaw.Gateway;

/// <summary>
/// Parses SKILL.md frontmatter fields, delegating standard field parsing to the SDK.
/// Only extracts KodaClaw-specific UI fields (kind, version, tags) from the metadata block.
/// </summary>
internal static class SkillFrontmatterParser
{
    internal sealed record ParsedFrontmatter(
        string? Description,
        string Kind,
        string? Version,
        IReadOnlyList<string> Tags,
        IReadOnlyList<string> AllowedTools,
        string? Compatibility);

    internal static ParsedFrontmatter Parse(string content)
    {
        // Guard: if no frontmatter block exists, return all defaults.
        // The SDK falls back to body-derived description when no frontmatter is found,
        // but the Gateway UI should show null rather than body text.
        if (!HasFrontmatter(content))
        {
            return new ParsedFrontmatter(null, "optional", null, [], [], null);
        }

        SkillMetadata meta;
        try
        {
            meta = SkillsLoader.ParseFrontmatter(content);
        }
        catch
        {
            return new ParsedFrontmatter(null, "optional", null, [], [], null);
        }

        // Only surface description if it was explicitly set in frontmatter (not body-derived).
        // SDK sets description to body text when the frontmatter description field is empty.
        var description = ExplicitDescription(content);

        var kind = GetMetaString(meta.Metadata, "kind") is { Length: > 0 } k ? k : "optional";
        var version = GetMetaString(meta.Metadata, "version");
        var tagsRaw = GetMetaString(meta.Metadata, "tags");
        var tags = tagsRaw is not null
            ? tagsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : (IReadOnlyList<string>)[];

        return new ParsedFrontmatter(
            Description:   description,
            Kind:          kind,
            Version:       version,
            Tags:          tags,
            AllowedTools:  meta.AllowedTools ?? [],
            Compatibility: meta.Compatibility);
    }

    private static bool HasFrontmatter(string content)
    {
        var trimmed = content.TrimStart();
        return trimmed.StartsWith("---", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the description when explicitly set in frontmatter (never body-fallback).
    /// Checks for a top-level <c>description:</c> key in the frontmatter block, then
    /// delegates to <see cref="SkillsLoader.ParseFrontmatter"/> — which supports YAML
    /// block scalars (<c>|</c> and <c>&gt;</c>) for multi-line values.
    /// </summary>
    private static string? ExplicitDescription(string content)
    {
        var start = content.IndexOf("---", StringComparison.Ordinal);
        if (start < 0) return null;
        var end = content.IndexOf("---", start + 3, StringComparison.Ordinal);
        if (end < 0) return null;

        var frontmatter = content[(start + 3)..end];
        var hasDescription = false;
        foreach (var line in frontmatter.Split('\n'))
        {
            // Only top-level keys — indented lines belong to a nested block (e.g. metadata:)
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t')) continue;
            if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                hasDescription = true;
                break;
            }
        }

        if (!hasDescription) return null;

        SkillMetadata meta;
        try { meta = SkillsLoader.ParseFrontmatter(content); }
        catch { return null; }

        return string.IsNullOrWhiteSpace(meta.Description) ? null : meta.Description;
    }

    private static string? GetMetaString(IReadOnlyDictionary<string, JsonElement>? meta, string key)
    {
        if (meta == null || !meta.TryGetValue(key, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText().Trim('"');
    }
}
