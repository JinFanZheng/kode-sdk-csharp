namespace KodaClaw.Workspace.Memory;

/// <summary>
/// Parses YAML-like frontmatter from memory markdown files.
/// Handles the <c>---</c> delimited header with fields like priority, status, created, tags.
/// No external YAML library dependency — simple line-by-line parsing.
/// </summary>
public static class MemoryFrontmatterParser
{
    public static MemoryFrontmatter Parse(string content)
    {
        var result = new MemoryFrontmatter();
        if (string.IsNullOrWhiteSpace(content))
            return result;

        var lines = content.Split('\n');
        if (lines.Length < 2 || lines[0].Trim() != "---")
            return result;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line == "---")
            {
                result.BodyStartLine = i + 1;
                break;
            }

            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0) continue;

            var key = line[..colonIdx].Trim().ToLowerInvariant();
            var value = line[(colonIdx + 1)..].Trim();

            switch (key)
            {
                case "priority":
                    result.Priority = NormalizePriority(value);
                    break;
                case "status":
                    result.Status = value;
                    break;
                case "created":
                    result.Created = value;
                    break;
                case "tags":
                    result.Tags = ParseInlineList(value);
                    break;
                case "title":
                    result.Title = value;
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Normalizes priority values: accepts both legacy numeric (0-3) and semantic labels.
    /// </summary>
    private static string NormalizePriority(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "0" or "p0" => "permanent",
            "1" or "p1" => "lasting",
            "2" or "p2" => "standard",
            "3" or "p3" => "ephemeral",
            "permanent" or "lasting" or "standard" or "ephemeral" => value.Trim().ToLowerInvariant(),
            _ => "standard",
        };
    }

    private static string[] ParseInlineList(string value)
    {
        // Support [a, b, c] inline format
        var trimmed = value.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToArray();
    }
}

public sealed class MemoryFrontmatter
{
    public string Priority { get; set; } = "standard";
    public string Status { get; set; } = "active";
    public string? Created { get; set; }
    public string? Title { get; set; }
    public string[]? Tags { get; set; }
    public int BodyStartLine { get; set; }
}
