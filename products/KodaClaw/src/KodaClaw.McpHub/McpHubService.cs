using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Sessions;
using KodaClaw.Contracts.Workspace;
using Kode.Agent.Mcp;
using Kode.Agent.Sdk.Core.Abstractions;

namespace KodaClaw.McpHub;

public sealed class McpHubService : IMcpHubService
{
    private const string DiagnosticSource = "koda.mcp_hub";

    private readonly IWorkspaceService _workspaceService;
    private readonly McpClientManager? _mcpClientManager;
    private readonly IDiagnosticsService? _diagnosticsService;
    private readonly ICorrelationContextAccessor? _correlationContextAccessor;

    public McpHubService(
        IWorkspaceService workspaceService,
        McpClientManager? mcpClientManager = null,
        IDiagnosticsService? diagnosticsService = null,
        ICorrelationContextAccessor? correlationContextAccessor = null)
    {
        _workspaceService = workspaceService;
        _mcpClientManager = mcpClientManager;
        _diagnosticsService = diagnosticsService;
        _correlationContextAccessor = correlationContextAccessor;
    }

    public async Task<McpHubInjectionResult> InjectToolsAsync(
        string sessionId,
        SessionKind sessionKind,
        IToolRegistry toolRegistry,
        CancellationToken cancellationToken = default)
    {
        if (_mcpClientManager is null)
        {
            return McpHubInjectionResult.Empty;
        }

        WorkspaceMcpConfig mcpConfig;
        try
        {
            mcpConfig = await _workspaceService.ReadMcpConfigAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TaskCanceledException)
        {
            RecordDiagnosticEvent(
                eventType: "mcp_hub.config.read_failed",
                level: "warning",
                message: ex.GetBaseException().Message,
                sessionId: sessionId);
            return McpHubInjectionResult.Empty;
        }

        if (mcpConfig.McpServers.Count == 0)
        {
            return McpHubInjectionResult.Empty;
        }

        var merged = new HashSet<string>(StringComparer.Ordinal);
        var injectedServerCount = 0;
        var injectedToolCount = 0;
        var failedServers = new List<string>();
        var injectedToolNames = new List<string>();

        foreach (var (serverName, entry) in mcpConfig.McpServers)
        {
            if (string.IsNullOrWhiteSpace(serverName))
            {
                continue;
            }

            if (entry.Enabled == false)
            {
                RecordDiagnosticEvent(
                    eventType: "mcp_hub.server.skipped",
                    level: "info",
                    message: $"MCP server '{serverName}' is disabled (enabled=false), skipping.",
                    sessionId: sessionId,
                    attributes: new Dictionary<string, string?> { ["serverName"] = serverName });
                continue;
            }

            // Session scope filtering
            var effectiveScopes = entry.SessionScopes;
            if (effectiveScopes is { Count: > 0 })
            {
                var sessionScope = sessionKind switch
                {
                    SessionKind.Main => "main",
                    SessionKind.ChannelDirectMessage => "dm",
                    SessionKind.ChannelGroup => "group",
                    SessionKind.Automation => "automation",
                    _ => "all",
                };

                if (!effectiveScopes.Contains("all") &&
                    !effectiveScopes.Contains(sessionScope, StringComparer.OrdinalIgnoreCase))
                {
                    RecordDiagnosticEvent(
                        eventType: "mcp_hub.server.scope_filtered",
                        level: "info",
                        message: $"MCP server '{serverName}' skipped for session kind '{sessionKind}' (scopes: {string.Join(",", effectiveScopes)}).",
                        sessionId: sessionId,
                        attributes: new Dictionary<string, string?>
                        {
                            ["serverName"] = serverName,
                            ["sessionKind"] = sessionKind.ToString(),
                        });
                    continue;
                }
            }

            var config = BuildMcpConfig(serverName, entry);
            if (config is null)
            {
                continue;
            }

            IReadOnlyList<Kode.Agent.Sdk.Core.Abstractions.ITool> serverTools;
            try
            {
                serverTools = await McpToolProvider.GetToolsAsync(
                    _mcpClientManager,
                    config,
                    logger: null,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not TaskCanceledException)
            {
                failedServers.Add(serverName);
                RecordDiagnosticEvent(
                    eventType: "mcp_hub.server.fetch_failed",
                    level: "warning",
                    message: ex.GetBaseException().Message,
                    sessionId: sessionId,
                    attributes: new Dictionary<string, string?> { ["serverName"] = serverName });
                continue;
            }

            var addedForServer = 0;
            foreach (var tool in serverTools)
            {
                if (string.IsNullOrWhiteSpace(tool.Name) || !merged.Add(tool.Name))
                {
                    continue;
                }

                toolRegistry.Register(tool);
                injectedToolNames.Add(tool.Name);
                injectedToolCount++;
                addedForServer++;
            }

            if (addedForServer > 0)
            {
                injectedServerCount++;
                RecordDiagnosticEvent(
                    eventType: "mcp_hub.server.injected",
                    level: "info",
                    message: $"MCP server '{serverName}' injected {addedForServer} tool(s).",
                    sessionId: sessionId,
                    attributes: new Dictionary<string, string?>
                    {
                        ["serverName"] = serverName,
                        ["toolCount"] = addedForServer.ToString(),
                    });
            }
        }

        return new McpHubInjectionResult(
            ServerCount: injectedServerCount,
            ToolCount: injectedToolCount,
            FailedServerCount: failedServers.Count,
            FailedServers: failedServers,
            InjectedToolNames: injectedToolNames);
    }

    public async Task<McpConnectionTestResult> TestConnectionAsync(
        string serverName,
        CancellationToken cancellationToken = default)
    {
        if (_mcpClientManager is null)
        {
            return new McpConnectionTestResult(false, 0, "McpClientManager is not available.");
        }

        WorkspaceMcpConfig mcpConfig;
        try
        {
            mcpConfig = await _workspaceService.ReadMcpConfigAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TaskCanceledException)
        {
            return new McpConnectionTestResult(false, 0, $"Failed to read mcp.json: {ex.GetBaseException().Message}");
        }

        if (!mcpConfig.McpServers.TryGetValue(serverName, out var entry))
        {
            return new McpConnectionTestResult(false, 0, $"Server '{serverName}' not found in mcp.json.");
        }

        var config = BuildMcpConfig(serverName, entry);
        if (config is null)
        {
            return new McpConnectionTestResult(false, 0, $"Server '{serverName}' has an unsupported transport type.");
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var tools = await McpToolProvider.GetToolsAsync(
                _mcpClientManager,
                config,
                logger: null,
                linkedCts.Token);

            var toolNames = tools.Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            return new McpConnectionTestResult(true, tools.Count, null, toolNames);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new McpConnectionTestResult(false, 0, "Connection timed out (10s).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TaskCanceledException)
        {
            return new McpConnectionTestResult(false, 0, ex.GetBaseException().Message);
        }
    }

    private static McpConfig? BuildMcpConfig(string serverName, WorkspaceMcpServerEntry entry)
    {
        var transport = entry.Transport?.Trim().ToLowerInvariant() switch
        {
            null or "" or "stdio" => McpTransportType.Stdio,
            "http" => McpTransportType.Http,
            "streamablehttp" => McpTransportType.StreamableHttp,
            "sse" => McpTransportType.Sse,
            _ => (McpTransportType?)null,
        };

        if (transport is null)
        {
            return null;
        }

        return new McpConfig
        {
            ServerName = serverName,
            Transport = transport.Value,
            Command = entry.Command,
            Args = entry.Args,
            Environment = entry.Env,
            Url = entry.Url,
            Headers = entry.Headers,
        };
    }

    private void RecordDiagnosticEvent(
        string eventType,
        string level,
        string message,
        string? sessionId = null,
        IReadOnlyDictionary<string, string?>? attributes = null)
    {
        if (_diagnosticsService is null)
        {
            return;
        }

        _diagnosticsService.Record(new DiagnosticEvent(
            Id: Guid.NewGuid().ToString("N"),
            Source: DiagnosticSource,
            EventType: eventType,
            Level: level,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow,
            CorrelationId: _correlationContextAccessor?.CorrelationId,
            SessionId: sessionId,
            Attributes: attributes));
    }
}
