namespace KodaClaw.ChannelHub.Commands;

/// <summary>
/// Describes a single registered channel command (control or directive).
/// </summary>
public sealed record ChannelCommandDefinition(
    string Key,
    IReadOnlyList<string> Aliases,
    string Description,
    string Category,
    ChannelControlCommandKind? ControlKind = null,
    ChannelDirectiveKind? DirectiveKind = null);

/// <summary>
/// Static registry of all channel commands and directives.
/// All waves' commands are defined here upfront; the parser/dispatcher are extended wave-by-wave.
/// </summary>
public static class ChannelCommandRegistry
{
    public static readonly IReadOnlyList<ChannelCommandDefinition> All = new List<ChannelCommandDefinition>
    {
        // ── Control commands ─────────────────────────────────────────────────────
        new(
            Key: "new-session",
            Aliases: ["/new", "/clear", "/reset"],
            Description: "开启新会话（清除当前对话历史）；支持可选模型参数：/new <序号> 或 /new <模型ID>",
            Category: "session",
            ControlKind: ChannelControlCommandKind.NewSession),

        new(
            Key: "model",
            Aliases: ["/model", "/models"],
            Description: "查看当前模型或列出所有可用模型（/model list）",
            Category: "session",
            ControlKind: ChannelControlCommandKind.Model),

        new(
            Key: "status",
            Aliases: ["/status", "/s"],
            Description: "查询当前会话状态",
            Category: "session",
            ControlKind: ChannelControlCommandKind.Status),

        new(
            Key: "stop",
            Aliases: ["/stop"],
            Description: "中断当前正在执行的 Agent 轮次",
            Category: "session",
            ControlKind: ChannelControlCommandKind.Stop),

        new(
            Key: "help",
            Aliases: ["/help", "/commands", "/?"],
            Description: "列出所有可用命令",
            Category: "info",
            ControlKind: ChannelControlCommandKind.Help),

        new(
            Key: "compact",
            Aliases: ["/compact"],
            Description: "立即压缩当前会话的上下文窗口",
            Category: "session",
            ControlKind: ChannelControlCommandKind.Compact),

        new(
            Key: "tools",
            Aliases: ["/tools"],
            Description: "列出当前会话可用的工具",
            Category: "info",
            ControlKind: ChannelControlCommandKind.Tools),

        new(
            Key: "info",
            Aliases: ["/info", "/i"],
            Description: "查看当前会话完整信息（模型、上下文占用、绑定、toggle、活动）",
            Category: "info",
            ControlKind: ChannelControlCommandKind.Info),

        new(
            Key: "btw",
            Aliases: ["/btw"],
            Description: "发送一次性旁路问题（不影响主会话上下文）",
            Category: "meta",
            ControlKind: ChannelControlCommandKind.SideQuestion),

        new(
            Key: "think-toggle",
            Aliases: ["/think"],
            Description: "开启/关闭扩展思考（/think on | /think off；/think 不带参数查看当前状态）",
            Category: "modifier",
            ControlKind: ChannelControlCommandKind.ThinkToggle),

        new(
            Key: "stream-toggle",
            Aliases: ["/stream", "/quiet"],
            Description: "开启/关闭流式进度输出（/stream on | /stream off；/quiet 等价 /stream off）",
            Category: "modifier",
            ControlKind: ChannelControlCommandKind.StreamToggle),

        // ── Directive modifiers ──────────────────────────────────────────────────
        new(
            Key: "focus",
            Aliases: ["/focus"],
            Description: "为本次对话追加主题约束（/focus <主题> <正文>）",
            Category: "modifier",
            DirectiveKind: ChannelDirectiveKind.Focus),
    };

    // ── Lookup maps built once at startup ───────────────────────────────────────

    /// <summary>Maps every alias (lowercased) to its definition.</summary>
    private static readonly Dictionary<string, ChannelCommandDefinition> _byAlias =
        All.SelectMany(def => def.Aliases.Select(a => (Alias: a.ToLowerInvariant(), Def: def)))
           .ToDictionary(t => t.Alias, t => t.Def, StringComparer.Ordinal);

    /// <summary>
    /// Looks up a command definition by any of its aliases (case-insensitive).
    /// Returns null when the token is unrecognised.
    /// </summary>
    public static ChannelCommandDefinition? Find(string alias)
        => _byAlias.TryGetValue(alias.ToLowerInvariant(), out var def) ? def : null;

    /// <summary>Returns all control command definitions for a given category.</summary>
    public static IEnumerable<ChannelCommandDefinition> ByCategory(string category)
        => All.Where(d => string.Equals(d.Category, category, StringComparison.OrdinalIgnoreCase));
}
