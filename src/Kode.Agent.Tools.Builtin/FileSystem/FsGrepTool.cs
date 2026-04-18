using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace Kode.Agent.Tools.Builtin.FileSystem;

/// <summary>
/// Tool for searching text in files.
/// </summary>
[Tool("fs_grep")]
[ToolAttributes(ReadOnly = true, NoEffect = true)]
public sealed class FsGrepTool : ToolBase<FsGrepArgs>
{
    public override string Name => "fs_grep";

    public override string Description =>
        "Search for text patterns in files (regex or literal). " +
        "Returns only matching lines with optional context — far more context-efficient than reading whole files. " +
        "Prefer this over fs_read when you're looking for a keyword, function, or pattern.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<FsGrepArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = true,
        NoEffect = true
    };

    public override ValueTask<string?> GetPromptAsync(ToolContext context)
    {
        return ValueTask.FromResult<string?>(
            "Search strategy:\n" +
            "1. Narrow scope with `filePattern` (e.g. \"*.cs\", \"src/**/*.ts\") — searching the whole tree is slow.\n" +
            "2. Start with `isRegex: false` for exact strings; switch to regex only when you need alternation or quantifiers.\n" +
            "3. Default `maxResults` is 50 — only raise it when you actually need more hits. Too many results waste context.\n" +
            "4. Use `contextLines` (1–3) when you need a few surrounding lines to interpret the match.\n" +
            "5. For broad code exploration combine with fs_glob first to confirm which files exist.");
    }

    protected override async Task<ToolResult> ExecuteAsync(
        FsGrepArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = new GrepOptions
            {
                FilePattern = args.FilePattern,
                IsRegex = args.IsRegex,
                CaseSensitive = args.CaseSensitive,
                MaxResults = args.MaxResults ?? 50,
                ContextBefore = args.ContextLines,
                ContextAfter = args.ContextLines
            };

            var results = await context.Sandbox.GrepAsync(args.Pattern, options, cancellationToken);

            return ToolResult.Ok(new
            {
                pattern = args.Pattern,
                results = results.Select(r => new
                {
                    file = r.FilePath,
                    line = r.LineNumber,
                    content = r.Line,
                    contextBefore = r.ContextBefore,
                    contextAfter = r.ContextAfter
                }),
                count = results.Count
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to search: {ex.Message}");
        }
    }
}

/// <summary>
/// Arguments for fs_grep tool.
/// </summary>
[GenerateToolSchema]
public class FsGrepArgs
{
    /// <summary>
    /// The pattern to search for.
    /// </summary>
    [ToolParameter(Description = "The text or regex pattern to search for")]
    public required string Pattern { get; init; }

    /// <summary>
    /// Glob pattern for files to search.
    /// </summary>
    [ToolParameter(Description = "Glob pattern for files to search (default: *)", Required = false)]
    public string? FilePattern { get; init; }

    /// <summary>
    /// Whether the pattern is a regex.
    /// </summary>
    [ToolParameter(Description = "Treat pattern as regex", Required = false)]
    public bool IsRegex { get; init; }

    /// <summary>
    /// Whether the search is case-sensitive.
    /// </summary>
    [ToolParameter(Description = "Case-sensitive search", Required = false)]
    public bool CaseSensitive { get; init; }

    /// <summary>
    /// Maximum number of results.
    /// </summary>
    [ToolParameter(Description = "Maximum number of results (default: 50)", Required = false)]
    public int? MaxResults { get; init; }

    /// <summary>
    /// Number of context lines to include.
    /// </summary>
    [ToolParameter(Description = "Number of context lines before/after match", Required = false)]
    public int? ContextLines { get; init; }
}
