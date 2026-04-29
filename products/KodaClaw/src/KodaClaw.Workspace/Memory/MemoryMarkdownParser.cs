using System.Text.RegularExpressions;

namespace KodaClaw.Workspace.Memory;

/// <summary>
/// Utility methods for parsing MEMORY.md section structure and normalizing keys.
/// </summary>
public static class MemoryMarkdownParser
{
    private static readonly Regex SectionHeadingRegex = new(
        @"^##\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Parses MEMORY.md into a list of (section title, section body) pairs.
    /// Each section starts with a ## heading.
    /// </summary>
    public static IReadOnlyList<(string Title, string Body)> ParseSections(string content)
    {
        var sections = new List<(string Title, string Body)>();
        var matches = SectionHeadingRegex.Matches(content);

        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var title = match.Groups[1].Value.Trim();
            var bodyStart = match.Index + match.Length;
            var bodyEnd = i + 1 < matches.Count
                ? matches[i + 1].Index
                : content.Length;

            var body = content[bodyStart..bodyEnd].Trim();
            if (!string.IsNullOrWhiteSpace(title))
            {
                sections.Add((title, body));
            }
        }

        return sections;
    }

    /// <summary>
    /// Normalizes a section title to a key suitable for file naming or lookup.
    /// Lowercases, replaces spaces with hyphens, removes special chars, preserves CJK.
    /// </summary>
    public static string NormalizeKey(string title)
    {
        var key = title.ToLowerInvariant().Trim();
        // Replace whitespace sequences with hyphens
        key = Regex.Replace(key, @"\s+", "-");
        // Remove non-alphanumeric except hyphens and CJK characters
        key = Regex.Replace(key, @"[^\w\u4e00-\u9fff\u3400-\u4dbf-]", "");
        // Collapse multiple hyphens
        key = Regex.Replace(key, @"-{2,}", "-");
        return key.Trim('-');
    }
}
