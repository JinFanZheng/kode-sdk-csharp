using System.Text.Json;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Context;
using Kode.Agent.Sdk.Core.Skills;
using Kode.Agent.Sdk.Tools;

namespace Kode.Agent.Tools.Builtin.Skills;

/// <summary>
/// Tool for listing available skills, optionally filtered and ranked by a query.
/// </summary>
[Tool("skill_list")]
public sealed class SkillListTool : ToolBase<SkillListArgs>
{
    public override string Name => "skill_list";

    public override string Description =>
        "List available Skills. Supports an optional free-text `query` (BM25 ranked over name + description + tags), " +
        "optional `tags` filter, and a result `limit`. Returns each Skill's name, description, activation state, " +
        "available resource flags, and custom tags.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<SkillListArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = true,
        RequiresApproval = false
    };

    private static readonly string[] EnableHint = { "Ensure skills are enabled in the agent configuration" };

    public override ValueTask<string?> GetPromptAsync(ToolContext context)
    {
        return ValueTask.FromResult<string?>(
            "Enumerate available Skills. Each result includes the Skill's name, description, activation flag, " +
            "resource flags (scripts/references/assets), and any tags from its frontmatter metadata.\n\n" +
            "Parameters:\n" +
            "- `query` (optional): free text — results are ranked by BM25 over name + description + tags.\n" +
            "- `tags` (optional): only return Skills whose metadata tags include ALL of the given tags (case-insensitive).\n" +
            "- `limit` (optional, default 20, max 100): cap the number of results.\n\n" +
            "When to call:\n" +
            "- The system prompt already lists available Skills. Call skill_list when you need to narrow that list " +
            "by topic, when names alone are shown (large skill libraries), or to re-check the current activation state.\n" +
            "- Pair with skill_activate once you've picked the right one.");
    }

    protected override Task<ToolResult> ExecuteAsync(
        SkillListArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (context.Agent is not ISkillsAwareAgent skillsAware)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Ok(new
            {
                ok = false,
                error = "Skills not configured for this agent",
                recommendations = EnableHint
            }));
        }

        var manager = skillsAware.SkillsManager;
        if (manager == null)
        {
            return System.Threading.Tasks.Task.FromResult(ToolResult.Ok(new
            {
                ok = false,
                error = "Skills manager not available",
                recommendations = EnableHint
            }));
        }

        var all = manager.List();

        // Tag filter (AND semantics, case-insensitive).
        var filtered = all.AsEnumerable();
        var tagFilter = NormalizeTags(args.Tags);
        if (tagFilter.Count > 0)
        {
            filtered = filtered.Where(s =>
            {
                var skillTags = ExtractTags(s);
                return tagFilter.All(t => skillTags.Contains(t));
            });
        }

        var hasQuery = !string.IsNullOrWhiteSpace(args.Query);
        // Default: unlimited when the caller supplies no filter/query (backward-compatible
        // with the original tool that returned every discovered skill). Query implicitly
        // caps at 20 to keep ranked output bounded.
        int? limit = args.Limit > 0
            ? Math.Min(args.Limit, 100)
            : (hasQuery ? 20 : null);

        IReadOnlyList<(Skill skill, double? score)> ordered;
        if (hasQuery)
        {
            var candidates = filtered.ToList();
            var index = new BM25Index();
            foreach (var skill in candidates)
            {
                var tags = ExtractTags(skill);
                var doc = string.Join(' ', new[] { skill.Name, skill.Description, string.Join(' ', tags) });
                index.Add(skill.Name, doc, snippet: skill.Description, timestamp: 0);
            }

            var hits = index.Search(args.Query!, limit ?? int.MaxValue);
            var byName = candidates.ToDictionary(s => s.Name, StringComparer.Ordinal);
            ordered = hits
                .Where(h => byName.ContainsKey(h.DocumentId))
                .Select(h => (byName[h.DocumentId], (double?)Math.Round(h.Score, 3)))
                .ToList();
        }
        else
        {
            // Preserve discovery order (matches the original SkillsManager.List() semantics).
            var sequence = filtered;
            if (limit.HasValue) sequence = sequence.Take(limit.Value);
            ordered = sequence
                .Select(s => (s, (double?)null))
                .ToList();
        }

        var skillInfos = ordered.Select(entry => new
        {
            name = entry.skill.Name,
            description = entry.skill.Description,
            activated = manager.IsActivated(entry.skill.Name),
            hasScripts = (entry.skill.Resources?.Scripts?.Count ?? 0) > 0,
            hasReferences = (entry.skill.Resources?.References?.Count ?? 0) > 0,
            hasAssets = (entry.skill.Resources?.Assets?.Count ?? 0) > 0,
            tags = ExtractTags(entry.skill),
            score = entry.score
        }).ToList();

        return System.Threading.Tasks.Task.FromResult(ToolResult.Ok(new
        {
            ok = true,
            skills = skillInfos,
            count = skillInfos.Count,
            total = all.Count,
            activatedCount = all.Count(s => manager.IsActivated(s.Name)),
            query = hasQuery ? args.Query : null,
            appliedTags = tagFilter.Count > 0 ? tagFilter : null
        }));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? tags)
    {
        if (tags == null || tags.Count == 0) return Array.Empty<string>();
        return tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Reads the spec's arbitrary <c>metadata</c> frontmatter map and extracts a normalized
    /// tag list from the <c>tags</c> key (supports a JSON array or a comma-separated string).
    /// Returns an empty list when no tags are declared.
    /// </summary>
    private static IReadOnlyList<string> ExtractTags(SkillMetadata skill)
    {
        if (skill.Metadata == null) return Array.Empty<string>();
        if (!skill.Metadata.TryGetValue("tags", out var element)) return Array.Empty<string>();

        var collected = new List<string>();
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var v = item.GetString();
                        if (!string.IsNullOrWhiteSpace(v)) collected.Add(v.Trim());
                    }
                }
                break;
            case JsonValueKind.String:
                var raw = element.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        collected.Add(part);
                }
                break;
        }

        return collected
            .Select(t => t.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>
/// Arguments for skill_list tool.
/// </summary>
[GenerateToolSchema]
public class SkillListArgs
{
    /// <summary>
    /// Free-text query; results are ranked by BM25 over name + description + tags.
    /// </summary>
    [ToolParameter(Description = "Optional free-text query to rank Skills by relevance (BM25 over name + description + tags)", Required = false)]
    public string? Query { get; init; }

    /// <summary>
    /// Tag filter; only Skills whose metadata tags include ALL provided tags are returned.
    /// </summary>
    [ToolParameter(Description = "Optional tag filter (AND semantics, case-insensitive). Reads the Skill frontmatter metadata.tags field.", Required = false)]
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// Maximum number of results (default 20, max 100).
    /// </summary>
    [ToolParameter(Description = "Maximum number of Skills to return (default 20, max 100)", Required = false)]
    public int Limit { get; init; }
}

// Note: ISkillsAwareAgent is defined in Kode.Agent.Sdk.Core.Abstractions.
