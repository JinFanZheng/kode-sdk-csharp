using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cronos;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Workspace;

namespace KodaClaw.Workspace.Heartbeat;

public sealed class HeartbeatAutomationCompiler : IHeartbeatAutomationCompiler
{
    internal const string HeartbeatSourcePath = $"{KodaClawWorkspaceLayout.WorkspaceDirectory}/{KodaClawWorkspaceLayout.HeartbeatFile}";

    // Legacy schedule: patterns — converted to cron for backward compatibility.
    private static readonly Regex LegacyMinutesPattern = new(
        "^every\\s+(\\d+)m$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex LegacyHourlyPattern = new(
        "^hourly\\s+(\\d+)h$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex LegacyDailyPattern = new(
        "^daily\\s+(\\d{2}:\\d{2})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex LegacyWeekdaysPattern = new(
        "^weekdays\\s+(\\d{2}:\\d{2})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex LegacyWeeklyPattern = new(
        "^weekly\\s+(.+?)\\s+(\\d{2}:\\d{2})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public IReadOnlyList<AutomationDefinition> Compile(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            throw new HeartbeatCompilationException("HEARTBEAT markdown is required.");
        }

        var lines = NormalizeNewLines(markdown).Split('\n');
        var sections = ParseSections(lines);
        if (sections.Count == 0)
        {
            throw new HeartbeatCompilationException("At least one automation section is required. Use '## <Title>'.");
        }

        return BuildDefinitions(sections);
    }

    private static string NormalizeNewLines(string markdown)
    {
        return markdown.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private static List<SectionDraft> ParseSections(string[] lines)
    {
        var sections = new List<SectionDraft>();
        var lineIndex = 0;
        SectionDraft? currentSection = null;

        while (lineIndex < lines.Length)
        {
            var line = lines[lineIndex];
            var trimmed = line.Trim();
            var lineNumber = lineIndex + 1;

            if (trimmed.Length == 0)
            {
                lineIndex++;
                continue;
            }

            if (trimmed.StartsWith("##", StringComparison.Ordinal))
            {
                var title = trimmed[2..].Trim();
                if (title.Length == 0)
                {
                    throw new HeartbeatCompilationException("Automation title is required after '##'.", lineNumber);
                }

                currentSection = new SectionDraft(title);
                sections.Add(currentSection);
                lineIndex++;
                continue;
            }

            if (currentSection is null)
            {
                if (trimmed.StartsWith('#'))
                {
                    lineIndex++;
                    continue;
                }

                throw new HeartbeatCompilationException(
                    "Automation content must be inside a section that starts with '## <Title>'.",
                    lineNumber);
            }

            if (!TryReadTopLevelBullet(line, out var bulletContent))
            {
                throw new HeartbeatCompilationException("Expected a top-level bullet field.", lineNumber);
            }

            if (TryReadFieldValue(bulletContent, "cron", out var cronExpr))
            {
                currentSection.CronExpression = EnsureSingleAssignment(
                    currentSection.CronExpression,
                    "cron",
                    cronExpr,
                    lineNumber);
                lineIndex++;
                continue;
            }

            if (TryReadFieldValue(bulletContent, "schedule", out var scheduleExpr))
            {
                // Legacy field — convert to cron on the fly.
                var cron = LegacyScheduleToCron(scheduleExpr, lineNumber);
                currentSection.CronExpression = EnsureSingleAssignment(
                    currentSection.CronExpression,
                    "schedule",
                    cron,
                    lineNumber);
                lineIndex++;
                continue;
            }

            if (TryReadFieldValue(bulletContent, "prompt", out var prompt))
            {
                if (string.Equals(prompt, ">", StringComparison.Ordinal))
                {
                    // YAML-style block scalar: collect indented continuation lines until the
                    // next top-level bullet or section header.
                    // Empty lines produce paragraph breaks (\n\n); non-empty lines are joined
                    // with \n so that Markdown rendering preserves structure.
                    lineIndex++;
                    var sb = new StringBuilder();
                    while (lineIndex < lines.Length)
                    {
                        var contLine = lines[lineIndex];
                        var contTrimmed = contLine.Trim();
                        if (contTrimmed.StartsWith("##", StringComparison.Ordinal) || IsTopLevelBullet(contLine))
                        {
                            break;
                        }

                        if (contTrimmed.Length > 0)
                        {
                            if (sb.Length > 0)
                            {
                                sb.Append('\n');
                            }

                            sb.Append(contTrimmed);
                        }
                        else if (sb.Length > 0)
                        {
                            // Blank line → paragraph break for Markdown
                            sb.Append('\n');
                        }

                        lineIndex++;
                    }

                    prompt = sb.ToString();
                    if (prompt.Length == 0)
                    {
                        throw new HeartbeatCompilationException("Field 'prompt' block scalar cannot be empty.", lineNumber);
                    }
                }
                else
                {
                    lineIndex++;
                }

                currentSection.Prompt = EnsureSingleAssignment(
                    currentSection.Prompt,
                    "prompt",
                    prompt,
                    lineNumber);
                continue;
            }

            if (TryReadFieldValue(bulletContent, "model", out var modelId))
            {
                currentSection.ModelId = EnsureSingleAssignment(
                    currentSection.ModelId,
                    "model",
                    modelId,
                    lineNumber);
                lineIndex++;
                continue;
            }

            if (TryReadFieldValue(bulletContent, "enabled", out var enabledText))
            {
                currentSection.Enabled = ParseEnabled(enabledText, lineNumber);
                lineIndex++;
                continue;
            }

            if (IsEmptyListField(bulletContent, "inputs"))
            {
                lineIndex = ParseInputs(lines, lineIndex + 1, currentSection);
                continue;
            }

            if (IsEmptyListField(bulletContent, "channels"))
            {
                lineIndex = ParseChannels(lines, lineIndex + 1, currentSection);
                continue;
            }

            if (TryReadFieldValue(bulletContent, "delivery-mode", out var deliveryMode))
            {
                currentSection.NotifyMode = ParseNotifyMode(deliveryMode);
                lineIndex++;
                continue;
            }

            throw new HeartbeatCompilationException($"Unsupported field '{bulletContent}'.", lineNumber);
        }

        return sections;
    }

    /// <summary>
    /// Converts a legacy <c>schedule:</c> expression to a standard 5-field cron string.
    /// Supported forms: "every 15m", "hourly 2h", "daily 09:00", "weekdays 09:00", "weekly mon,wed,fri 18:30".
    /// </summary>
    internal static string LegacyScheduleToCron(string expression, int lineNumber = 0)
    {
        var expr = expression.Trim();

        var minutesMatch = LegacyMinutesPattern.Match(expr);
        if (minutesMatch.Success)
        {
            var interval = int.Parse(minutesMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            if (interval < 5)
            {
                throw new HeartbeatCompilationException(
                    $"Legacy schedule '{expr}': minimum interval is 5 minutes.", lineNumber);
            }

            return $"*/{interval} * * * *";
        }

        var hourlyMatch = LegacyHourlyPattern.Match(expr);
        if (hourlyMatch.Success)
        {
            var interval = int.Parse(hourlyMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            if (interval < 1)
            {
                throw new HeartbeatCompilationException(
                    $"Legacy schedule '{expr}': hourly interval must be >= 1.", lineNumber);
            }

            return $"0 */{interval} * * *";
        }

        var dailyMatch = LegacyDailyPattern.Match(expr);
        if (dailyMatch.Success)
        {
            var (hour, minute) = ParseHHMM(dailyMatch.Groups[1].Value, expr, lineNumber);
            return $"{minute} {hour} * * *";
        }

        var weekdaysMatch = LegacyWeekdaysPattern.Match(expr);
        if (weekdaysMatch.Success)
        {
            var (hour, minute) = ParseHHMM(weekdaysMatch.Groups[1].Value, expr, lineNumber);
            return $"{minute} {hour} * * 1-5";
        }

        var weeklyMatch = LegacyWeeklyPattern.Match(expr);
        if (weeklyMatch.Success)
        {
            var dayTokens = weeklyMatch.Groups[1].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (dayTokens.Length == 0)
            {
                throw new HeartbeatCompilationException(
                    $"Legacy schedule '{expr}': weekly schedule requires at least one day.", lineNumber);
            }

            var dayNumbers = new List<int>(dayTokens.Length);
            foreach (var token in dayTokens)
            {
                dayNumbers.Add(ParseDayToken(token, expr, lineNumber));
            }

            dayNumbers.Sort();
            var (hour, minute) = ParseHHMM(weeklyMatch.Groups[2].Value, expr, lineNumber);
            return $"{minute} {hour} * * {string.Join(",", dayNumbers)}";
        }

        throw new HeartbeatCompilationException(
            $"Unsupported legacy schedule '{expr}'. " +
            "Use '- cron: \"0 9 * * *\"' instead. " +
            "Legacy forms: 'every 15m', 'hourly 2h', 'daily 09:00', 'weekdays 09:00', 'weekly mon,wed,fri 18:30'.",
            lineNumber);
    }

    private static (int Hour, int Minute) ParseHHMM(string token, string expression, int lineNumber)
    {
        if (TimeOnly.TryParseExact(token, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        {
            return (t.Hour, t.Minute);
        }

        throw new HeartbeatCompilationException(
            $"Invalid time token '{token}' in schedule '{expression}'.", lineNumber);
    }

    private static int ParseDayToken(string token, string expression, int lineNumber)
    {
        return token.ToLowerInvariant() switch
        {
            "sun" => 0,
            "mon" => 1,
            "tue" => 2,
            "wed" => 3,
            "thu" => 4,
            "fri" => 5,
            "sat" => 6,
            _ => throw new HeartbeatCompilationException(
                $"Invalid weekday token '{token}' in schedule '{expression}'.", lineNumber),
        };
    }

    private static int ParseChannels(string[] lines, int startIndex, SectionDraft section)
    {
        var lineIndex = startIndex;
        while (lineIndex < lines.Length)
        {
            var line = lines[lineIndex];
            var trimmed = line.Trim();
            var lineNumber = lineIndex + 1;

            if (trimmed.Length == 0)
            {
                lineIndex++;
                continue;
            }

            if (trimmed.StartsWith("##", StringComparison.Ordinal) || IsTopLevelBullet(line))
            {
                break;
            }

            if (!TryReadNestedBullet(line, out var bindingId))
            {
                throw new HeartbeatCompilationException(
                    "Channels must be declared as nested bullets under '- channels:'.",
                    lineNumber);
            }

            section.NotificationChannels.Add(bindingId);
            lineIndex++;
        }

        return lineIndex;
    }

    private static int ParseInputs(string[] lines, int startIndex, SectionDraft section)
    {
        var lineIndex = startIndex;
        while (lineIndex < lines.Length)
        {
            var line = lines[lineIndex];
            var trimmed = line.Trim();
            var lineNumber = lineIndex + 1;

            if (trimmed.Length == 0)
            {
                lineIndex++;
                continue;
            }

            if (trimmed.StartsWith("##", StringComparison.Ordinal) || IsTopLevelBullet(line))
            {
                break;
            }

            if (!TryReadNestedBullet(line, out var inputPath))
            {
                throw new HeartbeatCompilationException(
                    "Inputs must be declared as nested bullets under '- inputs:'.",
                    lineNumber);
            }

            section.Inputs.Add(NormalizeInputPath(inputPath, lineNumber));
            lineIndex++;
        }

        return lineIndex;
    }

    private static bool TryReadFieldValue(string bulletContent, string fieldName, out string value)
    {
        var prefix = $"{fieldName}:";
        if (!bulletContent.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = string.Empty;
            return false;
        }

        value = bulletContent[prefix.Length..].Trim().Trim('"', '\'');
        if (value.Length == 0)
        {
            throw new HeartbeatCompilationException($"Field '{fieldName}' cannot be empty.");
        }

        return true;
    }

    private static string EnsureSingleAssignment(string? currentValue, string fieldName, string newValue, int lineNumber)
    {
        if (currentValue is not null)
        {
            throw new HeartbeatCompilationException($"Field '{fieldName}' can only be declared once.", lineNumber);
        }

        return newValue;
    }

    private static AutomationNotifyMode ParseNotifyMode(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "auto" => AutomationNotifyMode.Auto,
            "approval" => AutomationNotifyMode.Approval,
            _ => AutomationNotifyMode.None,
        };
    }

    private static bool ParseEnabled(string enabledText, int lineNumber)
    {
        if (bool.TryParse(enabledText, out var enabled))
        {
            return enabled;
        }

        throw new HeartbeatCompilationException("Field 'enabled' must be 'true' or 'false'.", lineNumber);
    }

    private static IReadOnlyList<AutomationDefinition> BuildDefinitions(IReadOnlyList<SectionDraft> sections)
    {
        var definitions = new List<AutomationDefinition>(sections.Count);
        var slugCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var section in sections)
        {
            var title = section.Title;
            if (title.Length == 0)
            {
                throw new HeartbeatCompilationException("Automation title is required.");
            }

            if (string.IsNullOrWhiteSpace(section.CronExpression))
            {
                throw new HeartbeatCompilationException(
                    $"Automation '{title}' is missing required schedule. " +
                    "Use '- cron: \"0 9 * * *\"' or legacy '- schedule: daily 09:00'.");
            }

            if (string.IsNullOrWhiteSpace(section.Prompt))
            {
                throw new HeartbeatCompilationException($"Automation '{title}' is missing required 'prompt' field.");
            }

            // Validate that the cron expression is parseable by Cronos.
            var cronExpr = section.CronExpression.Trim();
            try
            {
                CronExpression.Parse(cronExpr);
            }
            catch (CronFormatException ex)
            {
                throw new HeartbeatCompilationException(
                    $"Automation '{title}' has invalid cron expression '{cronExpr}': {ex.Message}");
            }

            var slug = SlugifyTitle(title);
            var count = slugCounts.TryGetValue(slug, out var previousCount) ? previousCount + 1 : 1;
            slugCounts[slug] = count;
            var id = count == 1 ? slug : $"{slug}-{count}";

            definitions.Add(new AutomationDefinition(
                Id: id,
                Title: title,
                Prompt: section.Prompt,
                Source: AutomationDefinitionSource.Heartbeat,
                SourcePath: HeartbeatSourcePath,
                CronExpression: cronExpr,
                Enabled: section.Enabled,
                InputPaths: section.Inputs.ToArray(),
                ModelId: section.ModelId,
                NotificationChannels: section.NotificationChannels.Count > 0 ? section.NotificationChannels.ToArray() : null,
                NotifyMode: section.NotifyMode,
                CreatedAt: DateTimeOffset.UnixEpoch,
                UpdatedAt: DateTimeOffset.UnixEpoch,
                LastRunAt: null,
                NextRunAt: null,
                LastRunStatus: null,
                LastError: null));
        }

        return definitions;
    }

    private static string SlugifyTitle(string title)
    {
        var builder = new StringBuilder(title.Length);
        var lastWasHyphen = false;
        foreach (var character in title.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                lastWasHyphen = false;
                continue;
            }

            if (!lastWasHyphen)
            {
                builder.Append('-');
                lastWasHyphen = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length == 0)
        {
            throw new HeartbeatCompilationException($"Automation title '{title}' cannot be converted into an id slug.");
        }

        return slug;
    }

    private static string NormalizeInputPath(string inputPath, int lineNumber)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new HeartbeatCompilationException("Input path cannot be empty.", lineNumber);
        }

        var normalized = inputPath.Trim().Replace('\\', '/');
        if (normalized.StartsWith("~/", StringComparison.Ordinal)
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.StartsWith("\\", StringComparison.Ordinal)
            || normalized.Contains(':', StringComparison.Ordinal))
        {
            throw new HeartbeatCompilationException("Input path must be workspace-relative.", lineNumber);
        }

        if (normalized.StartsWith("workspace/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["workspace/".Length..];
        }

        var segments = new List<string>();
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(segment, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                throw new HeartbeatCompilationException(
                    "Input path cannot traverse outside workspace.",
                    lineNumber);
            }

            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new HeartbeatCompilationException("Input path contains invalid characters.", lineNumber);
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            throw new HeartbeatCompilationException("Input path cannot be empty.", lineNumber);
        }

        return string.Join('/', segments);
    }

    /// <summary>
    /// Returns true when a bullet content represents a list field header that is either
    /// empty ("inputs:") or declared as an explicit empty YAML list ("inputs: []").
    /// Nested items, if any, are parsed by the caller on subsequent lines.
    /// </summary>
    private static bool IsEmptyListField(string bulletContent, string fieldName)
    {
        var prefix = $"{fieldName}:";
        if (!bulletContent.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = bulletContent[prefix.Length..].Trim();
        // Accept "field:" (rest is empty) or "field: []" (explicit empty YAML list).
        return rest.Length == 0 || rest == "[]";
    }

    private static bool TryReadTopLevelBullet(string line, out string content)
    {
        if (!IsTopLevelBullet(line))
        {
            content = string.Empty;
            return false;
        }

        content = line.Trim()[2..].Trim();
        if (content.Length == 0)
        {
            throw new HeartbeatCompilationException("Bullet field cannot be empty.");
        }

        return true;
    }

    private static bool IsTopLevelBullet(string line)
    {
        return LeadingWhitespaceLength(line) == 0
            && line.TrimStart().StartsWith("- ", StringComparison.Ordinal);
    }

    private static bool TryReadNestedBullet(string line, out string content)
    {
        var trimmed = line.Trim();
        if (LeadingWhitespaceLength(line) == 0 || !trimmed.StartsWith("- ", StringComparison.Ordinal))
        {
            content = string.Empty;
            return false;
        }

        content = trimmed[2..].Trim();
        if (content.Length == 0)
        {
            throw new HeartbeatCompilationException("Input bullet cannot be empty.");
        }

        return true;
    }

    private static int LeadingWhitespaceLength(string value)
    {
        var index = 0;
        while (index < value.Length && char.IsWhiteSpace(value[index]))
        {
            index++;
        }

        return index;
    }

    private sealed class SectionDraft(string title)
    {
        public string Title { get; } = title;

        public string? CronExpression { get; set; }

        public string? Prompt { get; set; }

        public string? ModelId { get; set; }

        public bool Enabled { get; set; } = true;

        public List<string> Inputs { get; } = [];

        public List<string> NotificationChannels { get; } = [];

        public AutomationNotifyMode NotifyMode { get; set; } = AutomationNotifyMode.None;
    }
}
