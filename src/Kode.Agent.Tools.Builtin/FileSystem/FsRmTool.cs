using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace Kode.Agent.Tools.Builtin.FileSystem;

/// <summary>
/// Tool for removing files or directories.
/// </summary>
[Tool("fs_rm")]
public sealed class FsRmTool : ToolBase<FsRmArgs>
{
    public override string Name => "fs_rm";

    public override string Description =>
        "Remove a file or directory. Use recursive=true for directories with contents.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<FsRmArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = true
    };

    public override ValueTask<string?> GetPromptAsync(ToolContext context)
    {
        return ValueTask.FromResult<string?>(
            "Deletion is NOT recoverable — there's no trash bin. Before calling fs_rm:\n" +
            "1. Confirm the exact path with fs_list or fs_glob. A wrong path silently wipes the wrong files.\n" +
            "2. Set `recursive: true` only when you intentionally want to remove a non-empty directory.\n" +
            "3. Never use fs_rm to 'clean up' unfamiliar files — investigate first; they may be the user's work.\n\n" +
            "This tool requires approval; the user sees the path before it runs.");
    }

    protected override async Task<ToolResult> ExecuteAsync(
        FsRmArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var isFile = await context.Sandbox.FileExistsAsync(args.Path, cancellationToken);
            var isDir = await context.Sandbox.DirectoryExistsAsync(args.Path, cancellationToken);

            if (!isFile && !isDir)
            {
                if (args.Force)
                {
                    return ToolResult.Ok(new { path = args.Path, removed = false, message = "Path does not exist" });
                }
                return ToolResult.Fail($"Path not found: {args.Path}");
            }

            if (isDir)
            {
                await context.Sandbox.DeleteDirectoryAsync(args.Path, args.Recursive, cancellationToken);
            }
            else
            {
                await context.Sandbox.DeleteFileAsync(args.Path, cancellationToken);
            }

            var filePool = ToolContextFilePool.TryGetFilePool(context);
            filePool?.RecordDelete(args.Path);

            return ToolResult.Ok(new
            {
                path = args.Path,
                removed = true,
                type = isDir ? "directory" : "file"
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to remove: {ex.Message}");
        }
    }
}

/// <summary>
/// Arguments for fs_rm tool.
/// </summary>
[GenerateToolSchema]
public class FsRmArgs
{
    /// <summary>
    /// The path to remove.
    /// </summary>
    [ToolParameter(Description = "The path to the file or directory to remove")]
    public required string Path { get; init; }

    /// <summary>
    /// Remove directories recursively.
    /// </summary>
    [ToolParameter(Description = "Remove directories and their contents recursively", Required = false)]
    public bool Recursive { get; init; }

    /// <summary>
    /// Ignore if path doesn't exist.
    /// </summary>
    [ToolParameter(Description = "Do not error if path does not exist", Required = false)]
    public bool Force { get; init; }
}
