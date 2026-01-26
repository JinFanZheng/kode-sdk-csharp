using System.Text.Json;
using Kode.Agent.Sdk.Core.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kode.Agent.Mcp;

/// <summary>
/// Result from an MCP tool execution.
/// </summary>
public sealed record McpToolResult
{
    /// <summary>
    /// Gets or sets the content returned by the tool.
    /// </summary>
    public required IReadOnlyList<ContentBlock> Content { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the result is an error.
    /// </summary>
    public bool IsError { get; init; }
}

/// <summary>
/// Provides utilities for working with MCP tools.
/// </summary>
public static class McpToolProvider
{
    /// <summary>
    /// Gets tools from an MCP server and converts them to ITool objects.
    /// </summary>
    /// <param name="manager">The MCP client manager.</param>
    /// <param name="config">The MCP configuration.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of tools from the MCP server.</returns>
    public static async Task<IReadOnlyList<ITool>> GetToolsAsync(
        McpClientManager manager,
        McpConfig config,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var serverName = config.ServerName ?? "default";
        
        // Connect to MCP server
        var client = await manager.ConnectAsync(serverName, config, cancellationToken);
        
        // List available tools
        var mcpTools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        
        // Apply filters
        var filtered = mcpTools.AsEnumerable();
        
        if (config.Include is { Count: > 0 })
        {
            var includeSet = config.Include.ToHashSet(StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(t => includeSet.Contains(t.Name));
        }
        
        if (config.Exclude is { Count: > 0 })
        {
            var excludeSet = config.Exclude.ToHashSet(StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(t => !excludeSet.Contains(t.Name));
        }
        
        // Convert to ITool
        var tools = new List<ITool>();
        
        foreach (var mcpTool in filtered)
        {
            var tool = CreateDynamicTool(manager, serverName, mcpTool, config, logger);
            tools.Add(tool);
            logger?.LogDebug("Registered MCP tool: {ToolName} from {ServerName}", mcpTool.Name, serverName);
        }
        
        logger?.LogInformation("Loaded {Count} tools from MCP server: {ServerName}", tools.Count, serverName);
        
        return tools;
    }

    private static DynamicTool CreateDynamicTool(
        McpClientManager manager,
        string serverName,
        McpClientTool mcpTool,
        McpConfig config,
        ILogger? logger)
    {
        // Generate namespaced tool name: mcp__serverName__toolName
        var toolName = $"mcp__{serverName}__{mcpTool.Name}";
        
        // Convert input schema to object
        var inputSchema = (object)mcpTool.JsonSchema;
        
        return new DynamicTool
        {
            Name = toolName,
            Description = mcpTool.Description ?? $"MCP tool: {mcpTool.Name}",
            InputSchema = inputSchema,
            Executor = async (args, ctx, ct) =>
            {
                try
                {
                    logger?.LogDebug("Calling MCP tool: {ToolName} with args: {Args}", mcpTool.Name, args);
                    
                    // Get fresh client connection (reconnect if needed)
                    var client = await manager.GetOrReconnectAsync(serverName, config, ct);
                    
                    // Parse arguments
                    var arguments = args.Deserialize<Dictionary<string, object?>>();
                    
                    // Call MCP tool
                    var result = await client.CallToolAsync(
                        mcpTool.Name,
                        arguments,
                        cancellationToken: ct);
                    
                    // Convert result
                    var mcpResult = new McpToolResult
                    {
                        Content = result.Content.ToList(),
                        IsError = result.IsError ?? false
                    };
                    
                    logger?.LogDebug("MCP tool {ToolName} returned: IsError={IsError}", mcpTool.Name, mcpResult.IsError);
                    
                    return JsonSerializer.SerializeToElement(mcpResult);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Connection may have expired, try to reconnect once
                    logger?.LogWarning("MCP tool call failed with 404, attempting reconnect: {ToolName}", mcpTool.Name);
                    
                    try
                    {
                        // Force reconnect
                        var client = await manager.ReconnectAsync(serverName, config, ct);
                        var arguments = args.Deserialize<Dictionary<string, object?>>();
                        
                        var result = await client.CallToolAsync(
                            mcpTool.Name,
                            arguments,
                            cancellationToken: ct);
                        
                        var mcpResult = new McpToolResult
                        {
                            Content = result.Content.ToList(),
                            IsError = result.IsError ?? false
                        };
                        
                        logger?.LogInformation("MCP tool call succeeded after reconnect: {ToolName}", mcpTool.Name);
                        return JsonSerializer.SerializeToElement(mcpResult);
                    }
                    catch (Exception retryEx)
                    {
                        logger?.LogError(retryEx, "MCP tool execution failed after reconnect: {ToolName}", mcpTool.Name);
                        throw new InvalidOperationException($"MCP tool execution failed: {ex.Message}", ex);
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "MCP tool execution failed: {ToolName}", mcpTool.Name);
                    throw new InvalidOperationException($"MCP tool execution failed: {ex.Message}", ex);
                }
            },
            Metadata = new Dictionary<string, object?>
            {
                ["source"] = "mcp",
                ["mcpServer"] = serverName,
                ["mcpToolName"] = mcpTool.Name,
                ["transport"] = config.Transport.ToString()
            }
        };
    }
}
