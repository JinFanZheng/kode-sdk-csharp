using System.Text;
using System.Collections.Concurrent;
using KodaClaw.Contracts.Automations;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Jobs;
using KodaClaw.Contracts.Models;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.Settings;
using KodaClaw.Contracts.Workspace;
using KodaClaw.McpHub;
using KodaClaw.Runtime.Prompt;
using KodaClaw.Runtime.Providers;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Agent;
using Kode.Agent.Sdk.Core.Context;
using Kode.Agent.Sdk.Core.Skills;
using Kode.Agent.Sdk.Core.Types;
using AgentRuntime = Kode.Agent.Sdk.Core.Agent.Agent;

namespace KodaClaw.Runtime.Sessions;

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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _followUpLocks = new(StringComparer.Ordinal);

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

    public async Task<AutomationSessionHandle> StartJobSessionAsync(
        JobDefinition job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        EnsureJobIsValid(job);

        var snapshot = await _workspaceService.EnsureInitializedAsync(cancellationToken);
        var contextDocuments = await LoadBaselineContextDocumentsAsync(snapshot.RootPath, cancellationToken);
        var promptCharBudget = await ResolvePromptCharacterBudgetAsync(cancellationToken);
        var jobContext = BuildJobContextString(job);
        var prompt = BuildJobSystemPrompt(job, jobContext, contextDocuments, promptCharBudget);
        var systemPrompt = prompt.SystemPrompt;

        var sessionId = GenerateSessionId(job.Id);
        var sessionDirectory = _workspaceService.GetSessionDirectory(sessionId);
        Directory.CreateDirectory(sessionDirectory);
        await SessionPromptReportStore.WriteAsync(sessionDirectory, prompt, cancellationToken);

        var dependencies = _dependenciesFactory.Create(sessionId, sessionDirectory);
        var configuredModel = await ResolveConfiguredModelForJobAsync(cancellationToken);
        var sessionTools = await BuildSessionToolsAsync(sessionId, dependencies.ToolRegistry, cancellationToken);
        var maxIterations = await ResolveMaxIterationsAsync(cancellationToken);
        var contextWindowSize = await ResolveContextWindowSizeAsync(cancellationToken);
        var agent = await AgentRuntime.CreateAsync(
            sessionId,
            CreateAgentConfig(sessionDirectory, systemPrompt, configuredModel, contextWindowSize, sessionTools, maxIterations),
            dependencies,
            cancellationToken);

        return new AutomationSessionHandle(
            SessionId: sessionId,
            AutomationId: job.Id,
            SessionKind: SessionKind.Automation,
            SessionDirectory: sessionDirectory,
            Agent: agent);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task<AutomationFollowUpResult> RunFollowUpAsync(
        AutomationFollowUpRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var sessionId = request.Link.SessionId;
        var sessionDirectory = _workspaceService.GetSessionDirectory(sessionId);
        var dependencies = _dependenciesFactory.Create(sessionId, sessionDirectory);
        if (!await dependencies.Store.ExistsAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            return new AutomationFollowUpResult(
                Success: false,
                Response: null,
                ErrorMessage: $"Automation session '{sessionId}' was not found.",
                SessionId: sessionId);
        }

        var semaphore = _followUpLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var threadType = request.Binding.ThreadType;
            var model = await RuntimeProviderSelector.ResolveModelOrFallbackAsync(
                _runtimeConfigurationResolver,
                _options.Model,
                _accountRepository,
                cancellationToken).ConfigureAwait(false);
            var tools = BuildFollowUpTools(threadType);
            var contextWindowSize = await ResolveContextWindowSizeAsync(cancellationToken).ConfigureAwait(false);
            var skillsPaths = _workspaceService.GetSkillsPaths();
            var agent = await AgentRuntime.ResumeFromStoreAsync(
                sessionId,
                dependencies,
                options: new ResumeOptions { Strategy = RecoveryStrategy.Crash },
                overrides: new AgentConfigOverrides
                {
                    Model = model,
                    SystemPrompt = BuildFollowUpSystemPrompt(threadType),
                    Tools = tools,
                    Permissions = new PermissionConfig
                    {
                        Mode = "auto",
                        RequireApprovalTools = [],
                        SchemaHiddenTools = BuiltinSkills.SkillGatedTools,
                        DenyTools = ["channel_send", "channel_list"],
                    },
                    SandboxOptions = new SandboxOptions
                    {
                        WorkingDirectory = threadType == ChannelThreadType.DirectMessage
                            ? _workspaceService.RootPath
                            : sessionDirectory,
                        EnforceBoundary = true,
                        AllowPaths = skillsPaths,
                    },
                    Skills = new SkillsConfig
                    {
                        Paths = skillsPaths,
                        ValidateOnLoad = false,
                        AutoActivate = BuiltinSkills.ChannelAutoActivate,
                    },
                    Context = new ContextManagerOptions
                    {
                        MaxTokens = (int)(contextWindowSize * _options.ContextCompressionTriggerRatio),
                        CompressToTokens = (int)(contextWindowSize * _options.ContextCompressionTargetRatio),
                        CompressionPrompt = _options.CompressionPrompt,
                        ToolResultCompression = new ToolResultCompressionOptions
                        {
                            Enabled = true,
                            ThresholdBytes = _options.ToolResultThresholdBytes,
                        },
                    },
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            AgentRunResult runResult;
            try
            {
                runResult = await agent.RunAsync(
                    BuildFollowUpPrompt(request),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await agent.DisposeAsync().ConfigureAwait(false);
            }

            return new AutomationFollowUpResult(
                Success: runResult.Success,
                Response: runResult.Response,
                ErrorMessage: runResult.ErrorMessage,
                SessionId: sessionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AutomationFollowUpResult(
                Success: false,
                Response: null,
                ErrorMessage: ex.GetBaseException().Message,
                SessionId: sessionId);
        }
        finally
        {
            semaphore.Release();
        }
    }

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

    private static void EnsureJobIsValid(JobDefinition job)
    {
        if (string.IsNullOrWhiteSpace(job.Id))
        {
            throw new ArgumentException("Job id is required.", nameof(job));
        }

        if (string.IsNullOrWhiteSpace(job.Prompt))
        {
            throw new ArgumentException("Job prompt is required.", nameof(job));
        }
    }

    private static string BuildJobContextString(JobDefinition job)
    {
        var sb = new StringBuilder();
        sb.Append("JobId: ").AppendLine(job.Id);
        if (!string.IsNullOrWhiteSpace(job.Name))
            sb.Append("JobName: ").AppendLine(job.Name);
        sb.Append("JobType: ").AppendLine(job.Type.ToString());
        if (job.Cron is not null)
            sb.Append("Cron: ").AppendLine(job.Cron);
        sb.Append("TimeoutMinutes: ").AppendLine(job.TimeoutMinutes.ToString());
        sb.Append("MaxRetries: ").AppendLine(job.MaxRetries.ToString());
        if (job.Channels.Count > 0)
            sb.Append("Channels: ").AppendLine(string.Join(", ", job.Channels));
        sb.Append("DeliveryMode: ").AppendLine(job.DeliveryMode.ToString());
        return sb.ToString();
    }

    private async Task<IReadOnlyList<PromptContextDocument>> LoadBaselineContextDocumentsAsync(
        string workspaceRoot,
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

        return documents;
    }

    private PromptBuildResult BuildJobSystemPrompt(
        JobDefinition job,
        string jobContext,
        IReadOnlyList<PromptContextDocument> contextDocuments,
        int promptCharBudget)
    {
        var triggeredAt = DateTimeOffset.Now;
        var builder = new PromptBuilder(PromptProfiles.Automation(_options.SystemPrompt))
            .WithCharacterBudget(promptCharBudget)
            .AddBody($"Triggered at: {triggeredAt:yyyy-MM-dd HH:mm:ss zzz} ({triggeredAt.DayOfWeek}).")
            .AddSection("Runtime Environment", RuntimeEnvironmentContext.BuildLines(_workspaceService.RootPath))
            .AddSection(
                "Job Definition",
                [
                    $"Id: {job.Id}",
                    !string.IsNullOrWhiteSpace(job.Name) ? $"Name: {job.Name}" : string.Empty,
                    $"Type: {job.Type}",
                ])
            .AddSection("Prompt", job.Prompt.Trim());

        if (!string.IsNullOrWhiteSpace(jobContext))
        {
            builder.AddSection("<job_context>", jobContext);
        }

        builder
            .AddSection(
                "Memory Boundary",
                [
                    "Treat only the loaded context files below as available memory for this run.",
                    "Do not infer or recall workspace/MEMORY.md unless it was explicitly loaded.",
                    "If required context is missing, state that gap instead of pretending the job remembers it.",
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

        return builder.AddContextDocuments(contextDocuments).Build();
    }

    private async Task<string> ResolveConfiguredModelForJobAsync(CancellationToken cancellationToken)
    {
        return await RuntimeProviderSelector.ResolveModelOrFallbackAsync(
            _runtimeConfigurationResolver,
            _options.Model,
            _accountRepository,
            cancellationToken);
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

    private IReadOnlyList<string> BuildFollowUpTools(ChannelThreadType threadType)
    {
        var tools = new List<string>(_options.Tools)
            .Where(static tool => !string.Equals(tool, "channel_send", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(tool, "channel_list", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (threadType == ChannelThreadType.DirectMessage)
        {
            var dmTools = new HashSet<string>(tools, StringComparer.OrdinalIgnoreCase);
            if (dmTools.Add("workspace_protocol_update")) tools.Add("workspace_protocol_update");
            if (dmTools.Add("workspace_memory_append")) tools.Add("workspace_memory_append");
        }

        return tools;
    }

    private static string BuildFollowUpSystemPrompt(ChannelThreadType threadType)
    {
        var visibility = threadType == ChannelThreadType.DirectMessage
            ? "This follow-up came from a trusted direct-message channel."
            : "This follow-up came from a public or multi-participant group channel. Keep replies public-safe and do not reveal private context unless it was already included in the automation result.";

        return $"""
You are KodaClaw continuing a completed automation session because a user replied to the automation result message.
Preserve the existing automation context and answer the follow-up question.
{visibility}
Do not call channel_send or channel_list; the host will deliver your final answer to the channel.
""";
    }

    private static string BuildFollowUpPrompt(AutomationFollowUpRequest request)
    {
        var sender = request.Envelope.Sender?.DisplayName
            ?? request.Envelope.Sender?.Username
            ?? request.Envelope.Sender?.Id
            ?? "(unknown)";
        return $$"""
Channel follow-up on automation result.

AutomationId: {{request.Link.AutomationId}}
RunId: {{request.Link.RunId}}
BindingId: {{request.Link.BindingId}}
Connector: {{request.Link.ConnectorKind}}
ThreadType: {{request.Binding.ThreadType}}
OriginalNotificationMessageId: {{request.Link.ExternalMessageId}}
IncomingMessageId: {{request.Envelope.ExternalMessageId ?? "(none)"}}
ReplyToMessageId: {{request.Envelope.ReplyToExternalMessageId ?? "(none)"}}
Sender: {{sender}}

User message:
{{request.Text}}
""";
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
                    ThresholdBytes = _options.ToolResultThresholdBytes,
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
            .AddSection("Prompt", definition.Prompt.Trim());

        if (!string.IsNullOrWhiteSpace(_options.JobContext))
        {
            builder.AddSection("<job_context>", _options.JobContext);
        }

        builder
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
