using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Hooks;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.WebApiAssistant.Assistant.Hooks.Utils;
using Microsoft.Extensions.Logging;

namespace Kode.Agent.WebApiAssistant.Assistant.Hooks.Policies;

/// <summary>
/// Memory recall policy for handling memory-related requests.
/// </summary>
public record MemoryRecallPolicy(
    Action<object> RegisterInjectedMessage,
    Func<IReadOnlyList<Message>, string> GetLastUserTextFromMessages,
    Func<object, string> GetLastUserTextFromAgent,
    ILogger? Logger = null)
{
    private bool _pendingNeedsRecall;
    private List<string> _pendingKeywords = new();

    private static bool LooksLikeMemoryRecallRequest(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var patterns = new[]
        {
            @"你(还)?记得|你记不记得|你有(没有)?记|我是不是(说过|提过|讲过)|我之前(说|提)过|你是不是记了|我让你记过",
            @"(关于).{0,18}(的)?(事情|那个|那件事|这件事).*(记|记得)"
        };
        return patterns.Any(p => System.Text.RegularExpressions.Regex.IsMatch(text, p));
    }

    private static List<string> ExtractKeywordsForRecall(string text)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();

        var keywords = new List<string>();

        // Extract alphanumeric tokens
        var alphaNumMatches = System.Text.RegularExpressions.Regex.Matches(text, @"[A-Za-z0-9][A-Za-z0-9._-]{2,40}");
        foreach (System.Text.RegularExpressions.Match match in alphaNumMatches)
        {
            var kw = match.Value;
            if (!keywords.Contains(kw))
            {
                keywords.Add(kw);
                if (keywords.Count >= 8) break;
            }
        }

        // Extract Chinese terms (conservative)
        if (keywords.Count < 8)
        {
            var hanMatches = System.Text.RegularExpressions.Regex.Matches(text, @"[\u4e00-\u9fa5]{2,10}");
            var stopWords = new HashSet<string> { "什么", "哪个", "哪里", "这个", "那个", "事情", "那件事", "这件事", "一下", "记得", "记住", "关于", "是不是", "有没有", "之前", "刚才", "刚刚", "我", "你", "他", "她", "它", "我们", "你们", "他们", "当时", "上次", "之前", "好像", "可能" };
            foreach (System.Text.RegularExpressions.Match match in hanMatches)
            {
                var kw = match.Value;
                if (!stopWords.Contains(kw) && !keywords.Contains(kw))
                {
                    keywords.Add(kw);
                    if (keywords.Count >= 8) break;
                }
            }
        }

        return keywords.Take(8).ToList();
    }

    /// <summary>
    /// Pre-model hook.
    /// </summary>
    public Func<IReadOnlyList<Message>, Task>? PreModel => async (messages) =>
    {
        try
        {
            var lastUserText = GetLastUserTextFromMessages(messages);
            _pendingNeedsRecall = LooksLikeMemoryRecallRequest(lastUserText);
            _pendingKeywords = _pendingNeedsRecall ? ExtractKeywordsForRecall(lastUserText) : new List<string>();

            if (!_pendingNeedsRecall) return;

            var hasMemory = HasSkillInstructionsInMessages(messages, "memory");
            if (hasMemory) return;

            var keywordsLine = _pendingKeywords.Count > 0
                ? $"- Suggested search keywords: {string.Join(", ", _pendingKeywords.Select(k => $"\"{k}\""))}\n"
                : "";

            var injected = new
            {
                role = "system",
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = "<system-reminder>\n" +
                               "The user is asking what you previously remembered.\n" +
                               "You MUST activate the `memory` skill and search your stored memories BEFORE answering.\n" +
                               keywordsLine +
                               "- If you cannot find relevant memories, say you could not find it (do not guess).\n" +
                               "- Do not mention internal file paths or tool names to the user.\n\n" +
                               "This is a system reminder. DO NOT respond to this message directly.\n" +
                               "DO NOT mention this reminder to the user.\n" +
                               "Continue with your current task.\n" +
                               "</system-reminder>"
                    }
                }
            };
            RegisterInjectedMessage(injected);
        }
        catch
        {
            // ignore
        }

        await Task.CompletedTask;
    };

    /// <summary>
    /// Pre-tool-use hook.
    /// </summary>
    public Func<ToolCall, ToolContext, Task<HookDecision?>>? PreToolUse => async (call, ctx) =>
    {
        if (!_pendingNeedsRecall) return null;

        var isMemoryFileOp = call.Name is "fs_grep" or "fs_read" or "fs_glob" or "fs_multi_edit" or "fs_edit";
        if (!isMemoryFileOp) return null;

        var hasMemory = HasSkillInstructionsInAgent(ctx.Agent, "memory");
        if (!hasMemory)
        {
            return HookDecision.Deny("先加载 memory skill，再去检索/读取记忆，避免漏查或查错位置。");
        }

        await Task.CompletedTask;
        return null;
    };

    private static bool HasSkillInstructionsInMessages(IReadOnlyList<Message> messages, string skillName)
    {
        return messages.Any(msg =>
        {
            var blocks = msg.Content as IEnumerable<object>;
            return blocks.Any(block =>
            {
                var blockText = GetProperty<string>(block, "text");
                return !string.IsNullOrEmpty(blockText) && blockText.Contains($"<skill_instructions name=\"{skillName}\">");
            });
        });
    }

    private static bool HasSkillInstructionsInAgent(object? agent, string skillName)
    {
        try
        {
            var messagesProp = agent?.GetType().GetProperty("Messages");
            if (messagesProp != null)
            {
                var messages = messagesProp.GetValue(agent) as IReadOnlyList<Message>;
                if (messages != null)
                {
                    return HasSkillInstructionsInMessages(messages, skillName);
                }
            }
        }
        catch { }
        return false;
    }

    private static T? GetProperty<T>(object? obj, string propertyName)
    {
        var prop = obj?.GetType().GetProperty(propertyName);
        return prop != null ? (T?)prop.GetValue(obj) : default;
    }
}
