using KodaClaw.Contracts;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.Skills;
using KodaClaw.Contracts.Workspace;
using KodaClaw.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

public static partial class GatewayApp
{
    private static void MapSkillsEndpoints(WebApplication app)
    {
        var group = app.MapGroup("/api/skills");

        group.MapGet(string.Empty, async (
            HttpContext context,
            IConfiguration configuration,
            IWorkspaceService workspaceService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var paths = workspaceService.GetSkillsPaths();
            var appDir = Path.Combine(AppContext.BaseDirectory, "skills");
            var globalDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".agents",
                "skills");

            var items = new List<SkillDescriptor>();
            foreach (var searchPath in paths)
            {
                if (!Directory.Exists(searchPath))
                {
                    continue;
                }

                var source = string.Equals(searchPath, appDir, StringComparison.OrdinalIgnoreCase)
                    ? "built-in"
                    : string.Equals(searchPath, globalDir, StringComparison.OrdinalIgnoreCase)
                        ? "global"
                        : "workspace";

                foreach (var skillDir in Directory.GetDirectories(searchPath))
                {
                    var skillFile = Path.Combine(skillDir, "SKILL.md");
                    if (!File.Exists(skillFile))
                    {
                        continue;
                    }

                    var skillName = Path.GetFileName(skillDir);
                    SkillFrontmatterParser.ParsedFrontmatter frontmatter;

                    try
                    {
                        var content = await File.ReadAllTextAsync(skillFile, cancellationToken);
                        frontmatter = SkillFrontmatterParser.Parse(content);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not TaskCanceledException)
                    {
                        _ = ex;
                        frontmatter = new SkillFrontmatterParser.ParsedFrontmatter(null, "optional", null, [], [], null);
                    }

                    var hasResources = Directory.GetFiles(skillDir).Length > 1;

                    items.Add(new SkillDescriptor(
                        Name: skillName,
                        Description: frontmatter.Description,
                        Source: source,
                        Path: skillDir,
                        HasResources: hasResources,
                        Kind: frontmatter.Kind,
                        Tags: frontmatter.Tags,
                        AllowedTools: frontmatter.AllowedTools,
                        Version: frontmatter.Version,
                        Compatibility: frontmatter.Compatibility));
                }
            }

            return Results.Ok(items);
        });
    }
}
