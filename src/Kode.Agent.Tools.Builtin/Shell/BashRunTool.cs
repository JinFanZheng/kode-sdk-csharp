using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace Kode.Agent.Tools.Builtin.Shell;

/// <summary>
/// Tool for executing shell commands.
/// </summary>
[Tool("bash_run")]
public sealed class BashRunTool : ToolBase<BashRunArgs>
{
    public override string Name => "bash_run";

    public override string Description =>
        "Execute a shell command in the sandbox environment. " +
        "Returns stdout, stderr, and exit code.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<BashRunArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = true
    };

    public override ValueTask<string?> GetPromptAsync(ToolContext context)
    {
        return ValueTask.FromResult<string?>(
            "Tool choice:\n" +
            "- File search → fs_glob (NOT `find` / `ls`)\n" +
            "- Content search → fs_grep (NOT `grep` / `rg`)\n" +
            "- Read files → fs_read (NOT `cat` / `head` / `tail`)\n" +
            "- Edit files → fs_edit / fs_multi_edit (NOT `sed` / `awk`)\n\n" +
            "Reserve bash_run for genuine shell operations: build/test/package managers, git, one-off scripts. " +
            "bash_run usually requires approval — each call is expensive for the user.\n\n" +
            "Long-running commands:\n" +
            "- Set `background: true` to get a processId back immediately\n" +
            "- Use bash_logs to read stdout/stderr while it runs\n" +
            "- Use bash_kill to terminate when no longer needed\n\n" +
            "Always inspect `exitCode` — non-zero means failure even if stdout looks fine.");
    }

    protected override async Task<ToolResult> ExecuteAsync(
        BashRunArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = new CommandOptions
            {
                WorkingDirectory = args.WorkingDirectory,
                Timeout = args.TimeoutSeconds.HasValue
                    ? TimeSpan.FromSeconds(args.TimeoutSeconds.Value)
                    : null,
                Background = args.Background
            };

            var result = await context.Sandbox.ExecuteCommandAsync(args.Command, options, cancellationToken);

            if (args.Background)
            {
                return ToolResult.Ok(new
                {
                    processId = result.ProcessId,
                    background = true,
                    message = "Command started in background"
                });
            }

            return ToolResult.Ok(new
            {
                exitCode = result.ExitCode,
                stdout = result.Stdout,
                stderr = result.Stderr,
                success = result.Success
            });
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("Command timed out");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to execute command: {ex.Message}");
        }
    }
}

/// <summary>
/// Arguments for bash_run tool.
/// </summary>
[GenerateToolSchema]
public class BashRunArgs
{
    /// <summary>
    /// The command to execute.
    /// </summary>
    [ToolParameter(Description = "The shell command to execute")]
    public required string Command { get; init; }

    /// <summary>
    /// Optional working directory.
    /// </summary>
    [ToolParameter(Description = "Working directory for the command", Required = false)]
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Optional timeout in seconds.
    /// </summary>
    [ToolParameter(Description = "Timeout in seconds (default: 300)", Required = false)]
    public int? TimeoutSeconds { get; init; }

    /// <summary>
    /// Whether to run in background.
    /// </summary>
    [ToolParameter(Description = "Run command in background", Required = false)]
    public bool Background { get; init; }
}
