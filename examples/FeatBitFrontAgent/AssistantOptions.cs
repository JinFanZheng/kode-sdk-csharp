using Kode.Agent.Sdk.Core.Skills;
using Kode.Agent.Sdk.Core.Types;

namespace Kode.Agent.WebApiAssistant;

public sealed record AssistantOptions
{
    public required string WorkDir { get; init; }
    public required string StoreDir { get; init; }
    public string? ApiKey { get; init; }
    public required string DefaultProvider { get; init; }
    public required string DefaultModel { get; init; }
    public string? DefaultSystemPrompt { get; init; }
    public required IReadOnlyList<string> Tools { get; init; }
    public required PermissionConfig PermissionConfig { get; init; }

    /// <summary>
    /// Skills 配置
    /// </summary>
    public SkillsConfig? SkillsConfig { get; init; }

    public static AssistantOptions FromConfiguration(IConfiguration configuration, string defaultWorkDir)
    {
        var workDir = GetFirst(configuration, "Kode:WorkDir", "KODE_WORK_DIR", defaultWorkDir).Trim();
        if (string.IsNullOrWhiteSpace(workDir))
        {
            workDir = defaultWorkDir;
        }
        var resolvedWorkDir = Path.GetFullPath(workDir);

        var provider = GetFirst(configuration, "Kode:DefaultProvider", "DEFAULT_PROVIDER", "anthropic").Trim();
        var defaultModelFromConfig = GetFirst(configuration, "Kode:DefaultModel", "KODE_DEFAULT_MODEL", "");

        var defaultModel = !string.IsNullOrWhiteSpace(defaultModelFromConfig)
            ? defaultModelFromConfig
            : provider.Equals("openai", StringComparison.OrdinalIgnoreCase)
                ? configuration["Kode:OpenAI:DefaultModel"] ?? GetFirst(configuration, "OPENAI_MODEL_ID", "gpt-4o")
                : configuration["Kode:Anthropic:ModelId"] ?? GetFirst(configuration, "ANTHROPIC_MODEL_ID", "claude-sonnet-4-20250514");

        var tools = ParseList(GetFirst(configuration, "Kode:Tools", "KODE_TOOLS", "fs_read,fs_list,fs_glob,fs_grep"));
        var storeDirRaw = GetFirst(configuration, "Kode:StoreDir", "KODE_STORE_DIR", "./.kode").Trim();
        var resolvedStoreDir = Path.IsPathRooted(storeDirRaw)
            ? Path.GetFullPath(storeDirRaw)
            : Path.GetFullPath(Path.Combine(resolvedWorkDir, storeDirRaw));

        // 构建技能配置
        var skillsConfig = BuildSkillsConfig(configuration, resolvedWorkDir);

        return new AssistantOptions
        {
            WorkDir = resolvedWorkDir,
            StoreDir = resolvedStoreDir,
            ApiKey = GetFirstOrNull(configuration, "Kode:ApiKey", "KODE_API_KEY"),
            DefaultProvider = provider,
            DefaultModel = defaultModel,
            DefaultSystemPrompt = GetFirstOrNull(configuration, "Kode:SystemPrompt", "KODE_SYSTEM_PROMPT"),
            Tools = tools,
            PermissionConfig = BuildPermissionConfig(configuration),
            SkillsConfig = skillsConfig
        };
    }

    public static AssistantOptions FromEnvironment()
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        return FromConfiguration(configuration, Directory.GetCurrentDirectory());
    }

    private static IReadOnlyList<string> ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
    }

    private static PermissionConfig BuildPermissionConfig(IConfiguration configuration)
    {
        var modeRaw = GetFirst(configuration, "Kode:PermissionMode", "KODE_PERMISSION_MODE", "auto").Trim();
        var mode = modeRaw.ToLowerInvariant() switch
        {
            "approval" => "approval",
            "readonly" => "readonly",
            "read-only" => "readonly",
            "read_only" => "readonly",
            // Back-compat for older config names.
            "strict" => "approval",
            "default" => "approval",
            _ => "auto"
        };

        // 解析工具白名单（默认允许所有工具，包括 MCP 工具）
        var allowToolsRaw = GetFirst(configuration, "Kode:AllowTools", "KODE_ALLOW_TOOLS",
            "*," +
            "fs_read,fs_write,fs_edit,fs_grep,fs_glob,fs_multi_edit," +
            "email_list,email_read,email_draft,email_move," +
            "notify_send,time_now," +
            "web_search,web_reader,web_search_prime,read_url," +
            "skill_list,skill_activate,skill_resource," +
            "todo_read,todo_write,bash_run,bash_logs");
        var allowTools = ParseList(allowToolsRaw);

        // 解析需要审批的工具
        var requireApprovalToolsRaw = GetFirst(configuration, "Kode:RequireApprovalTools", "KODE_REQUIRE_APPROVAL_TOOLS",
            "email_send,email_delete,fs_rm");
        var requireApprovalTools = ParseList(requireApprovalToolsRaw);

        // 解析禁止的工具
        var denyToolsRaw = GetFirst(configuration, "Kode:DenyTools", "KODE_DENY_TOOLS", "bash_kill");
        var denyTools = ParseList(denyToolsRaw);

        return new PermissionConfig
        {
            Mode = mode,
            AllowTools = allowTools,
            RequireApprovalTools = requireApprovalTools,
            DenyTools = denyTools
        };
    }

    private static SkillsConfig BuildSkillsConfig(IConfiguration configuration, string workDir)
    {
        // 获取技能目录路径
        var skillsDirRaw = GetFirst(configuration, "Kode:SkillsDir", "KODE_SKILLS_DIR", "Skills").Trim();
        var resolvedSkillsDir = Path.IsPathRooted(skillsDirRaw)
            ? Path.GetFullPath(skillsDirRaw)
            : Path.GetFullPath(Path.Combine(workDir, skillsDirRaw));

        // 获取信任的技能列表
        var trustedSkillsRaw = GetFirst(configuration, "Kode:TrustedSkills", "KODE_TRUSTED_SKILLS", "memory,knowledge,email");
        var trustedSkills = ParseList(trustedSkillsRaw);

        return new SkillsConfig
        {
            Paths = [resolvedSkillsDir],
            Trusted = trustedSkills.ToArray()
        };
    }

    private static string GetFirst(IConfiguration configuration, string key1, string key2, string defaultValue)
    {
        return configuration[key1]
            ?? configuration[key2]
            ?? defaultValue;
    }

    private static string GetFirst(IConfiguration configuration, string key1, string defaultValue)
    {
        return configuration[key1] ?? defaultValue;
    }

    private static string? GetFirstOrNull(IConfiguration configuration, string key1, string key2)
    {
        return configuration[key1] ?? configuration[key2];
    }
}
