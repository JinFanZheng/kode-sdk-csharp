using AgentImpl = Kode.Agent.Sdk.Core.Agent.Agent;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;
using Kode.Agent.Tools.Builtin;
using Kode.Agent.WebApiAssistant.Assistant.Hooks;
using Kode.Agent.WebApiAssistant.Extensions;
using Microsoft.Extensions.Logging;

namespace Kode.Agent.WebApiAssistant.Assistant;

/// <summary>
/// Result of building assistant hooks, includes hooks and MCP tool names.
/// </summary>
public record BuildAssistantHooksResult
{
    /// <summary>
    /// The composed hooks instance.
    /// </summary>
    public required Kode.Agent.Sdk.Core.Hooks.IHooks Hooks { get; init; }

    /// <summary>
    /// List of MCP tool names (namespaced as mcp__{server}__{tool}).
    /// </summary>
    public required IReadOnlyList<string> McpToolNames { get; init; }
}

/// <summary>
/// Options for building assistant hooks.
/// </summary>
public record BuildAssistantHooksOptions
{
    /// <summary>
    /// Agent sandbox dataDir (user data directory). Used in preModel phase to read/write profile.
    /// </summary>
    public string? DataDir { get; init; }
}

/// <summary>
/// Builder for creating Personal Assistant agents.
/// Follows the TypeScript createAssistant pattern.
/// </summary>
public static class AssistantBuilder
{
    /// <summary>
    /// Generate a unique agent ID.
    /// Format: agt:{timestamp}{random} (26 characters total)
    /// Matches the SDK's Agent.GenerateAgentId() implementation.
    /// </summary>
    public static string GenerateAgentId()
    {
        var chars = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Encode timestamp (10 characters)
        var timePart = new char[10];
        var num = now;
        for (var i = 9; i >= 0; i--)
        {
            timePart[i] = chars[(int)(num % chars.Length)];
            num /= (long)chars.Length;
        }

        // Random part (16 characters)
        var random = new char[16];
        var rand = new Random();
        for (var i = 0; i < 16; i++)
        {
            random[i] = chars[rand.Next(chars.Length)];
        }

        return $"agt_{new string(timePart)}{new string(random)}";
    }

    /// <summary>
    /// Create a Personal Assistant Agent.
    /// </summary>
    public static async Task<AgentImpl> CreateAssistantAsync(
        CreateAssistantOptions options,
        AgentDependencies globalDeps,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default)
    {
        var logger = loggerFactory.CreateLogger(typeof(AssistantBuilder).FullName!);
        var workDir = options.WorkDir ?? Directory.GetCurrentDirectory();
        // var storeDir = options.StoreDir ?? Path.Combine(workDir, ".assistant-store");

        // // Ensure store directory exists
        // Directory.CreateDirectory(storeDir);

        // Determine effective agent ID first (needed for data dir fallback)
        var effectiveAgentId = !string.IsNullOrEmpty(options.AgentId)
            ? options.AgentId!
            : GenerateAgentId();

        // Determine data directory
        // Priority: userDataDir > userId > agent-specific data (for isolation)
        var dataDir = !string.IsNullOrEmpty(options.UserDataDir)
            ? options.UserDataDir
            : !string.IsNullOrEmpty(options.UserId)
                ? Path.Combine(workDir, ".users", options.UserId!)
                : Path.Combine(workDir, "data");

        // Ensure data directory structure
        EnsureDataDirectory(dataDir, loggerFactory);

        // Create per-agent dependencies with tool registry
        var agentDeps = await CreateAgentDependenciesAsync(
            string.Empty, // agentId not needed for deps creation
            globalDeps,
            serviceProvider,
            loggerFactory,
            dataDir,
            options.Skills?.Paths,
            workDir);

        // Get MCP tool names
        var toolRegistry = agentDeps.ToolRegistry as Kode.Agent.Sdk.Tools.ToolRegistry;
        var allToolNames = toolRegistry?.List().ToList() ?? new List<string>();
        var mcpToolNames = allToolNames
            .Where(name => name.StartsWith("mcp__"))
            .ToList();

        // Build assistant hooks
        var hooksResult = AssistantHooks.Build(
            agentDeps,
            new BuildAssistantHooksOptions { DataDir = dataDir },
            logger);

        // var agentDir = Path.Combine(storeDir, effectiveAgentId);
        // Directory.CreateDirectory(agentDir);

        // Build allowPaths
        var allowPaths = new List<string>
        {
            // agentDir,
            dataDir,
            workDir
        };

        // Add Skills paths if configured
        if (options.Skills?.Paths != null)
        {
            foreach (var skillsPath in options.Skills.Paths)
            {
                var resolvedPath = Path.IsPathRooted(skillsPath)
                    ? skillsPath
                    : Path.Combine(workDir, skillsPath);
                if (Directory.Exists(resolvedPath))
                {
                    allowPaths.Add(resolvedPath);
                }
            }
        }

        // Merge MCP tool names into allowTools
        var mergedAllowTools = MergeAllowTools(options.Permissions?.AllowTools, mcpToolNames);

        var config = new AgentConfig
        {
            Model = options.Model ?? "claude-sonnet-4-5-20250929",
            SystemPrompt = options.SystemPrompt,
            TemplateId = AssistantTemplate.PersonalAssistantTemplateId,
            MaxIterations = 50,
            MaxTokens = options.MaxTokens,
            Temperature = options.Temperature,
            Tools = ["*"], // Allow all tools
            Permissions = (options.Permissions ?? new PermissionConfig()) with
            {
                Mode = options.Permissions?.Mode ?? "auto",
                AllowTools = mergedAllowTools
            },
            SandboxOptions = new SandboxOptions
            {
                WorkingDirectory = dataDir,
                EnforceBoundary = true,
                AllowPaths = allowPaths
            },
            Skills = options.Skills,
            Hooks = [hooksResult.Hooks]
        };

        AgentImpl agent;

        // Resume or create agent
        if (await globalDeps.Store.ExistsAsync(effectiveAgentId, cancellationToken))
        {
            var overrides = new AgentConfigOverrides
            {
                Model = config.Model,
                SystemPrompt = config.SystemPrompt,
                TemplateId = config.TemplateId,
                SandboxOptions = config.SandboxOptions,
                MaxTokens = config.MaxTokens,
                Temperature = config.Temperature,
                Tools = config.Tools,
                Permissions = config.Permissions,
                Hooks = config.Hooks,
                Skills = config.Skills
            };

            agent = await AgentImpl.ResumeFromStoreAsync(
                effectiveAgentId,
                agentDeps,
                overrides: overrides,
                cancellationToken: cancellationToken);
        }
        else
        {
            agent = await AgentImpl.CreateAsync(
                effectiveAgentId,
                config,
                agentDeps,
                cancellationToken);
        }

        logger.LogInformation("Assistant {AgentId} created/resumed successfully", effectiveAgentId);
        return agent;
    }

    /// <summary>
    /// Ensure data directory structure exists.
    /// </summary>
    private static void EnsureDataDirectory(string dataDir, ILoggerFactory loggerFactory)
    {
        var memoryDir = Path.Combine(dataDir, ".memory");
        var knowledgeDir = Path.Combine(dataDir, ".knowledge");
        var configDir = Path.Combine(dataDir, ".config");
        var tasksDir = Path.Combine(dataDir, ".tasks");

        foreach (var dir in new[] { memoryDir, knowledgeDir, configDir, tasksDir })
        {
            Directory.CreateDirectory(dir);
        }

        InitDefaultConfigs(configDir);
        InitDefaultMemory(memoryDir);
    }

    /// <summary>
    /// Initialize default configuration files.
    /// </summary>
    private static void InitDefaultConfigs(string configDir)
    {
        // Notify config
        var notifyConfigPath = Path.Combine(configDir, "notify.json");
        if (!File.Exists(notifyConfigPath))
        {
            var defaultNotifyConfig = new
            {
                @default = "dingtalk",
                channels = new
                {
                    dingtalk = new
                    {
                        webhook = "",
                        secret = ""
                    },
                    wecom = new
                    {
                        webhook = ""
                    },
                    telegram = new
                    {
                        botToken = "",
                        chatId = ""
                    }
                }
            };
            WriteJson(notifyConfigPath, defaultNotifyConfig);
        }

        // Email config
        var emailConfigPath = Path.Combine(configDir, "email.json");
        if (!File.Exists(emailConfigPath))
        {
            var defaultEmailConfig = new
            {
                imap = new
                {
                    host = "imap.gmail.com",
                    port = 993,
                    secure = true,
                    auth = new
                    {
                        user = "",
                        pass = ""
                    }
                },
                smtp = new
                {
                    host = "smtp.gmail.com",
                    port = 587,
                    secure = false,
                    auth = new
                    {
                        user = "",
                        pass = ""
                    }
                }
            };
            WriteJson(emailConfigPath, defaultEmailConfig);
        }
    }

    /// <summary>
    /// Initialize default memory structure.
    /// </summary>
    private static void InitDefaultMemory(string memoryDir)
    {
        var profilePath = Path.Combine(memoryDir, "profile.json");
        if (!File.Exists(profilePath))
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            var defaultProfile = new
            {
                assistantName = "Koda",
                userName = "",
                timezone = "Asia/Shanghai",
                language = "zh-CN",
                preferences = new { },
                createdAt = now,
                updatedAt = now
            };
            WriteJson(profilePath, defaultProfile);
        }

        Directory.CreateDirectory(Path.Combine(memoryDir, "facts"));
    }

    /// <summary>
    /// Write JSON to file with indentation.
    /// </summary>
    private static void WriteJson<T>(string path, T value)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Create agent-specific dependencies.
    /// </summary>
    private static async Task<AgentDependencies> CreateAgentDependenciesAsync(
        string agentId,
        AgentDependencies globalDeps,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory,
        string dataDir,
        IReadOnlyList<string>? skillsPaths = null,
        string? workDir = null)
    {
        // Create a new tool registry for this agent
        var agentRegistry = new Kode.Agent.Sdk.Tools.ToolRegistry();

        // Register built-in tools
        agentRegistry.RegisterBuiltinTools();

        // Register platform tools (time)
        agentRegistry.RegisterPlatformTools(serviceProvider);

        // Load agent-specific tools from .config directory (email, notify)
        var agentToolsLoader = new Services.AgentToolsLoader(loggerFactory, serviceProvider);
        await agentToolsLoader.LoadAgentToolsAsync(agentId, dataDir, agentRegistry);

        // Load MCP tools (from global configuration)
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var mcpManager = serviceProvider.GetRequiredService<Kode.Agent.Mcp.McpClientManager>();
        var mcpServersLoader = new Services.McpServersLoader(mcpManager, loggerFactory.CreateLogger<Services.McpServersLoader>());
        await mcpServersLoader.LoadAndRegisterServersAsync(configuration, agentRegistry);

        // Get template registry from global deps
        var templateRegistry = globalDeps.TemplateRegistry;
        if (templateRegistry == null)
        {
            templateRegistry = new Sdk.Core.Templates.AgentTemplateRegistry();
            templateRegistry.Register(AssistantTemplate.CreatePersonalAssistantTemplate(skillsPaths, workDir));
        }

        return globalDeps with
        {
            ToolRegistry = agentRegistry, 
            TemplateRegistry = templateRegistry, 
            LoggerFactory = loggerFactory
        };
    }

    /// <summary>
    /// Merge allow tools with MCP tool names.
    /// </summary>
    private static IReadOnlyList<string>? MergeAllowTools(
        IReadOnlyList<string>? baseAllowTools,
        IReadOnlyList<string> mcpToolNames)
    {
        if (baseAllowTools == null) return null;
        if (baseAllowTools.Contains("*")) return baseAllowTools;

        return baseAllowTools
            .Concat(mcpToolNames)
            .Distinct()
            .ToList();
    }
}
