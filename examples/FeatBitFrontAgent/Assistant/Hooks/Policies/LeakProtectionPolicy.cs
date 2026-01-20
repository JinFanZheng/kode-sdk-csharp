using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Hooks;
using Kode.Agent.Sdk.Core.Types;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Kode.Agent.WebApiAssistant.Assistant.Hooks.Policies;

/// <summary>
/// Leak protection policy to prevent internal details from leaking to user or model.
/// </summary>
public record LeakProtectionPolicy(
    HashSet<string> KeepSkillInstructions,
    ILogger Logger)
{
    private readonly List<(object Block, string OriginalText)> _pendingRestores = new();
    private readonly List<object> _pendingInsertedMessages = new();
    private IReadOnlyList<Message>? _pendingMessageArray;

    /// <summary>
    /// Register an injected message for cleanup.
    /// </summary>
    public void RegisterInjectedMessage(object msg)
    {
        _pendingInsertedMessages.Add(msg);
    }

    /// <summary>
    /// Sanitize text for model-facing context (removes internal implementation details).
    /// </summary>
    private string SanitizeModelFacingText(string text)
    {
        var out_text = text;

        // Remove tool names and result previews from context-summary
        out_text = System.Text.RegularExpressions.Regex.Replace(out_text, @"🔧\s+[^\s(]+\(\.\.\.\)", "🔧 tool_call(...)");
        out_text = System.Text.RegularExpressions.Regex.Replace(out_text, @"✅ result: [^\n]*", "✅ result: [omitted]");

        // Remove internal paths
        out_text = System.Text.RegularExpressions.Regex.Replace(out_text, @"(\.config|\.tasks)/[^\s""'`<>)]*", "[redacted_path]");
        out_text = System.Text.RegularExpressions.Regex.Replace(out_text, @"/Users/[^\s""'`<>)]*", "[redacted_path]");

        // Collapse skill instructions by default, except for whitelisted skills
        out_text = System.Text.RegularExpressions.Regex.Replace(out_text, @"<skill_instructions([^>]*)>[\s\S]*?</skill_instructions>", m =>
        {
            try
            {
                var attrs = m.Groups[1].Value;
                var nameMatch = System.Text.RegularExpressions.Regex.Match(attrs, @"\bname\s*=\s*""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var skillName = nameMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(skillName) && KeepSkillInstructions.Contains(skillName))
                {
                    return m.Value;
                }
            }
            catch
            {
                // ignore
            }
            return $"<skill_instructions$1>[instructions loaded]</skill_instructions>";
        });

        return out_text;
    }

    /// <summary>
    /// Sanitize text for user-facing output (removes all internal paths and tool names).
    /// </summary>
    private string SanitizeUserFacingText(string text)
    {
        var out_text = text;

        // Remove "system shows" type phrases
        out_text = System.Text.RegularExpressions.Regex.Replace(out_text, @"我注意到系统显示当前时间是[^，。!?\n]*[，,]?\s*", "");
        out_text = System.Text.RegularExpressions.Regex.Replace(out_text, @"系统显示当前时间是[^，。!?\n]*[，,]?\s*", "");
        out_text = System.Text.RegularExpressions.Regex.Replace(out_text, @"系统显示[^，。!?\n]*[，,]?\s*", "");

        // Remove "I updated your memory" type phrases
        out_text = System.Text.RegularExpressions.Regex.Replace(out_text, @"我帮你更新好了[，,]?\s*现在记忆里", "我帮你把信息补全了，");
        out_text = System.Text.RegularExpressions.Regex.Replace(out_text, @"现在记忆里(已经)?(加了|写入了|更新了|补充了)", "我已经补充了");

        // Normalize source citation format
        out_text = NormalizeSourceCitation(out_text);

        // Fix URLs
        out_text = FixUrlTrailingPunctuation(out_text);

        // Redact internal paths
        out_text = System.Text.RegularExpressions.Regex.Replace(out_text, @"/Users/[^\s""'`<>)]*", "[redacted]");
        out_text = System.Text.RegularExpressions.Regex.Replace(out_text, @"`?\.memory[^\s`]*`?", "我的记忆");
        out_text = System.Text.RegularExpressions.Regex.Replace(out_text, @"`?\.knowledge[^\s`]*`?", "我的知识库");
        out_text = System.Text.RegularExpressions.Regex.Replace(out_text, @"`?\.config[^\s`]*`?", "我的设置");

        // Redact MCP namespaces and tool names
        out_text = System.Text.RegularExpressions.Regex.Replace(out_text, @"`?mcp__[^`\s]+`?", "（我这边查证用的渠道）", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        out_text = System.Text.RegularExpressions.Regex.Replace(out_text, @"`?\b(fs|email|todo|skill|bash)_[a-z0-9_]+\b`?", "（我这边内部处理的步骤）", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return out_text;
    }

    private string NormalizeSourceCitation(string text)
    {
        // Convert "来源：xxx；链接：https://..." to "来源：[xxx](<https://...>)"
        var pattern = @"来源[:：]\s*([^\n；;（）()]{1,80}?)(?:\s+(\d{4}[-/]\d{1,2}[-/]\d{1,2}|\d{4}年\d{1,2}月\d{1,2}日))?\s*[；;]\s*链接[:：]\s*(https?:\/\/\S+)";
        return System.Text.RegularExpressions.Regex.Replace(text, pattern, m =>
        {
            var name = m.Groups[1].Value.Trim();
            var date = m.Groups[2].Value;
            var url = m.Groups[3].Value;
            var source = !string.IsNullOrEmpty(name) ? $"[{name}](<{url}>)" : $"<{url}>";
            var dateStr = !string.IsNullOrEmpty(date) ? $"；当地时间：{date.Replace("/", "-")}" : "";
            return $"来源：{source}{dateStr}";
        });
    }

    private string FixUrlTrailingPunctuation(string text)
    {
        // Fix URLs with trailing Chinese punctuation
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(^|[^<])(https?:\/\/[^\s>）]+)）", "$1<$2>）");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(^|[^<])(https?:\/\/[^\s>（(]+)([（(])", "$1<$2>$3");

        // Normalize Markdown links
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]\n]{1,80})\]\((https?:\/\/[^\s<)\n]+)\)", m =>
        {
            var label = m.Groups[1].Value.Trim();
            var url = m.Groups[2].Value;
            return $"[{label}](<{url}>)";
        });

        return text;
    }

    /// <summary>
    /// Create the hooks delegate.
    /// </summary>
    public Func<IReadOnlyList<Message>, Task>? PreModel => async (messages) =>
    {
        _pendingRestores.Clear();
        _pendingMessageArray = messages;
        if (_pendingMessageArray == null) return;

        foreach (var msg in _pendingMessageArray)
        {
            var blocks = msg.Content as IEnumerable<object>;
            if (blocks == null) continue;

            foreach (var block in blocks)
            {
                var blockType = GetProperty<string>(block, "type");
                if (blockType == "text")
                {
                    var blockText = GetProperty<string>(block, "text");
                    if (!string.IsNullOrEmpty(blockText))
                    {
                        var msgRole = GetProperty<string>(msg, "Role");
                        // Collapse system-reminder in user messages
                        if (msgRole == "user" && blockText.StartsWith("<system-reminder>"))
                        {
                            _pendingRestores.Add((block, blockText));
                            SetProperty(block, "text", "<system-reminder>[omitted]</system-reminder>");
                            continue;
                        }

                        // Sanitize model-facing text
                        if (blockText.Contains("<context-summary") ||
                            blockText.Contains("<system-reminder") ||
                            blockText.Contains("<skill_instructions"))
                        {
                            var sanitized = SanitizeModelFacingText(blockText);
                            if (sanitized != blockText)
                            {
                                _pendingRestores.Add((block, blockText));
                                SetProperty(block, "text", sanitized);
                            }
                        }
                    }
                }
            }
        }

        await Task.CompletedTask;
    };

    /// <summary>
    /// Create the post-model hooks delegate.
    /// </summary>
    public Func<ModelResponse, Task>? PostModel => async (response) =>
    {
        // Restore preModel temporary replacements
        if (_pendingRestores.Count > 0)
        {
            foreach (var (block, originalText) in _pendingRestores)
            {
                try
                {
                    SetProperty(block, "text", originalText);
                }
                catch
                {
                    // ignore
                }
            }
            _pendingRestores.Clear();
        }

        // Remove injected messages - simplified
        _pendingInsertedMessages.Clear();
        _pendingMessageArray = null;

        await Task.CompletedTask;
    };

    private static T? GetProperty<T>(object obj, string propertyName)
    {
        var prop = obj?.GetType().GetProperty(propertyName);
        return prop != null ? (T?)prop.GetValue(obj) : default;
    }

    private static void SetProperty(object obj, string propertyName, object? value)
    {
        var prop = obj?.GetType().GetProperty(propertyName);
        prop?.SetValue(obj, value);
    }
}
