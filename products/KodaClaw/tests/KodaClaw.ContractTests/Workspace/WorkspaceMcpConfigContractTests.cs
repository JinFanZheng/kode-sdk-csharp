using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Workspace;
using System.Text.Json;
using KodaClaw.Contracts.Workspace;
using Xunit;

namespace KodaClaw.ContractTests.Workspace;

public sealed class WorkspaceMcpConfigContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void Stdio_entry_round_trips_correctly()
    {
        const string json = """
            {
              "mcpServers": {
                "my-tool": {
                  "command": "npx",
                  "args": ["-y", "my-mcp-server@latest"],
                  "env": { "API_KEY": "secret123" }
                }
              }
            }
            """;

        var config = JsonSerializer.Deserialize<WorkspaceMcpConfig>(json, JsonOptions)!;

        config.McpServers.Should().ContainKey("my-tool");
        var entry = config.McpServers["my-tool"];
        entry.Command.Should().Be("npx");
        entry.Args.Should().Equal("-y", "my-mcp-server@latest");
        entry.Env.Should().ContainKey("API_KEY").WhoseValue.Should().Be("secret123");
        entry.Transport.Should().BeNull();
        entry.Url.Should().BeNull();
    }

    [Fact]
    public void Http_entry_round_trips_correctly()
    {
        const string json = """
            {
              "mcpServers": {
                "remote-search": {
                  "transport": "streamableHttp",
                  "url": "https://api.example.com/mcp",
                  "headers": { "Authorization": "Bearer token123" }
                }
              }
            }
            """;

        var config = JsonSerializer.Deserialize<WorkspaceMcpConfig>(json, JsonOptions)!;

        config.McpServers.Should().ContainKey("remote-search");
        var entry = config.McpServers["remote-search"];
        entry.Transport.Should().Be("streamableHttp");
        entry.Url.Should().Be("https://api.example.com/mcp");
        entry.Headers.Should().ContainKey("Authorization").WhoseValue.Should().Be("Bearer token123");
        entry.Command.Should().BeNull();
    }

    [Fact]
    public void Multiple_servers_deserialize_correctly()
    {
        const string json = """
            {
              "mcpServers": {
                "server-a": { "command": "node", "args": ["server-a.js"] },
                "server-b": { "transport": "sse", "url": "http://localhost:3001/sse" }
              }
            }
            """;

        var config = JsonSerializer.Deserialize<WorkspaceMcpConfig>(json, JsonOptions)!;

        config.McpServers.Should().HaveCount(2);
        config.McpServers.Should().ContainKey("server-a");
        config.McpServers.Should().ContainKey("server-b");
    }

    [Fact]
    public void Empty_json_object_deserializes_to_empty_servers()
    {
        const string json = "{}";

        var config = JsonSerializer.Deserialize<WorkspaceMcpConfig>(json, JsonOptions)!;

        config.McpServers.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadMcpConfigAsync_returns_empty_when_file_missing()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var service = new WorkspaceService(new KodaClawWorkspaceOptions { RootPath = rootPath });
            var config = await service.ReadMcpConfigAsync();
            config.McpServers.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(rootPath)) Directory.Delete(rootPath, true);
        }
    }

    [Fact]
    public async Task ReadMcpConfigAsync_returns_empty_on_invalid_json()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var service = new WorkspaceService(new KodaClawWorkspaceOptions { RootPath = rootPath });
            await service.EnsureInitializedAsync();
            var mcpPath = Path.Combine(rootPath, "workspace", "mcp.json");
            await File.WriteAllTextAsync(mcpPath, "not valid json {{{{");

            var config = await service.ReadMcpConfigAsync();

            config.McpServers.Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(rootPath)) Directory.Delete(rootPath, true);
        }
    }

    [Fact]
    public void Enabled_false_deserializes_correctly()
    {
        const string json = """
            {
              "mcpServers": {
                "my-tool": {
                  "command": "python3",
                  "args": ["server.py"],
                  "enabled": false
                }
              }
            }
            """;

        var config = JsonSerializer.Deserialize<WorkspaceMcpConfig>(json, JsonOptions)!;

        var entry = config.McpServers["my-tool"];
        entry.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Enabled_true_deserializes_correctly()
    {
        const string json = """
            {
              "mcpServers": {
                "my-tool": { "command": "npx", "enabled": true }
              }
            }
            """;

        var config = JsonSerializer.Deserialize<WorkspaceMcpConfig>(json, JsonOptions)!;

        config.McpServers["my-tool"].Enabled.Should().BeTrue();
    }

    [Fact]
    public void Enabled_omitted_defaults_to_null_for_backward_compat()
    {
        const string json = """
            {
              "mcpServers": {
                "my-tool": { "command": "npx" }
              }
            }
            """;

        var config = JsonSerializer.Deserialize<WorkspaceMcpConfig>(json, JsonOptions)!;

        // null means "not specified" — treated as enabled=true at runtime
        config.McpServers["my-tool"].Enabled.Should().BeNull();
    }

    [Fact]
    public void Enabled_false_entry_serializes_back_with_field()
    {
        var config = new WorkspaceMcpConfig
        {
            McpServers = new Dictionary<string, WorkspaceMcpServerEntry>
            {
                ["disabled-server"] = new WorkspaceMcpServerEntry
                {
                    Command = "npx",
                    Enabled = false,
                }
            }
        };

        var json = JsonSerializer.Serialize(config, JsonOptions);

        json.Should().Contain("\"enabled\":false");
        json.Should().Contain("\"disabled-server\"");
    }

    [Fact]
    public async Task SaveMcpConfigAsync_and_ReadMcpConfigAsync_round_trip()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var service = new WorkspaceService(new KodaClawWorkspaceOptions { RootPath = rootPath });
            await service.EnsureInitializedAsync();

            var config = new WorkspaceMcpConfig
            {
                McpServers = new Dictionary<string, WorkspaceMcpServerEntry>
                {
                    ["saved-server"] = new WorkspaceMcpServerEntry
                    {
                        Command = "npx",
                        Args = ["-y", "saved-server@latest"],
                        Enabled = false,
                    }
                }
            };

            await service.SaveMcpConfigAsync(config);
            var readBack = await service.ReadMcpConfigAsync();

            readBack.McpServers.Should().ContainKey("saved-server");
            readBack.McpServers["saved-server"].Command.Should().Be("npx");
            readBack.McpServers["saved-server"].Args.Should().Equal("-y", "saved-server@latest");
            readBack.McpServers["saved-server"].Enabled.Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(rootPath)) Directory.Delete(rootPath, true);
        }
    }

    [Fact]
    public async Task SaveMcpConfigAsync_overwrites_existing_file()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var service = new WorkspaceService(new KodaClawWorkspaceOptions { RootPath = rootPath });
            await service.EnsureInitializedAsync();

            var original = new WorkspaceMcpConfig
            {
                McpServers = new Dictionary<string, WorkspaceMcpServerEntry>
                {
                    ["old-server"] = new WorkspaceMcpServerEntry { Command = "node" }
                }
            };
            await service.SaveMcpConfigAsync(original);

            var updated = new WorkspaceMcpConfig
            {
                McpServers = new Dictionary<string, WorkspaceMcpServerEntry>
                {
                    ["new-server"] = new WorkspaceMcpServerEntry { Command = "python3" }
                }
            };
            await service.SaveMcpConfigAsync(updated);

            var readBack = await service.ReadMcpConfigAsync();
            readBack.McpServers.Should().ContainKey("new-server");
            readBack.McpServers.Should().NotContainKey("old-server");
        }
        finally
        {
            if (Directory.Exists(rootPath)) Directory.Delete(rootPath, true);
        }
    }

    [Fact]
    public void SessionScopes_array_deserializes_correctly()
    {
        const string json = """
            {
              "mcpServers": {
                "main-only-server": {
                  "command": "npx",
                  "sessionScopes": ["main", "dm"]
                }
              }
            }
            """;

        var config = JsonSerializer.Deserialize<WorkspaceMcpConfig>(json, JsonOptions)!;

        var entry = config.McpServers["main-only-server"];
        entry.SessionScopes.Should().NotBeNull();
        entry.SessionScopes.Should().Equal("main", "dm");
    }

    [Fact]
    public void SessionScopes_null_is_backward_compat()
    {
        const string json = """
            {
              "mcpServers": {
                "legacy-server": {
                  "command": "npx"
                }
              }
            }
            """;

        var config = JsonSerializer.Deserialize<WorkspaceMcpConfig>(json, JsonOptions)!;

        // null means "not specified" — treated as all session types at runtime
        config.McpServers["legacy-server"].SessionScopes.Should().BeNull();
    }

    [Fact]
    public void SessionScopes_with_other_fields_round_trips()
    {
        var entry = new WorkspaceMcpServerEntry
        {
            Command = "npx",
            Args = ["-y", "my-server@latest"],
            Enabled = true,
            SessionScopes = ["main", "automation"],
        };

        var config = new WorkspaceMcpConfig
        {
            McpServers = new Dictionary<string, WorkspaceMcpServerEntry>
            {
                ["scoped-server"] = entry,
            }
        };

        var json = JsonSerializer.Serialize(config, JsonOptions);
        var readBack = JsonSerializer.Deserialize<WorkspaceMcpConfig>(json, JsonOptions)!;

        var readEntry = readBack.McpServers["scoped-server"];
        readEntry.Command.Should().Be("npx");
        readEntry.Enabled.Should().BeTrue();
        readEntry.SessionScopes.Should().Equal("main", "automation");
    }

    [Fact]
    public async Task ReadMcpConfigAsync_returns_parsed_servers()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var service = new WorkspaceService(new KodaClawWorkspaceOptions { RootPath = rootPath });
            await service.EnsureInitializedAsync();

            const string json = """
                {
                  "mcpServers": {
                    "chrome-devtools": {
                      "command": "npx",
                      "args": ["-y", "chrome-devtools-mcp@latest"]
                    }
                  }
                }
                """;
            var mcpPath = Path.Combine(rootPath, "workspace", "mcp.json");
            await File.WriteAllTextAsync(mcpPath, json);

            var config = await service.ReadMcpConfigAsync();

            config.McpServers.Should().ContainKey("chrome-devtools");
            config.McpServers["chrome-devtools"].Command.Should().Be("npx");
        }
        finally
        {
            if (Directory.Exists(rootPath)) Directory.Delete(rootPath, true);
        }
    }
}
