using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;

namespace Kode.Agent.Tools.Builtin.Todo;

/// <summary>
/// Tool for reading the current todo list.
/// </summary>
[Tool("todo_read")]
public sealed class TodoReadTool : ToolBase<TodoReadArgs>
{
    public override string Name => "todo_read";

    public override string Description =>
        "Read the current todo list. Returns all todos with their status.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<TodoReadArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = true,
        RequiresApproval = false
    };

    public override ValueTask<string?> GetPromptAsync(ToolContext context)
    {
        return ValueTask.FromResult<string?>(
            "Read the current todo list with status of each item. " +
            "Call at the start of a resumed conversation, when the user asks about progress, " +
            "or before todo_write to avoid clobbering concurrent updates. " +
            "Skip if the task is a single trivial step — todos are overhead for short work.");
    }

    protected override async Task<ToolResult> ExecuteAsync(
        TodoReadArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            // Get todos from the agent
            if (context.Agent is IAgent agent)
            {
                var todos = await agent.GetTodosAsync(cancellationToken);
                return ToolResult.Ok(new { todos });
            }

            return ToolResult.Ok(new
            {
                todos = Array.Empty<object>(),
                note = "Todo service not enabled for this agent"
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to read todos: {ex.Message}");
        }
    }
}

/// <summary>
/// Arguments for todo_read tool (empty, no parameters required).
/// </summary>
[GenerateToolSchema]
public class TodoReadArgs
{
    // No parameters required for reading todos
}
