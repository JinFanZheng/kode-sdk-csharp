using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace Kode.Agent.Tools.Builtin.Shell;

/// <summary>
/// Tool for viewing logs from background shell processes.
/// </summary>
[Tool("bash_logs")]
public sealed class BashLogsTool : ToolBase<BashLogsArgs>
{
    public override string Name => "bash_logs";

    public override string Description =>
        "View the output logs (stdout/stderr) from a background shell process. " +
        "Use this to check the status and output of running or completed background commands.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<BashLogsArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = true,
        RequiresApproval = false
    };

    public override ValueTask<string?> GetPromptAsync(ToolContext context)
    {
        return ValueTask.FromResult<string?>(
            "Read stdout/stderr from a background bash_run process. Pass the `processId` from the original call.\n\n" +
            "Use cases:\n" +
            "- Monitor progress of a long-running build/test\n" +
            "- Retrieve results after the process completes\n" +
            "- Decide whether to bash_kill a stuck process\n\n" +
            "Poll sparingly — each call is a round trip. If you need streaming updates, let the process finish and read once.");
    }

    protected override async Task<ToolResult> ExecuteAsync(
        BashLogsArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var processInfo = await context.Sandbox.GetProcessAsync(args.ProcessId, cancellationToken);

            if (processInfo == null)
            {
                return ToolResult.Fail($"Process not found: {args.ProcessId}");
            }

            var status = processInfo.IsRunning
                ? "running"
                : $"completed (exit code {processInfo.ExitCode})";

            var output = new[] { processInfo.Stdout ?? "", processInfo.Stderr ?? "" }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Aggregate("", (a, b) => string.IsNullOrEmpty(a) ? b! : $"{a}\n{b}")
                .Trim();

            return ToolResult.Ok(new
            {
                processId = args.ProcessId,
                status,
                running = processInfo.IsRunning,
                exitCode = processInfo.ExitCode,
                output = string.IsNullOrEmpty(output) ? "(no output yet)" : output,
                startedAt = processInfo.StartedAt,
                endedAt = processInfo.EndedAt
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to get process logs: {ex.Message}");
        }
    }
}

/// <summary>
/// Arguments for bash_logs tool.
/// </summary>
[GenerateToolSchema]
public class BashLogsArgs
{
    /// <summary>
    /// The process ID to get logs for.
    /// </summary>
    [ToolParameter(Description = "The process ID from bash_run to get logs for")]
    public required int ProcessId { get; init; }
}
