using Kode.Agent.Sdk.Core.Abstractions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Sessions;

namespace KodaClaw.McpHub;

public interface IMcpHubService
{
    /// <summary>
    /// Reads workspace/mcp.json, connects to each enabled MCP server, and injects
    /// their tools into the provided <paramref name="toolRegistry"/>.
    /// Single-server failures are isolated; other servers continue normally.
    /// Servers whose sessionScopes do not include <paramref name="sessionKind"/> are skipped.
    /// </summary>
    Task<McpHubInjectionResult> InjectToolsAsync(
        string sessionId,
        SessionKind sessionKind,
        IToolRegistry toolRegistry,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to connect to the named MCP server and call ListTools.
    /// Returns success/failure result without modifying the tool registry.
    /// </summary>
    Task<McpConnectionTestResult> TestConnectionAsync(
        string serverName,
        CancellationToken cancellationToken = default);
}
