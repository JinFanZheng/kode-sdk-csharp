namespace Kode.Agent.WebApiAssistant.Assistant;

/// <summary>
/// Utility for discovering skills from the file system.
/// </summary>
public static class SkillsDiscovery
{
    /// <summary>
    /// Skill names that should always be auto-activated (not just recommended).
    /// </summary>
    private static readonly HashSet<string> AutoActivateSkills = new(StringComparer.OrdinalIgnoreCase)
    {
        "memory", "knowledge", "email", "calendar"
    };

    /// <summary>
    /// Discover available skills from the specified paths.
    /// A skill directory is valid if it contains a SKILL.md file.
    /// </summary>
    /// <param name="skillsPaths">Paths to search for skills.</param>
    /// <param name="workDir">Base working directory for resolving relative paths.</param>
    /// <returns>Tuple of (autoActivate, recommend) skill lists.</returns>
    public static (IReadOnlyList<string> AutoActivate, IReadOnlyList<string> Recommend) DiscoverSkills(
        IReadOnlyList<string>? skillsPaths,
        string? workDir = null)
    {
        if (skillsPaths == null || skillsPaths.Count == 0)
        {
            return (
                AutoActivate: AutoActivateSkills.ToList(),
                Recommend: new List<string>()
            );
        }

        var baseDir = workDir ?? Directory.GetCurrentDirectory();
        var allSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var autoActivate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recommend = new List<string>();

        foreach (var skillsPath in skillsPaths)
        {
            var fullPath = Path.IsPathRooted(skillsPath)
                ? skillsPath
                : Path.Combine(baseDir, skillsPath);

            if (!Directory.Exists(fullPath)) continue;

            foreach (var skillDir in Directory.GetDirectories(fullPath))
            {
                var skillName = Path.GetFileName(skillDir);

                // Check if this is a valid skill directory (contains SKILL.md)
                var skillFile = Path.Combine(skillDir, "SKILL.md");
                if (!File.Exists(skillFile)) continue;

                allSkills.Add(skillName);

                // Check if should be auto-activated
                if (AutoActivateSkills.Contains(skillName))
                {
                    autoActivate.Add(skillName);
                }
                else
                {
                    recommend.Add(skillName);
                }
            }
        }

        // Ensure all auto-activate skills are in the list even if directory doesn't exist
        foreach (var skill in AutoActivateSkills)
        {
            if (!allSkills.Contains(skill))
            {
                recommend.Add(skill);
            }
        }

        return (
            AutoActivate: autoActivate.ToList(),
            Recommend: recommend
        );
    }
}
