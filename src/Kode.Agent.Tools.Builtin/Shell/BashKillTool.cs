using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace Kode.Agent.Tools.Builtin.Shell;

/// <summary>
/// Tool for killing background shell processes.
/// </summary>
[Tool("bash_kill")]
public sealed class BashKillTool : ToolBase<BashKillArgs>
{
    public override string Name => "bash_kill";

    public override string Description =>
        "Kill a background shell process by its process ID. " +
        "Use this to terminate long-running or stuck processes.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<BashKillArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = false
    };

    public override ValueTask<string?> GetPromptAsync(ToolContext context)
    {
        return ValueTask.FromResult<string?>(
            "Terminate a background process started by bash_run with `background: true`. " +
            "Pass the `processId` returned from that call.\n\n" +
            "Before killing, consider bash_logs — the process may have already finished or emitted what you need. " +
            "Killing is final: there's no resume.");
    }

    protected override async Task<ToolResult> ExecuteAsync(
        BashKillArgs args,
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

            var killed = await context.Sandbox.KillProcessAsync(args.ProcessId, cancellationToken);

            if (killed)
            {
                return ToolResult.Ok(new
                {
                    processId = args.ProcessId,
                    message = $"Process {args.ProcessId} killed successfully",
                    wasRunning = processInfo.IsRunning
                });
            }

            return ToolResult.Fail($"Failed to kill process {args.ProcessId}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to kill process: {ex.Message}");
        }
    }
}

/// <summary>
/// Arguments for bash_kill tool.
/// </summary>
[GenerateToolSchema]
public class BashKillArgs
{
    /// <summary>
    /// The process ID to kill.
    /// </summary>
    [ToolParameter(Description = "The process ID from bash_run to kill")]
    public required int ProcessId { get; init; }
}
