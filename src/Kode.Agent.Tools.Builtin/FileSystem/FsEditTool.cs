using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace Kode.Agent.Tools.Builtin.FileSystem;

/// <summary>
/// Tool for editing file contents by replacing text.
/// </summary>
[Tool("fs_edit")]
public sealed class FsEditTool : ToolBase<FsEditArgs>
{
    public override string Name => "fs_edit";

    public override string Description =>
        "Edit a file by replacing exact text. Use oldString to specify the text to replace " +
        "and newString to specify the replacement text.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<FsEditArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = true
    };

    public override ValueTask<string?> GetPromptAsync(ToolContext context)
    {
        return ValueTask.FromResult<string?>(
            "Edit workflow:\n" +
            "1. Call fs_read first — you must know the current content to craft a matching `oldString`.\n" +
            "2. `oldString` must match the file byte-for-byte (indentation, whitespace, line endings).\n" +
            "3. `oldString` must be unique in the file, or the edit is rejected. " +
            "Include 3+ lines of surrounding context to disambiguate — don't match a bare identifier.\n" +
            "4. Use `replaceAll: true` only for genuine rename-all cases (variable renaming, string replacement). " +
            "Otherwise keep it false so accidental duplicates fail loudly.\n" +
            "5. For several edits to the same file, use fs_multi_edit in one call rather than chaining fs_edit.");
    }

    protected override async Task<ToolResult> ExecuteAsync(
        FsEditArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await context.Sandbox.FileExistsAsync(args.Path, cancellationToken))
            {
                return ToolResult.Fail($"File not found: {args.Path}");
            }

            var content = await context.Sandbox.ReadFileAsync(args.Path, cancellationToken);

            // Count occurrences
            var count = CountOccurrences(content, args.OldString);

            if (count == 0)
            {
                return ToolResult.Fail($"Text not found in file: {args.Path}");
            }

            if (count > 1 && !args.ReplaceAll)
            {
                return ToolResult.Fail(
                    $"Found {count} occurrences of the text. Use replaceAll=true to replace all, " +
                    "or provide more context to uniquely identify the location.");
            }

            // Count check above guarantees that unique-match and replace-all cases are equivalent here.
            var newContent = content.Replace(args.OldString, args.NewString);

            await context.Sandbox.WriteFileAsync(args.Path, newContent, cancellationToken);

            var filePool = ToolContextFilePool.TryGetFilePool(context);
            if (filePool != null)
            {
                await filePool.RecordEditAsync(args.Path, cancellationToken);
            }

            return ToolResult.Ok(new
            {
                path = args.Path,
                replacements = args.ReplaceAll ? count : 1,
                message = $"Successfully replaced text in {args.Path}"
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to edit file: {ex.Message}");
        }
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}

/// <summary>
/// Arguments for fs_edit tool.
/// </summary>
[GenerateToolSchema]
public class FsEditArgs
{
    /// <summary>
    /// The path to the file to edit.
    /// </summary>
    [ToolParameter(Description = "The absolute or relative path to the file to edit")]
    public required string Path { get; init; }

    /// <summary>
    /// The exact text to replace.
    /// </summary>
    [ToolParameter(Description = "The exact text to find and replace")]
    public required string OldString { get; init; }

    /// <summary>
    /// The replacement text.
    /// </summary>
    [ToolParameter(Description = "The text to replace oldString with")]
    public required string NewString { get; init; }

    /// <summary>
    /// Whether to replace all occurrences.
    /// </summary>
    [ToolParameter(Description = "Whether to replace all occurrences (default: false)", Required = false)]
    public bool ReplaceAll { get; init; }
}
