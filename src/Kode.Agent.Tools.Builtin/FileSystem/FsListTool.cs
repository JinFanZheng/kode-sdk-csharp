using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace Kode.Agent.Tools.Builtin.FileSystem;

/// <summary>
/// Tool for listing directory contents.
/// </summary>
[Tool("fs_list")]
[ToolAttributes(ReadOnly = true, NoEffect = true)]
public sealed class FsListTool : ToolBase<FsListArgs>
{
    public override string Name => "fs_list";

    public override string Description =>
        "List the contents of a directory. Returns file and directory names with metadata.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<FsListArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = true,
        NoEffect = true
    };

    public override ValueTask<string?> GetPromptAsync(ToolContext context)
    {
        return ValueTask.FromResult<string?>(
            "Use fs_list when you need directory metadata (entry type, size, modified time) " +
            "or a single-level directory scan. " +
            "Prefer fs_glob when you're matching by name pattern across subdirectories — it's more targeted. " +
            "Avoid using fs_list as a prelude to reading everything; narrow with fs_glob + fs_grep first.");
    }

    protected override async Task<ToolResult> ExecuteAsync(
        FsListArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = string.IsNullOrEmpty(args.Path) ? "." : args.Path;

            if (!await context.Sandbox.DirectoryExistsAsync(path, cancellationToken))
            {
                return ToolResult.Fail($"Directory not found: {path}");
            }

            var entries = await context.Sandbox.ListDirectoryAsync(path, cancellationToken);

            // Filter hidden files if needed
            if (!args.IncludeHidden)
            {
                entries = entries.Where(e => !e.Name.StartsWith('.')).ToList();
            }

            var items = entries.Select(e => new
            {
                name = e.Name,
                type = e.IsDirectory ? "directory" : "file",
                size = e.Size,
                modified = e.LastModified?.ToString("o")
            }).ToList();

            return ToolResult.Ok(new
            {
                path,
                count = items.Count,
                entries = items
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to list directory: {ex.Message}");
        }
    }
}

/// <summary>
/// Arguments for fs_list tool.
/// </summary>
[GenerateToolSchema]
public class FsListArgs
{
    /// <summary>
    /// The directory path to list.
    /// </summary>
    [ToolParameter(Description = "The directory path to list (default: current directory)", Required = false)]
    public string? Path { get; init; }

    /// <summary>
    /// Include hidden files.
    /// </summary>
    [ToolParameter(Description = "Include hidden files (starting with .)", Required = false)]
    public bool IncludeHidden { get; init; }
}
