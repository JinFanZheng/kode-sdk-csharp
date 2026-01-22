using Kode.Agent.Sdk.Core.Skills;
using Kode.Agent.Sdk.Core.Types;

namespace Kode.Agent.Boilerplate;

/// <summary>
/// Application configuration options
/// </summary>
public sealed record BoilerplateOptions
{
    public required string WorkDir { get; init; }
    public required string StoreDir { get; init; }
    public required string DefaultProvider { get; init; }
    public required string DefaultModel { get; init; }
    public string? DefaultSystemPrompt { get; init; }
    public required IReadOnlyList<string> Tools { get; init; }
    public required PermissionConfig PermissionConfig { get; init; }
    public SkillsConfig? SkillsConfig { get; init; }

    public static BoilerplateOptions FromConfiguration(IConfiguration configuration, string defaultWorkDir)
    {
        var workDir = configuration["Kode:WorkDir"] ?? defaultWorkDir;
        var resolvedWorkDir = Path.GetFullPath(workDir);

        var provider = configuration["Kode:DefaultProvider"] ?? "anthropic";
        var defaultModel = configuration["Kode:DefaultModel"] ?? "claude-sonnet-4-20250514";

        var tools = ParseTools(configuration["Kode:Tools"] ?? "");
        var storeDir = configuration["Kode:StoreDir"] ?? "./.kode";
        var resolvedStoreDir = Path.IsPathRooted(storeDir)
            ? Path.GetFullPath(storeDir)
            : Path.GetFullPath(Path.Combine(resolvedWorkDir, storeDir));

        // Skills configuration
        var skillsConfig = BuildSkillsConfig(configuration, resolvedWorkDir);

        return new BoilerplateOptions
        {
            WorkDir = resolvedWorkDir,
            StoreDir = resolvedStoreDir,
            DefaultProvider = provider,
            DefaultModel = defaultModel,
            DefaultSystemPrompt = configuration["Kode:DefaultSystemPrompt"],
            Tools = tools,
            PermissionConfig = new PermissionConfig
            {
                Mode = configuration["Kode:Permissions:Mode"] ?? "auto"
            },
            SkillsConfig = skillsConfig
        };
    }

    private static IReadOnlyList<string> ParseTools(string toolsStr)
    {
        if (string.IsNullOrWhiteSpace(toolsStr))
        {
            return Array.Empty<string>();
        }

        return toolsStr
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
    }

    private static SkillsConfig? BuildSkillsConfig(IConfiguration configuration, string workDir)
    {
        var skillsSection = configuration.GetSection("Skills");
        if (!skillsSection.Exists())
        {
            return null;
        }

        var pathsStr = skillsSection["Paths"];
        var paths = string.IsNullOrWhiteSpace(pathsStr)
            ? new[] { "skills" }
            : pathsStr.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                      .Select(s => s.Trim())
                      .ToArray();

        return new SkillsConfig
        {
            Paths = paths,
            Trusted = Array.Empty<string>()
        };
    }
}
