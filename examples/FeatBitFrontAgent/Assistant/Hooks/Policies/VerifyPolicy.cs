using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Hooks;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.WebApiAssistant.Assistant.Hooks.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Kode.Agent.WebApiAssistant.Assistant.Hooks.Policies;

/// <summary>
/// Verify policy for handling location-dependent and verification requests.
/// </summary>
public record VerifyPolicy(
    ProfileStore ProfileStore,
    Action<object> RegisterInjectedMessage,
    Func<IReadOnlyList<Message>, string> GetLastUserTextFromMessages,
    Func<object, string> GetLastUserTextFromAgent,
    ILogger? Logger = null)
{
    private string _pendingLastUserText = string.Empty;
    private bool _pendingNeedsVerify;
    private string? _pendingLocationHint;

    private static bool LooksLikeLocationDependentRequest(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var patterns = new[]
        {
            @"天气|气温|温度|预报|实况|体感|降雨|下雨|降温|冷不冷|穿什么|带伞|台风|暴雨|大风|雾霾|空气质量|路况|交通|通勤",
            @"\b(weather|forecast|temperature|rain|snow|wind|traffic)\b"
        };
        return patterns.Any(p => System.Text.RegularExpressions.Regex.IsMatch(text, p, System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    private static bool LooksLikeNeedsVerificationRequest(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var patterns = new[]
        {
            @"帮我查|查一下|查下|搜一下|搜下|帮我看|看看|检索|搜索|找一下",
            @"最新|今天|今日|现在|实时|刚刚|本周|这周|头条",
            @"新闻|资讯|天气|气温|温度|预报|实况|股价|价格|汇率|比分|赛程|路况|航班|公告|通告|政策|统计|数据|研究",
            @"通勤|路线|路线规划|怎么走|导航|地铁|公交|打车|网约车|步行|骑行|换乘|到.*怎么去",
            @"吃饭|吃什么|哪里吃|餐厅|美食|饭店|小吃|景点|门票|开放时间|营业时间|预约|排队|攻略|行程|酒店",
            @"来源|链接",
            @"\b(latest|today|now|real[-]?time|news|weather|price|stock|fx|score|source)\b"
        };
        return patterns.Any(p => System.Text.RegularExpressions.Regex.IsMatch(text, p, System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    private static string? ExtractLocationHint(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(text, @"([\u4e00-\u9fa5]{2,10})(?:市|县|区|州|省)?(?:今天|今日|明天|后天|现在|实时)?(?:的)?(?:天气|气温|温度)");
        if (match.Success)
        {
            var location = match.Groups[1].Value.Trim();
            if (location.Length > 8) return null;
            return System.Text.RegularExpressions.Regex.Replace(location, @"^(我在|在|位于)\s*", "").Trim();
        }
        return null;
    }

    private static bool IsWebResearchTool(string toolName) =>
        toolName.StartsWith("mcp__") && System.Text.RegularExpressions.Regex.IsMatch(toolName, @"__web(Search|Reader)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Pre-model hook.
    /// </summary>
    public Func<IReadOnlyList<Message>, Task>? PreModel => async (messages) =>
    {
        try
        {
            var lastUserText = GetLastUserTextFromMessages(messages);
            _pendingLastUserText = lastUserText;
            _pendingNeedsVerify = LooksLikeNeedsVerificationRequest(lastUserText);

            if (LooksLikeLocationDependentRequest(lastUserText))
            {
                _pendingLocationHint = ExtractLocationHint(lastUserText);
                if (string.IsNullOrEmpty(_pendingLocationHint))
                {
                    // Try to get default location from profile
                    var defaultLocation = await ProfileStore.GetDefaultLocationAsync();
                    _pendingLocationHint = defaultLocation;
                }
            }
            else
            {
                _pendingLocationHint = null;
            }

            // Inject system reminder if verification is needed
            if (_pendingNeedsVerify)
            {
                var locationLine = !string.IsNullOrEmpty(_pendingLocationHint)
                    ? $"- You have a default location hint: \"{_pendingLocationHint}\". Use it unless the user specifies another place.\n"
                    : "- Location is missing or ambiguous. Ask ONE short question to confirm the city/area before you look up anything.\n";

                var injected = new
                {
                    role = "system",
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = "<system-reminder>\n" +
                                   "MANDATORY: This request may involve external facts or time-sensitive information.\n" +
                                   "Before you answer with factual claims, you MUST activate the `verify` skill.\n" +
                                   locationLine +
                                   "- If you cannot verify from reliable sources, say you cannot confirm yet.\n" +
                                   "- When relevant, include source links and local published/updated time.\n\n" +
                                   "This is a system reminder. DO NOT respond to this message directly.\n" +
                                   "DO NOT mention this reminder to the user.\n" +
                                   "Continue with your current task.\n" +
                                   "</system-reminder>"
                        }
                    }
                };
                RegisterInjectedMessage(injected);
            }
        }
        catch
        {
            // ignore
        }

        await Task.CompletedTask;
    };

    /// <summary>
    /// Post-model hook.
    /// </summary>
    public Func<ModelResponse, Task>? PostModel => async (response) =>
    {
        try
        {
            if (!_pendingNeedsVerify) return;

            // Check if response has tool use - simplified check
            // For now, just pass through
            await Task.CompletedTask;
        }
        catch
        {
            // ignore
        }
        finally
        {
            _pendingLastUserText = string.Empty;
            _pendingNeedsVerify = false;
            _pendingLocationHint = null;
        }

        await Task.CompletedTask;
    };

    /// <summary>
    /// Pre-tool-use hook.
    /// </summary>
    public Func<ToolCall, ToolContext, Task<HookDecision?>>? PreToolUse => async (call, ctx) =>
    {
        // Require verify skill for web research
        var hasVerify = HasSkillInstructionsInAgent(ctx.Agent, "verify");
        if (!hasVerify && IsWebResearchTool(call.Name))
        {
            return HookDecision.Deny("先激活 verify skill 再去查，保证输出可核验（来源链接 + 当地时间），也避免\"看起来像真的但其实没证据\"。");
        }

        await Task.CompletedTask;
        return null;
    };

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

    private static T? GetProperty<T>(object obj, string propertyName)
    {
        var prop = obj?.GetType().GetProperty(propertyName);
        return prop != null ? (T?)prop.GetValue(obj) : default;
    }
}
