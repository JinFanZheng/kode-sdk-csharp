using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace Kode.Agent.Tools.Builtin.FileSystem;

/// <summary>
/// Tool for searching files with glob patterns.
/// </summary>
[Tool("fs_glob")]
[ToolAttributes(ReadOnly = true, NoEffect = true)]
public sealed class FsGlobTool : ToolBase<FsGlobArgs>
{
    public override string Name => "fs_glob";

    public override string Description =>
        "Find files matching a glob pattern. Returns matching file paths (default limit 200). " +
        "Use for discovering files by name/extension; pair with fs_grep when you need to search content.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<FsGlobArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = true,
        NoEffect = true
    };

    public override ValueTask<string?> GetPromptAsync(ToolContext context)
    {
        return ValueTask.FromResult<string?>(
            "Glob patterns:\n" +
            "- `**/*.cs` — all .cs files recursively\n" +
            "- `src/**/*.ts` — all .ts files under src/\n" +
            "- `**/{foo,bar}.md` — foo.md or bar.md anywhere\n" +
            "- `!node_modules/**` — exclusion (if supported by sandbox)\n\n" +
            "Prefer fs_glob over `bash_run ls/find` — it's purpose-built and sandbox-safe. " +
            "When you hit the 200-result cap, tighten the pattern rather than raising maxResults.");
    }

    protected override async Task<ToolResult> ExecuteAsync(
        FsGlobArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var files = await context.Sandbox.GlobAsync(args.Pattern, cancellationToken);

            var limit = args.MaxResults ?? 200;
            var fileList = files.Take(limit).ToList();

            return ToolResult.Ok(new
            {
                pattern = args.Pattern,
                files = fileList,
                count = fileList.Count,
                truncated = files.Count > limit,
                totalMatched = files.Count
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to search files: {ex.Message}");
        }
    }
}

/// <summary>
/// Arguments for fs_glob tool.
/// </summary>
[GenerateToolSchema]
public class FsGlobArgs
{
    /// <summary>
    /// The glob pattern to match.
    /// </summary>
    [ToolParameter(Description = "Glob pattern to match files (e.g., **/*.cs, src/**/*.json)")]
    public required string Pattern { get; init; }

    /// <summary>
    /// Maximum number of results to return. Defaults to 500.
    /// </summary>
    [ToolParameter(Description = "Maximum number of files to return (default: 200)", Required = false)]
    public int? MaxResults { get; init; }
}
