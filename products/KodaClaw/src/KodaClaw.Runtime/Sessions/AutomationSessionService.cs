using System.Text;
using KodaClaw.Contracts;
using KodaClaw.McpHub;
using KodaClaw.ModelHub;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Agent;
using Kode.Agent.Sdk.Core.Context;
using Kode.Agent.Sdk.Core.Skills;
using Kode.Agent.Sdk.Core.Types;
using AgentRuntime = Kode.Agent.Sdk.Core.Agent.Agent;

namespace KodaClaw.Runtime;

public sealed class AutomationSessionService : IAutomationSessionService, IAsyncDisposable
{
    private static readonly IReadOnlyList<string> BaselineContextFiles =
    [
        KodaClawWorkspaceLayout.AgentsFile,
        KodaClawWorkspaceLayout.IdentityFile,
        KodaClawWorkspaceLayout.SoulFile,
        KodaClawWorkspaceLayout.OntologyFile,
        KodaClawWorkspaceLayout.UserFile,
        KodaClawWorkspaceLayout.HeartbeatFile,
    ];

    private readonly IWorkspaceService _workspaceService;
    private readonly IMainSessionAgentDependenciesFactory _dependenciesFactory;
    private readonly AutomationSessionOptions _options;
    private readonly IRuntimeConfigurationResolver? _runtimeConfigurationResolver;
    private readonly IProviderAccountRepository? _accountRepository;
    private readonly IMcpHubService? _mcpHubService;
    private readonly ISettingsRepository? _settingsRepository;

    public AutomationSessionService(
        IWorkspaceService workspaceService,
        IMainSessionAgentDependenciesFactory dependenciesFactory,
        AutomationSessionOptions? options = null,
        IRuntimeConfigurationResolver? runtimeConfigurationResolver = null,
        IProviderAccountRepository? accountRepository = null,
        IMcpHubService? mcpHubService = null,
        ISettingsRepository? settingsRepository = null)
    {
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
        _dependenciesFactory = dependenciesFactory ?? throw new ArgumentNullException(nameof(dependenciesFactory));
        _options = options ?? new AutomationSessionOptions();
        _runtimeConfigurationResolver = runtimeConfigurationResolver;
        _accountRepository = accountRepository;
        _mcpHubService = mcpHubService;
        _settingsRepository = settingsRepository;
    }

    public async Task<AutomationSessionHandle> StartAutomationSessionAsync(
        AutomationDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        EnsureDefinitionIsValid(definition);

        var snapshot = await _workspaceService.EnsureInitializedAsync(cancellationToken);
        var contextDocuments = await LoadContextDocumentsAsync(snapshot.RootPath, definition, cancellationToken);
        var promptCharBudget = await ResolvePromptCharacterBudgetAsync(cancellationToken);
        var prompt = BuildSystemPrompt(definition, contextDocuments, promptCharBudget);
        var systemPrompt = prompt.SystemPrompt;

        var sessionId = GenerateSessionId(definition.Id);
        var sessionDirectory = _workspaceService.GetSessionDirectory(sessionId);
        Directory.CreateDirectory(sessionDirectory);
        await SessionPromptReportStore.WriteAsync(sessionDirectory, prompt, cancellationToken);

        var dependencies = _dependenciesFactory.Create(sessionId, sessionDirectory);
        var configuredModel = await ResolveConfiguredModelAsync(definition, cancellationToken);
        var sessionTools = await BuildSessionToolsAsync(sessionId, dependencies.ToolRegistry, cancellationToken);
        var maxIterations = await ResolveMaxIterationsAsync(cancellationToken);
        var contextWindowSize = await ResolveContextWindowSizeAsync(cancellationToken);
        var agent = await AgentRuntime.CreateAsync(
            sessionId,
            CreateAgentConfig(sessionDirectory, systemPrompt, configuredModel, contextWindowSize, sessionTools, maxIterations),
            dependencies,
            cancellationToken);

        // The caller (AutomationScheduler) is responsible for disposing the agent via try/finally.
        return new AutomationSessionHandle(
            SessionId: sessionId,
            AutomationId: definition.Id,
            SessionKind: SessionKind.Automation,
            SessionDirectory: sessionDirectory,
            Agent: agent);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void EnsureDefinitionIsValid(AutomationDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id))
        {
            throw new ArgumentException("Automation definition id is required.", nameof(definition));
        }

        if (string.IsNullOrWhiteSpace(definition.Prompt))
        {
            throw new ArgumentException("Automation definition prompt is required.", nameof(definition));
        }
    }

    private async Task<IReadOnlyList<PromptContextDocument>> LoadContextDocumentsAsync(
        string workspaceRoot,
        AutomationDefinition definition,
        CancellationToken cancellationToken)
    {
        var documents = new List<PromptContextDocument>();
        var seenPaths = new HashSet<string>(GetPathComparer());
        var workspaceDirectory = Path.Combine(workspaceRoot, KodaClawWorkspaceLayout.WorkspaceDirectory);

        foreach (var baselineFile in BaselineContextFiles)
        {
            var absolutePath = Path.Combine(workspaceDirectory, baselineFile);
            await TryAddContextDocumentAsync(absolutePath, workspaceRoot, seenPaths, documents, cancellationToken);
        }

        if (definition.InputPaths is { Count: > 0 })
        {
            foreach (var inputPath in definition.InputPaths)
            {
                var absolutePath = ResolveInputPath(workspaceRoot, inputPath);
                await TryAddContextDocumentAsync(absolutePath, workspaceRoot, seenPaths, documents, cancellationToken);
            }
        }

        return documents;
    }

    private static async Task TryAddContextDocumentAsync(
        string absolutePath,
        string workspaceRoot,
        HashSet<string> seenPaths,
        ICollection<PromptContextDocument> documents,
        CancellationToken cancellationToken)
    {
        if (!seenPaths.Add(absolutePath))
        {
            return;
        }

        if (!File.Exists(absolutePath))
        {
            return;
        }

        var content = await File.ReadAllTextAsync(absolutePath, cancellationToken);
        var displayPath = ToDisplayPath(workspaceRoot, absolutePath);
        documents.Add(new PromptContextDocument(displayPath, content));
    }

    private static string ResolveInputPath(string workspaceRoot, string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new InvalidOperationException("Automation input path cannot be empty.");
        }

        var normalizedInputPath = inputPath.Replace('\\', '/').Trim();
        var candidate = Path.IsPathRooted(normalizedInputPath)
            ? Path.GetFullPath(normalizedInputPath)
            : ResolveWorkspaceRelativePath(workspaceRoot, normalizedInputPath);
        if (!IsPathInsideRoot(workspaceRoot, candidate))
        {
            throw new InvalidOperationException($"Automation input path '{inputPath}' escapes workspace root.");
        }

        return candidate;
    }

    private static string ResolveWorkspaceRelativePath(string workspaceRoot, string inputPath)
    {
        if (inputPath.StartsWith($"{KodaClawWorkspaceLayout.WorkspaceDirectory}/", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(Path.Combine(workspaceRoot, inputPath));
        }

        return Path.GetFullPath(Path.Combine(
            workspaceRoot,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            inputPath));
    }

    private static bool IsPathInsideRoot(string rootPath, string candidatePath)
    {
        var fullRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(fullRoot, fullCandidate, comparison))
        {
            return true;
        }

        var rootPrefix = fullRoot + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(rootPrefix, comparison);
    }

    private static string ToDisplayPath(string workspaceRoot, string absolutePath)
    {
        var relativePath = Path.GetRelativePath(workspaceRoot, absolutePath);
        return relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    private async Task<IReadOnlyList<string>> BuildSessionToolsAsync(
        string sessionId,
        IToolRegistry? toolRegistry,
        CancellationToken cancellationToken)
    {
        var tools = new List<string>(_options.Tools);
        if (_mcpHubService is null || toolRegistry is null)
        {
            return tools;
        }

        var merged = new HashSet<string>(tools, StringComparer.OrdinalIgnoreCase);
        var mcpResult = await _mcpHubService.InjectToolsAsync(sessionId, SessionKind.Automation, toolRegistry, cancellationToken);
        foreach (var toolName in mcpResult.InjectedToolNames)
        {
            if (merged.Add(toolName))
            {
                tools.Add(toolName);
            }
        }

        return tools;
    }

    private async Task<int> ResolveMaxIterationsAsync(CancellationToken cancellationToken)
    {
        if (_settingsRepository is null) return _options.MaxIterations;
        var settings = await _settingsRepository.GetAsync(cancellationToken);
        return settings.AutomationMaxIterations ?? _options.MaxIterations;
    }

    private AgentConfig CreateAgentConfig(
        string sessionDirectory,
        string systemPrompt,
        string model,
        int contextWindowSize,
        IReadOnlyList<string>? tools = null,
        int? maxIterations = null)
    {
        var skillsPaths = _workspaceService.GetSkillsPaths();
        return new AgentConfig
        {
            Model = model,
            SystemPrompt = systemPrompt,
            MaxIterations = maxIterations ?? _options.MaxIterations,
            Tools = tools ?? _options.Tools,
            Permissions = (_options.Permissions ?? new PermissionConfig()) with { SchemaHiddenTools = BuiltinSkills.SkillGatedTools },
            SandboxOptions = new SandboxOptions
            {
                WorkingDirectory = _workspaceService.RootPath,
                EnforceBoundary = true,
                AllowPaths = skillsPaths,
            },
            Skills = new SkillsConfig
            {
                Paths = skillsPaths,
                ValidateOnLoad = false,
                AutoActivate = BuiltinSkills.AutomationAutoActivate,
            },
            Context = new ContextManagerOptions
            {
                MaxTokens = (int)(contextWindowSize * _options.ContextCompressionTriggerRatio),
                CompressToTokens = (int)(contextWindowSize * _options.ContextCompressionTargetRatio),
                CompressionPrompt = _options.CompressionPrompt,
                ToolResultCompression = new ToolResultCompressionOptions
                {
                    Enabled = true,
                    ThresholdBytes = 81_920,
                },
            },
            SessionType = "automation",
        };
    }

    private Task<string> ResolveConfiguredModelAsync(
        AutomationDefinition definition,
        CancellationToken cancellationToken)
    {
        // Per-automation model takes priority; falls back to the global session option.
        var preferredModel = string.IsNullOrWhiteSpace(definition.ModelId)
            ? _options.Model
            : definition.ModelId;

        return RuntimeProviderSelector.ResolveModelOrFallbackAsync(
            _runtimeConfigurationResolver,
            preferredModel,
            _accountRepository,
            cancellationToken);
    }

    private async Task<int> ResolvePromptCharacterBudgetAsync(CancellationToken cancellationToken)
    {
        if (_accountRepository is null) return _options.MaxPromptCharacters;
        try
        {
            var resolved = await _accountRepository.ResolveDefaultForAsync(
                ModelCapabilitySet.Text, cancellationToken);
            if (resolved is null) return _options.MaxPromptCharacters;
            var maxOutputCap = Math.Min(resolved.Model.MaxOutputTokens, Math.Min((int)(resolved.Model.ContextWindowSize * 0.20), 16_384));
            var usableTokens = Math.Max(resolved.Model.ContextWindowSize - maxOutputCap, 0);
            return Math.Max((int)((long)usableTokens * 4 / 5), _options.MaxPromptCharacters);
        }
        catch
        {
            return _options.MaxPromptCharacters;
        }
    }

    private async Task<int> ResolveContextWindowSizeAsync(CancellationToken cancellationToken)
    {
        if (_accountRepository is null) return _options.DefaultContextWindowSize;
        try
        {
            var resolved = await _accountRepository.ResolveDefaultForAsync(
                ModelCapabilitySet.Text, cancellationToken);
            if (resolved is null) return _options.DefaultContextWindowSize;
            var maxOutputCap = Math.Min(resolved.Model.MaxOutputTokens, Math.Min((int)(resolved.Model.ContextWindowSize * 0.20), 16_384));
            var available = resolved.Model.ContextWindowSize - maxOutputCap;
            return available > 0 ? available : _options.DefaultContextWindowSize;
        }
        catch
        {
            return _options.DefaultContextWindowSize;
        }
    }

    private PromptBuildResult BuildSystemPrompt(
        AutomationDefinition definition,
        IReadOnlyList<PromptContextDocument> contextDocuments,
        int promptCharBudget)
    {
        var triggeredAt = DateTimeOffset.Now;
        var builder = new PromptBuilder(PromptProfiles.Automation(_options.SystemPrompt))
            .WithCharacterBudget(promptCharBudget)
            .AddBody($"Triggered at: {triggeredAt:yyyy-MM-dd HH:mm:ss zzz} ({triggeredAt.DayOfWeek}).")
            .AddSection("Runtime Environment", RuntimeEnvironmentContext.BuildLines(_workspaceService.RootPath))
            .AddSection(
                "Automation Definition",
                [
                    $"Id: {definition.Id}",
                    !string.IsNullOrWhiteSpace(definition.Title) ? $"Title: {definition.Title}" : string.Empty,
                ])
            .AddSection("Prompt", definition.Prompt.Trim())
            .AddSection(
                "Memory Boundary",
                [
                    "Treat only the loaded context files below as available memory for this run.",
                    "Do not infer or recall workspace/MEMORY.md unless it was explicitly loaded as an automation input.",
                    "If required context is missing, state that gap instead of pretending the automation remembers it.",
                ]);

        if (Environment.GetEnvironmentVariable("KODACLAW_DOCKER_MODE") == "true")
        {
            builder.AddBody("""
                ## File Persistence (Docker Deployment)
                Running inside a Docker container. Only paths under /data/ persist across restarts:
                - ~/  (→ /data/home/) — tool binaries, credentials, code outputs
                - workspace/  (→ /data/workspace/) — identity, memory, rules

                Save work outputs to ~/projects/ or workspace/outputs/.
                Do not write to /tmp/ or relative paths — they resolve to /app/ and vanish on restart.
                """);
        }

        var prompt = builder.AddContextDocuments(contextDocuments).Build();

        return prompt;
    }

    private static string GenerateSessionId(string automationId)
    {
        var builder = new StringBuilder(capacity: 20);
        foreach (var value in automationId)
        {
            if (char.IsLetterOrDigit(value) || value is '-' or '_')
            {
                builder.Append(char.ToLowerInvariant(value));
                if (builder.Length >= 20)
                {
                    break;
                }
            }
        }

        var safeAutomationId = builder.Length > 0
            ? builder.ToString()
            : "automation";
        var suffix = Guid.NewGuid().ToString("N")[..8];

        return $"auto-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{safeAutomationId}-{suffix}";
    }

    private static IEqualityComparer<string> GetPathComparer()
    {
        return OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }
}
