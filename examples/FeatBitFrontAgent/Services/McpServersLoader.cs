using System.Collections.Concurrent;
using Kode.Agent.Mcp;
using Kode.Agent.Sdk.Core.Abstractions;

namespace Kode.Agent.WebApiAssistant.Services;

/// <summary>
/// MCP 服务器配置（从 appsettings.json 读取）
/// </summary>
public class McpServerConfigOptions
{
    public string? Command { get; set; }
    public string[]? Args { get; set; }
    public string? Type { get; set; }
    public string? Transport { get; set; }
    public string? Url { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public Dictionary<string, string>? Env { get; set; }
}

/// <summary>
/// MCP 服务器加载服务
/// 从 appsettings.json 的 McpServers 配置中加载 MCP 工具
/// </summary>
public sealed class McpServersLoader : IAsyncDisposable
{
    private readonly McpClientManager _mcpManager;
    private readonly ILogger<McpServersLoader> _logger;
    private readonly ConcurrentDictionary<string, McpConfig> _serverConfigs = new();
    private bool _disposed;

    public McpServersLoader(
        McpClientManager mcpManager,
        ILogger<McpServersLoader> logger)
    {
        _mcpManager = mcpManager;
        _logger = logger;
    }

    /// <summary>
    /// 从配置加载 MCP 服务器并注册工具
    /// </summary>
    /// <returns>加载的 MCP 工具名列表</returns>
    public async Task<IReadOnlyList<string>> LoadAndRegisterServersAsync(
        IConfiguration configuration,
        IToolRegistry toolRegistry,
        CancellationToken cancellationToken = default)
    {
        var mcpServersSection = configuration.GetSection("McpServers");
        if (!mcpServersSection.Exists())
        {
            _logger.LogInformation("No MCP servers configuration found");
            return Array.Empty<string>();
        }

        var mcpServers = mcpServersSection.GetChildren().ToList();
        _logger.LogInformation("Found {Count} MCP servers in configuration", mcpServers.Count);

        var mcpToolNames = new List<string>();
        foreach (var serverSection in mcpServers)
        {
            var serverName = serverSection.Key;
            var serverConfig = serverSection.Get<McpServerConfigOptions>();

            if (serverConfig == null)
            {
                _logger.LogWarning("Skipping MCP server {ServerName}: invalid configuration", serverName);
                continue;
            }

            try
            {
                var mcpConfig = ConvertToMcpConfig(serverName, serverConfig);
                if (mcpConfig == null)
                {
                    _logger.LogWarning("Skipping MCP server {ServerName}: unsupported transport type", serverName);
                    continue;
                }

                // Store config for later reference
                _serverConfigs.TryAdd(serverName, mcpConfig);

                // Load tools from this server (McpToolProvider logs the tool count)
                var tools = await McpToolProvider.GetToolsAsync(_mcpManager, mcpConfig, _logger, cancellationToken);

                // Register each tool and collect names
                foreach (var tool in tools)
                {
                    toolRegistry.Register(tool);
                    mcpToolNames.Add(tool.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load MCP server {ServerName}", serverName);
            }
        }

        _logger.LogInformation("Total MCP tools loaded: {TotalCount}", mcpToolNames.Count);
        return mcpToolNames;
    }

    /// <summary>
    /// 转换配置为 McpConfig
    /// </summary>
    private static McpConfig? ConvertToMcpConfig(string serverName, McpServerConfigOptions config)
    {
        // 确定传输类型
        var transportRaw = config.Transport ?? config.Type ?? "";
        var transport = transportRaw.ToLowerInvariant() switch
        {
            "stdio" => McpTransportType.Stdio,
            "http" => McpTransportType.Http,
            "streamablehttp" => McpTransportType.StreamableHttp,
            "sse" => McpTransportType.Sse,
            _ => McpTransportType.Stdio // 默认 stdio
        };

        // Stdio transport
        if (transport == McpTransportType.Stdio)
        {
            if (string.IsNullOrEmpty(config.Command))
            {
                return null;
            }

            return new McpConfig
            {
                ServerName = serverName,
                Transport = McpTransportType.Stdio,
                Command = config.Command,
                Args = config.Args,
                Environment = config.Env
            };
        }

        // HTTP/SSE transport
        if (transport is McpTransportType.Http or McpTransportType.StreamableHttp or McpTransportType.Sse)
        {
            if (string.IsNullOrEmpty(config.Url))
            {
                return null;
            }

            return new McpConfig
            {
                ServerName = serverName,
                Transport = transport,
                Url = config.Url,
                Headers = config.Headers
            };
        }

        return null;
    }

    /// <summary>
    /// 断开所有 MCP 服务器连接
    /// </summary>
    public async Task DisconnectAllAsync()
    {
        await _mcpManager.DisconnectAllAsync();
    }

    /// <summary>
    /// 获取已加载的服务器列表
    /// </summary>
    public IReadOnlyList<string> LoadedServers => _serverConfigs.Keys.ToList();

    /// <summary>
    /// 释放资源
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await DisconnectAllAsync();
        await _mcpManager.DisposeAsync();
    }
}
