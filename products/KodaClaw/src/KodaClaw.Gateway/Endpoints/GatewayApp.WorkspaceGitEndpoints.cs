using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Gateway;
using KodaClaw.Workspace;
using KodaClaw.Workspace.Git;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

public static partial class GatewayApp
{
    private static void MapWorkspaceGitEndpoints(WebApplication app)
    {
        var git = app.MapGroup("/api/workspace/git");

        git.MapGet("/log", async (
            HttpContext context,
            int? limit,
            int? skip,
            IConfiguration configuration,
            IWorkspaceGitService gitService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var pageLimit = limit ?? 10;
            var pageSkip = skip ?? 0;
            // Fetch one extra to determine hasMore without a separate count query
            var commits = await gitService.GetRecentCommitsAsync(pageLimit + 1, pageSkip, cancellationToken);
            var hasMore = commits.Count > pageLimit;
            return Results.Ok(new WorkspaceGitLogResponse(
                Commits: hasMore ? commits.Take(pageLimit).ToArray() : commits,
                HasMore: hasMore));
        });

        git.MapGet("/diff/{hash}", async (
            HttpContext context,
            string hash,
            IConfiguration configuration,
            IWorkspaceGitService gitService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var diff = await gitService.GetCommitDiffAsync(hash, cancellationToken);
            return Results.Text(diff, contentType: "text/plain");
        });

        git.MapPost("/revert-file", async (
            HttpContext context,
            WorkspaceGitRevertFileRequest body,
            IConfiguration configuration,
            IWorkspaceGitService gitService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            try
            {
                var newHash = await gitService.RevertFileToCommitAsync(
                    body.Hash, body.FilePath, cancellationToken);
                RecordDiagnosticEvent(diagnosticsService, context, source: "gateway.workspace.git",
                    eventType: "gateway.workspace.git.file_reverted", level: "info",
                    message: $"Workspace file reverted: {body.FilePath} to {body.Hash[..Math.Min(7, body.Hash.Length)]}.",
                    attributes: new Dictionary<string, string?>
                    {
                        ["filePath"] = body.FilePath,
                        ["targetHash"] = body.Hash,
                        ["newHash"] = newHash,
                    });
                return Results.Ok(new WorkspaceGitRevertFileResponse(newHash));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "workspace.git.invalid_path",
                    Message: ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ErrorResponse(
                    Code: "workspace.git.revert_failed",
                    Message: ex.Message));
            }
        });
    }
}
