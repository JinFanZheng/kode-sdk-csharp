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

        var warnings = new List<string>();
        var (metadata, body) = ParseSkillFile(content, bodyFallback: true, warnings);
        LogSpecWarnings(skillPath, warnings);

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

        var warnings = new List<string>();
        var (metadata, _) = ParseSkillFile(content, bodyFallback: true, warnings);
        LogSpecWarnings(skillPath, warnings);
        
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
    /// Does not fall back to body-derived description when the frontmatter field is absent.
    /// </summary>
    public static SkillMetadata ParseFrontmatter(string content)
    {
        var (metadata, _) = ParseSkillFile(content, bodyFallback: false, warnings: null);
        return metadata;
    }

    /// <summary>
    /// Parses SKILL.md frontmatter and reports deviations from the agentskills.io spec
    /// (e.g. placing <c>allowed-tools</c> / <c>compatibility</c> inside <c>metadata:</c>,
    /// or using a YAML flow sequence as a metadata value).
    /// </summary>
    public static SkillMetadata ParseFrontmatter(string content, out IReadOnlyList<string> warnings)
    {
        var collected = new List<string>();
        var (metadata, _) = ParseSkillFile(content, bodyFallback: false, collected);
        warnings = collected;
        return metadata;
    }

    private void LogSpecWarnings(string skillPath, List<string> warnings)
    {
        if (warnings.Count == 0 || _logger is null) return;
        foreach (var w in warnings)
        {
            _logger.LogWarning("Skill {Path} deviates from agentskills.io spec: {Warning}", skillPath, w);
        }
    }

    private static (SkillMetadata Metadata, string Body) ParseSkillFile(
        string content,
        bool bodyFallback = true,
        List<string>? warnings = null)
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
            var lines = frontmatter.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                // Metadata sub-lines (indented)
                if (inMetadataBlock)
                {
                    if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                    {
                        var subColonIndex = line.IndexOf(':');
                        if (subColonIndex > 0)
                        {
                            var subKey = line[..subColonIndex].Trim();
                            var subValueRaw = line[(subColonIndex + 1)..].Trim();
                            var subValue = subValueRaw.Trim('"', '\'');
                            if (!string.IsNullOrEmpty(subKey))
                            {
                                // Spec deviation checks (agentskills.io): allowed-tools and
                                // compatibility are top-level fields, and metadata values must
                                // be strings (not flow sequences).
                                if (warnings is not null)
                                {
                                    if (subKey.Equals("allowed-tools", StringComparison.OrdinalIgnoreCase) ||
                                        subKey.Equals("allowedtools", StringComparison.OrdinalIgnoreCase) ||
                                        subKey.Equals("allowed_tools", StringComparison.OrdinalIgnoreCase))
                                    {
                                        warnings.Add("'allowed-tools' is a top-level field per agentskills.io spec; move it out of 'metadata:'.");
                                    }
                                    else if (subKey.Equals("compatibility", StringComparison.OrdinalIgnoreCase))
                                    {
                                        warnings.Add("'compatibility' is a top-level field per agentskills.io spec; move it out of 'metadata:'.");
                                    }

                                    if (subValueRaw.Length >= 2 && subValueRaw[0] == '[' && subValueRaw[^1] == ']')
                                    {
                                        warnings.Add($"metadata.{subKey} uses a YAML flow sequence; metadata values must be strings per agentskills.io spec (use a quoted comma-separated string instead).");
                                    }
                                }

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
                var rawAfterColon = line[(colonIndex + 1)..];

                string value;
                if (TryReadBlockScalarIndicator(rawAfterColon, out var blockStyle, out var blockChomping))
                {
                    value = ConsumeBlockScalar(lines, ref i, blockStyle, blockChomping);
                }
                else
                {
                    value = rawAfterColon.Trim().Trim('"', '\'');
                }

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

        if (string.IsNullOrEmpty(description) && bodyFallback)
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

    /// <summary>
    /// Detects a YAML block scalar indicator (<c>|</c>, <c>&gt;</c>) with optional
    /// chomping modifier (<c>-</c> strip, <c>+</c> keep) immediately after <c>key:</c>.
    /// Trailing comments (<c># ...</c>) are ignored.
    /// </summary>
    private static bool TryReadBlockScalarIndicator(string rawAfterColon, out char style, out char chomping)
    {
        style = '\0';
        chomping = '\0';

        var token = rawAfterColon.Trim();
        // Strip trailing inline comment
        var hashIndex = token.IndexOf('#');
        if (hashIndex >= 0)
        {
            token = token[..hashIndex].TrimEnd();
        }

        if (token.Length == 0 || token.Length > 2) return false;
        var first = token[0];
        if (first != '|' && first != '>') return false;
        style = first;

        if (token.Length == 2)
        {
            var second = token[1];
            if (second != '-' && second != '+') return false;
            chomping = second;
        }

        return true;
    }

    /// <summary>
    /// Consumes a YAML block scalar starting after the current line. Advances <paramref name="index"/>
    /// to the last line consumed; the outer loop increments past it. Lines less-indented than the
    /// first non-blank block line terminate the scalar without being consumed.
    /// </summary>
    private static string ConsumeBlockScalar(string[] lines, ref int index, char style, char chomping)
    {
        int? blockIndent = null;
        var collected = new List<string>();
        int pendingBlank = 0;
        int lastConsumed = index;

        for (int j = index + 1; j < lines.Length; j++)
        {
            var ln = lines[j];

            if (string.IsNullOrWhiteSpace(ln))
            {
                // Don't commit to consuming blank lines until we see a real content line
                // at or beyond the block indent — otherwise a blank line before the next
                // top-level key would be swallowed.
                pendingBlank++;
                continue;
            }

            int lineIndent = 0;
            while (lineIndent < ln.Length && ln[lineIndent] == ' ')
                lineIndent++;

            if (blockIndent == null)
            {
                // First content line establishes block indent. Require at least 1 space;
                // a fully un-indented first line terminates the empty block immediately.
                if (lineIndent == 0) break;
                blockIndent = lineIndent;
            }

            if (lineIndent < blockIndent) break;

            for (int k = 0; k < pendingBlank; k++) collected.Add("");
            pendingBlank = 0;

            collected.Add(ln[blockIndent.Value..]);
            lastConsumed = j;
        }

        index = lastConsumed;

        if (collected.Count == 0) return "";

        string result;
        if (style == '|')
        {
            // Literal: keep all line breaks
            result = string.Join('\n', collected);
        }
        else
        {
            // Folded: non-blank lines join with a space; blank lines become a newline
            var sb = new System.Text.StringBuilder();
            foreach (var ln in collected)
            {
                if (ln.Length == 0)
                {
                    sb.Append('\n');
                }
                else
                {
                    if (sb.Length > 0 && sb[^1] != '\n')
                        sb.Append(' ');
                    sb.Append(ln);
                }
            }
            result = sb.ToString();
        }

        // Chomping: default ("clip") and "+" ("keep") both are fine as-is for our trimmed
        // collection; only "-" ("strip") needs to drop trailing newlines.
        if (chomping == '-')
        {
            result = result.TrimEnd('\n');
        }

        return result;
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
