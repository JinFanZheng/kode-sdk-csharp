using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;
using Kode.Agent.WebApiAssistant.Tools.Time;

namespace Kode.Agent.WebApiAssistant.Tools.Agent;

/// <summary>
/// Tool for getting current time information.
/// </summary>
[Tool("time_now")]
public sealed class TimeNowTool : ToolBase<TimeNowArgs>
{
    private readonly TimeTool _timeTool;

    public TimeNowTool(TimeTool timeTool)
    {
        _timeTool = timeTool;
    }

    public override string Name => "time_now";

    public override string Description =>
        "Get the current time and date information. " +
        "Returns current time, date, and timestamp.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<TimeNowArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = true,
        NoEffect = true
    };

    protected override Task<ToolResult> ExecuteAsync(
        TimeNowArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(ToolResult.Ok(new
        {
            currentTime = _timeTool.GetCurrentTime(),
            currentDate = _timeTool.GetCurrentDate(),
            timestamp = _timeTool.GetTimestamp(),
            timezone = "Asia/Shanghai"
        }));
    }
}

/// <summary>
/// Arguments for time_now tool.
/// </summary>
[GenerateToolSchema]
public class TimeNowArgs
{
    // No parameters required
}
