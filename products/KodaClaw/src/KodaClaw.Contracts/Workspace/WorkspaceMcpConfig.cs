using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Workspace;

/// <summary>
/// Represents the workspace/mcp.json file — a user-level MCP server declaration
/// compatible with the Claude Desktop mcpServers format.
/// </summary>
public sealed class WorkspaceMcpConfig
{
    [JsonPropertyName("mcpServers")]
    public IReadOnlyDictionary<string, WorkspaceMcpServerEntry> McpServers { get; init; }
        = new Dictionary<string, WorkspaceMcpServerEntry>();
}

/// <summary>
/// A single MCP server entry in mcp.json.
/// </summary>
public sealed class WorkspaceMcpServerEntry
{
    /// <summary>
    /// Transport type. Defaults to "stdio" if omitted.
    /// Accepted values: "stdio", "http", "streamableHttp", "sse".
    /// </summary>
    [JsonPropertyName("transport")]
    public string? Transport { get; init; }

    /// <summary>
    /// Executable command for stdio transport (e.g. "npx", "python").
    /// </summary>
    [JsonPropertyName("command")]
    public string? Command { get; init; }

    /// <summary>
    /// Arguments for the command (e.g. ["-y", "my-mcp-server@latest"]).
    /// </summary>
    [JsonPropertyName("args")]
    public IReadOnlyList<string>? Args { get; init; }

    /// <summary>
    /// Environment variables injected into the subprocess.
    /// </summary>
    [JsonPropertyName("env")]
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>
    /// URL for http / streamableHttp / sse transport.
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>
    /// HTTP headers for http / streamableHttp / sse transport.
    /// </summary>
    [JsonPropertyName("headers")]
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Whether this server is enabled. Defaults to true when omitted (backward-compatible
    /// with the Claude Desktop mcp.json format which does not include this field).
    /// Set to false to skip this server without removing it from the config.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    /// <summary>
    /// Limits this MCP server to specific session types.
    /// Accepted values: "main", "dm", "group", "automation".
    /// null or absent = all session types (backward-compatible).
    /// Empty array is treated as "all".
    /// </summary>
    [JsonPropertyName("sessionScopes")]
    public IReadOnlyList<string>? SessionScopes { get; init; }
}
