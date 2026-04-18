using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Kode.Agent.Sdk.Core.Skills;

/// <summary>
/// Loads skills from the filesystem.
/// </summary>
public partial class SkillsLoader
{
    private const string SkillFileName = "SKILL.md";
    private readonly ISandbox _sandbox;
    private readonly ILogger<SkillsLoader>? _logger;

    public SkillsLoader(ISandbox sandbox, ILogger<SkillsLoader>? logger = null)
    {
        _sandbox = sandbox;
        _logger = logger;
    }

    /// <summary>
    /// Discover all skills (metadata only).
    /// </summary>
    public async Task<IReadOnlyList<Skill>> DiscoverAsync(
        SkillsConfig config,
        CancellationToken cancellationToken = default)
    {
        var skills = new List<Skill>();

        foreach (var searchPath in config.Paths)
        {
            try
            {
                var skillDirs = await FindSkillDirectoriesAsync(searchPath, cancellationToken);
                
                foreach (var skillDir in skillDirs)
                {
                    try
                    {
                        var skill = await LoadMetadataAsync(skillDir, cancellationToken);
                        
                        if (skill == null) continue;
                        
                        // Apply include/exclude filters
                        if (config.Include != null && !config.Include.Contains(skill.Name))
                            continue;
                        
                        if (config.Exclude != null && config.Exclude.Contains(skill.Name))
                            continue;

                        skills.Add(skill);
                        _logger?.LogDebug("Discovered skill: {Name} at {Path}", skill.Name, skillDir);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to load skill from {Path}", skillDir);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to search skills in {Path}", searchPath);
            }
        }

        return skills;
    }

    /// <summary>
    /// Load full skill content.
    /// </summary>
    public async Task<Skill> LoadFullAsync(
        string skillPath,
        CancellationToken cancellationToken = default)
    {
        var skillFile = Path.Combine(skillPath, SkillFileName);
        var content = await _sandbox.ReadFileAsync(skillFile, cancellationToken);
        
        var (metadata, body) = ParseSkillFile(content);
        
        // Load resources
        var resources = await LoadResourcesAsync(skillPath, cancellationToken);
        
        return new Skill
        {
            Name = metadata.Name,
            Description = metadata.Description,
            License = metadata.License,
            Compatibility = metadata.Compatibility,
            AllowedTools = metadata.AllowedTools,
            Metadata = metadata.Metadata,
            Path = skillPath,
            Body = body,
            Resources = resources,
            LoadedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    private async Task<IReadOnlyList<string>> FindSkillDirectoriesAsync(
        string searchPath,
        CancellationToken cancellationToken)
    {
        var result = new List<string>();
        
        try
        {
            // Check if the search path itself contains SKILL.md
            var directSkillFile = Path.Combine(searchPath, SkillFileName);
            if (await _sandbox.FileExistsAsync(directSkillFile, cancellationToken))
            {
                result.Add(searchPath);
                return result;
            }
            
            // Search subdirectories
            var entries = await _sandbox.ListDirectoryAsync(searchPath, cancellationToken);
            foreach (var entry in entries.Where(e => e.IsDirectory))
            {
                var skillFile = Path.Combine(entry.Path, SkillFileName);
                if (await _sandbox.FileExistsAsync(skillFile, cancellationToken))
                {
                    result.Add(entry.Path);
                }
            }
        }
        catch
        {
            // Directory might not exist
        }

        return result;
    }

    private async Task<Skill?> LoadMetadataAsync(
        string skillPath,
        CancellationToken cancellationToken)
    {
        var skillFile = Path.Combine(skillPath, SkillFileName);
        var content = await _sandbox.ReadFileAsync(skillFile, cancellationToken);
        
        var (metadata, _) = ParseSkillFile(content);
        
        return new Skill
        {
            Name = metadata.Name,
            Description = metadata.Description,
            License = metadata.License,
            Compatibility = metadata.Compatibility,
            AllowedTools = metadata.AllowedTools,
            Metadata = metadata.Metadata,
            Path = skillPath,
            LoadedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    /// <summary>
    /// Parses SKILL.md frontmatter from raw content string.
    /// Exposed for host-layer parsers to reuse without requiring ISandbox.
    /// </summary>
    public static SkillMetadata ParseFrontmatter(string content)
    {
        var (metadata, _) = ParseSkillFile(content);
        return metadata;
    }

    private static (SkillMetadata Metadata, string Body) ParseSkillFile(string content)
    {
        var name = "";
        var description = "";
        string? license = null;
        string? compatibility = null;
        List<string>? allowedTools = null;
        Dictionary<string, JsonElement>? metadataDict = null;
        string body;

        // Parse YAML frontmatter
        var frontmatterMatch = FrontmatterRegex().Match(content);
        if (frontmatterMatch.Success)
        {
            var frontmatter = frontmatterMatch.Groups[1].Value;
            body = content[(frontmatterMatch.Index + frontmatterMatch.Length)..].Trim();

            var inMetadataBlock = false;

            // Simple YAML parsing
            foreach (var line in frontmatter.Split('\n'))
            {
                // Metadata sub-lines (indented)
                if (inMetadataBlock)
                {
                    if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                    {
                        var subColonIndex = line.IndexOf(':');
                        if (subColonIndex > 0)
                        {
                            var subKey = line[..subColonIndex].Trim();
                            var subValue = line[(subColonIndex + 1)..].Trim().Trim('"', '\'');
                            if (!string.IsNullOrEmpty(subKey))
                            {
                                metadataDict ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                                metadataDict[subKey] = JsonSerializer.SerializeToElement(subValue);
                            }
                        }
                        continue;
                    }
                    else
                    {
                        inMetadataBlock = false;
                    }
                }

                var colonIndex = line.IndexOf(':');
                if (colonIndex <= 0) continue;

                var key = line[..colonIndex].Trim().ToLowerInvariant();
                var value = line[(colonIndex + 1)..].Trim().Trim('"', '\'');

                switch (key)
                {
                    case "name":
                        name = value;
                        break;
                    case "description":
                        description = value;
                        break;
                    case "license":
                        license = value;
                        break;
                    case "compatibility":
                        compatibility = value;
                        break;
                    case "allowed-tools":
                    case "allowedtools":
                    case "allowed_tools":
                        // Support both comma-separated (Claude Code native, e.g. "Bash(npx foo:*), Read")
                        // and space-separated (legacy, e.g. "Bash(kc:*) fs_read") formats.
                        var separator = value.Contains(',') ? ',' : ' ';
                        allowedTools = value
                            .Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(NormalizeToolSpec)
                            .ToList();
                        break;
                    case "metadata":
                        if (string.IsNullOrEmpty(value))
                            inMetadataBlock = true;
                        break;
                }
            }
        }
        else
        {
            body = content;

            // Try to extract name from first heading
            var headingMatch = HeadingRegex().Match(content);
            if (headingMatch.Success)
            {
                name = headingMatch.Groups[1].Value.Trim();
            }
        }

        // Validate required fields
        if (string.IsNullOrEmpty(name))
        {
            throw new InvalidOperationException("Skill name is required");
        }

        if (string.IsNullOrEmpty(description))
        {
            // Use first paragraph as description
            var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            description = lines.FirstOrDefault(l => !l.StartsWith('#'))?.Trim() ?? name;
        }

        return (new SkillMetadata
        {
            Name = name,
            Description = description,
            License = license,
            Compatibility = compatibility,
            AllowedTools = allowedTools,
            Metadata = metadataDict
        }, body);
    }

    private async Task<SkillResources?> LoadResourcesAsync(
        string skillPath,
        CancellationToken cancellationToken)
    {
        var scriptsDir = Path.Combine(skillPath, "scripts");
        var referencesDir = Path.Combine(skillPath, "references");
        var assetsDir = Path.Combine(skillPath, "assets");

        List<string>? scripts = null;
        List<string>? references = null;
        List<string>? assets = null;

        try
        {
            if (await _sandbox.DirectoryExistsAsync(scriptsDir, cancellationToken))
            {
                var entries = await _sandbox.ListDirectoryAsync(scriptsDir, cancellationToken);
                scripts = entries.Where(e => !e.IsDirectory).Select(e => e.Path).ToList();
            }
        }
        catch { }

        try
        {
            if (await _sandbox.DirectoryExistsAsync(referencesDir, cancellationToken))
            {
                var entries = await _sandbox.ListDirectoryAsync(referencesDir, cancellationToken);
                references = entries.Where(e => !e.IsDirectory).Select(e => e.Path).ToList();
            }
        }
        catch { }

        try
        {
            if (await _sandbox.DirectoryExistsAsync(assetsDir, cancellationToken))
            {
                var entries = await _sandbox.ListDirectoryAsync(assetsDir, cancellationToken);
                assets = entries.Where(e => !e.IsDirectory).Select(e => e.Path).ToList();
            }
        }
        catch { }

        if (scripts == null && references == null && assets == null)
        {
            return null;
        }

        return new SkillResources
        {
            Scripts = scripts,
            References = references,
            Assets = assets
        };
    }

    /// <summary>
    /// Loads a resource file from a skill directory.
    /// </summary>
    /// <param name="skillPath">The path to the skill directory.</param>
    /// <param name="resourcePath">The relative path to the resource file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The content of the resource file, or null if not found.</returns>
    public async Task<string?> LoadResourceAsync(
        string skillPath,
        string resourcePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(skillPath) || string.IsNullOrEmpty(resourcePath))
        {
            return null;
        }

        // Normalize and validate the resource path to prevent path traversal
        var normalizedPath = resourcePath.Replace('\\', '/').TrimStart('/');
        if (normalizedPath.Contains(".."))
        {
            _logger?.LogWarning("Path traversal attempt detected: {ResourcePath}", resourcePath);
            return null;
        }

        // Try to load from different resource directories
        var resourceDirs = new[] { "references", "assets", "scripts", "" };
        
        foreach (var dir in resourceDirs)
        {
            var fullPath = string.IsNullOrEmpty(dir)
                ? Path.Combine(skillPath, normalizedPath)
                : Path.Combine(skillPath, dir, normalizedPath);

            try
            {
                if (await _sandbox.FileExistsAsync(fullPath, cancellationToken))
                {
                    var content = await _sandbox.ReadFileAsync(fullPath, cancellationToken);
                    _logger?.LogDebug("Loaded resource {ResourcePath} from {FullPath}", resourcePath, fullPath);
                    return content;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to read resource at {Path}", fullPath);
            }
        }

        _logger?.LogWarning("Resource not found: {ResourcePath} in skill {SkillPath}", resourcePath, skillPath);
        return null;
    }

    /// <summary>
    /// Normalizes a tool spec token from SKILL.md allowed-tools.
    /// Maps agentskills.io standard names (e.g. "Bash") to internal names (e.g. "bash_run").
    /// Converts constraint syntax "Bash(kc:*)" to internal format "bash_run[kc]".
    /// </summary>
    internal static string NormalizeToolSpec(string token)
    {
        // Check for constraint syntax: ToolName(prefix:*)
        var parenOpen = token.IndexOf('(');
        if (parenOpen > 0 && token.EndsWith(":*)"))
        {
            var toolName = token[..parenOpen];
            var prefix = token[(parenOpen + 1)..^3]; // strip '(' prefix ':*)'
            var internalName = MapToolAlias(toolName);
            return $"{internalName}[{prefix}]";
        }

        return MapToolAlias(token);
    }

    /// <summary>
    /// Maps agentskills.io standard tool names to Kode.Agent.Tools.Builtin tool names.
    /// </summary>
    private static string MapToolAlias(string toolName) => toolName switch
    {
        "Bash" => "bash_run",
        "Read" => "fs_read",
        "Write" => "fs_write",
        "Edit" => "fs_edit",
        _ => toolName
    };

    [GeneratedRegex(@"^---\s*\n([\s\S]*?)\n---", RegexOptions.Multiline)]
    private static partial Regex FrontmatterRegex();

    [GeneratedRegex(@"^#\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();
}
