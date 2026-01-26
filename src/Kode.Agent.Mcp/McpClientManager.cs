using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Kode.Agent.Mcp;

/// <summary>
/// Manages MCP client connections to multiple servers.
/// </summary>
public sealed class McpClientManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, McpClient> _clients = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _reconnectLocks = new();
    private readonly ILogger<McpClientManager>? _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="McpClientManager"/> class.
    /// </summary>
    public McpClientManager(ILogger<McpClientManager>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Connects to an MCP server.
    /// </summary>
    /// <param name="serverName">The name of the server (for namespacing).</param>
    /// <param name="config">The MCP configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The connected MCP client.</returns>
    public async Task<McpClient> ConnectAsync(
        string serverName,
        McpConfig config,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Return existing client if already connected
        if (_clients.TryGetValue(serverName, out var existingClient))
        {
            return existingClient;
        }

        _logger?.LogDebug("Connecting to MCP server: {ServerName}", serverName);

        McpClient client;
        
        switch (config.Transport)
        {
            case McpTransportType.Stdio:
                if (string.IsNullOrEmpty(config.Command))
                {
                    throw new ArgumentException("Command is required for stdio transport", nameof(config));
                }

                var stdioTransport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = serverName,
                    Command = config.Command,
                    Arguments = config.Args?.ToList() ?? [],
                    EnvironmentVariables = config.Environment?.ToDictionary(
                        kvp => kvp.Key,
                        kvp => (string?)kvp.Value) ?? []
                });

                client = await McpClient.CreateAsync(stdioTransport, cancellationToken: cancellationToken);
                break;

            case McpTransportType.Http:
            case McpTransportType.StreamableHttp:
            case McpTransportType.Sse:
                if (string.IsNullOrEmpty(config.Url))
                {
                    throw new ArgumentException("URL is required for HTTP/StreamableHttp/SSE transport", nameof(config));
                }

                // Build additional headers dictionary
                var additionalHeaders = new Dictionary<string, string>();
                if (config.Headers != null)
                {
                    foreach (var header in config.Headers)
                    {
                        additionalHeaders[header.Key] = header.Value;
                    }
                }

                var httpTransport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Name = serverName,
                    Endpoint = new Uri(config.Url),
                    AdditionalHeaders = additionalHeaders
                });

                client = await McpClient.CreateAsync(httpTransport, cancellationToken: cancellationToken);
                break;

            default:
                throw new ArgumentException($"Unsupported transport type: {config.Transport}", nameof(config));
        }

        if (!_clients.TryAdd(serverName, client))
        {
            // Another thread connected first, dispose our client and use the existing one
            await client.DisposeAsync();
            return _clients[serverName];
        }

        _logger?.LogInformation("Connected to MCP server: {ServerName}", serverName);
        return client;
    }

    /// <summary>
    /// Gets an existing client or reconnects if the connection is stale.
    /// For HTTP-based transports, this ensures a fresh connection is available.
    /// </summary>
    /// <param name="serverName">The name of the server.</param>
    /// <param name="config">The MCP configuration (used if reconnection is needed).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A connected MCP client.</returns>
    public async Task<McpClient> GetOrReconnectAsync(
        string serverName,
        McpConfig config,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // For now, just return existing client if available
        // The actual reconnection happens on 404 errors in the tool executor
        if (_clients.TryGetValue(serverName, out var existingClient))
        {
            return existingClient;
        }

        // No existing client, connect new one
        return await ConnectAsync(serverName, config, cancellationToken);
    }

    /// <summary>
    /// Forces a reconnection to an MCP server, disposing the old connection first.
    /// Uses a lock to prevent concurrent reconnection attempts.
    /// </summary>
    /// <param name="serverName">The name of the server.</param>
    /// <param name="config">The MCP configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A newly connected MCP client.</returns>
    public async Task<McpClient> ReconnectAsync(
        string serverName,
        McpConfig config,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Get or create a lock for this server to prevent concurrent reconnections
        var reconnectLock = _reconnectLocks.GetOrAdd(serverName, _ => new SemaphoreSlim(1, 1));

        await reconnectLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check: another thread may have already reconnected
            if (_clients.TryGetValue(serverName, out var existingClient))
            {
                // Client was reconnected by another thread, return it
                _logger?.LogDebug("MCP server already reconnected by another thread: {ServerName}", serverName);
                return existingClient;
            }

            _logger?.LogInformation("Reconnecting to MCP server: {ServerName}", serverName);

            // Remove and dispose old client (if still present)
            if (_clients.TryRemove(serverName, out var oldClient))
            {
                try
                {
                    await oldClient.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Error disposing old MCP client during reconnect: {ServerName}", serverName);
                }
            }

            // Connect fresh client
            return await ConnectAsync(serverName, config, cancellationToken);
        }
        finally
        {
            reconnectLock.Release();
        }
    }

    /// <summary>
    /// Disconnects from an MCP server.
    /// </summary>
    /// <param name="serverName">The name of the server.</param>
    public async Task DisconnectAsync(string serverName)
    {
        if (_clients.TryRemove(serverName, out var client))
        {
            _logger?.LogDebug("Disconnecting from MCP server: {ServerName}", serverName);
            try
            {
                await client.DisposeAsync();
                _logger?.LogInformation("Disconnected from MCP server: {ServerName}", serverName);
            }
            catch (Exception ex) when (IsExpectedDisconnectError(ex))
            {
                // Common case during host shutdown:
                // stdio-based MCP servers may receive SIGINT/SIGTERM and exit with code 130/143.
                // Disposal should not crash the entire application in that case.
                _logger?.LogDebug(ex, "MCP server already stopped during disconnect: {ServerName}", serverName);
            }
            catch (Exception ex)
            {
                // Best-effort: never fail shutdown because one MCP server exited unexpectedly.
                _logger?.LogWarning(ex, "Failed to disconnect MCP server: {ServerName}", serverName);
            }
        }
    }

    /// <summary>
    /// Disconnects from all MCP servers.
    /// </summary>
    public async Task DisconnectAllAsync()
    {
        var serverNames = _clients.Keys.ToList();
        foreach (var serverName in serverNames)
        {
            await DisconnectAsync(serverName);
        }
    }

    /// <summary>
    /// Gets a connected client by server name.
    /// </summary>
    /// <param name="serverName">The name of the server.</param>
    /// <returns>The client if connected; otherwise null.</returns>
    public McpClient? GetClient(string serverName)
    {
        return _clients.TryGetValue(serverName, out var client) ? client : null;
    }

    /// <summary>
    /// Gets all connected server names.
    /// </summary>
    public IReadOnlyList<string> ConnectedServers => _clients.Keys.ToList();

    /// <summary>
    /// Disposes all client connections.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        
        await DisconnectAllAsync();
    }

    private static bool IsExpectedDisconnectError(Exception ex)
    {
        // OperationCanceledException: shutdown / cancellation path.
        if (ex is OperationCanceledException) return true;

        // ModelContextProtocol may wrap process termination as IOException with message containing exit code.
        if (ex is IOException io)
        {
            var msg = io.Message ?? "";
            return msg.Contains("exit code: 130", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("exit code: 143", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("broken pipe", StringComparison.OrdinalIgnoreCase) ||
                   msg.Contains("EPIPE", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
