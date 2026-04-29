using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Workspace;
using KodaClaw.McpHub;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

public static partial class GatewayApp
{
    private static void MapMcpServersEndpoints(WebApplication app)
    {
        var mcp = app.MapGroup("/api/mcp-servers");

        mcp.MapGet("/", async (
            HttpContext context,
            IConfiguration configuration,
            IWorkspaceService workspaceService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var config = await workspaceService.ReadMcpConfigAsync(cancellationToken);
            return Results.Ok(config);
        });

        mcp.MapPut("/", async (
            HttpContext context,
            IConfiguration configuration,
            IWorkspaceService workspaceService,
            IDiagnosticsService diagnosticsService,
            WorkspaceMcpConfig body,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            await workspaceService.SaveMcpConfigAsync(body, cancellationToken);
            return Results.Ok(body);
        });

        mcp.MapPost("/{name}/test-connection", async (
            HttpContext context,
            string name,
            IConfiguration configuration,
            IMcpHubService mcpHubService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var result = await mcpHubService.TestConnectionAsync(name, cancellationToken);
            return Results.Ok(result);
        });
    }
}
