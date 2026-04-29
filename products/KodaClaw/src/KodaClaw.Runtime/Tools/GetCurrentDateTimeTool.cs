using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace KodaClaw.Runtime.Tools;

/// <summary>
/// Tool that returns the current local date and time.
/// Useful when the user asks for the current time during a long-running session.
/// </summary>
public sealed class GetCurrentDateTimeTool : ToolBase<NoArgs>
{
    public override string Name => "get_current_datetime";

    public override string Description =>
        "Returns the current local date and time. " +
        "Use this when the user asks what time or date it is, " +
        "or when you need a precise timestamp during an ongoing session.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<NoArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = true,
        RequiresApproval = false,
    };

    protected override Task<ToolResult> ExecuteAsync(
        NoArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        return Task.FromResult(ToolResult.Ok(new
        {
            datetime = now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            date = now.ToString("yyyy-MM-dd"),
            time = now.ToString("HH:mm:ss"),
            day_of_week = now.DayOfWeek.ToString(),
            unix_ms = now.ToUnixTimeMilliseconds(),
        }));
    }
}

/// <summary>
/// Empty args for tools that take no parameters.
/// </summary>
public sealed class NoArgs { }
