using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Todo;
using Kode.Agent.Sdk.Tools;

namespace Kode.Agent.Tools.Builtin.Todo;

/// <summary>
/// Tool for writing/updating the todo list.
/// </summary>
[Tool("todo_write")]
public sealed class TodoWriteTool : ToolBase<TodoWriteArgs>
{
    public override string Name => "todo_write";

    public override string Description =>
        "Write or update the todo list. Replaces the entire todo list with the provided items. " +
        "Only one todo can be 'in_progress' at a time.";

    public override object InputSchema => JsonSchemaBuilder.BuildSchema<TodoWriteArgs>();

    public override ToolAttributes Attributes => new()
    {
        ReadOnly = false,
        RequiresApproval = false
    };

    public override ValueTask<string?> GetPromptAsync(ToolContext context)
    {
        return ValueTask.FromResult<string?>(
            "When to use todos:\n" +
            "- Tasks with 3+ distinct steps, or when the user lists multiple items\n" +
            "- Work you'll revisit across multiple responses\n" +
            "- Skip for single-step or purely conversational tasks\n\n" +
            "Rules:\n" +
            "- The call REPLACES the list — always send the complete set, not a diff\n" +
            "- Exactly one todo may be `InProgress` at a time\n" +
            "- Mark `InProgress` BEFORE starting work; mark `Completed` immediately after finishing " +
            "(don't batch completions)\n" +
            "- Status values: `Pending` | `InProgress` | `Completed`\n" +
            "- Each todo needs a non-empty `id` and `title`\n\n" +
            "If work is blocked, keep it `InProgress` and add a new todo describing the blocker — " +
            "don't mark it `Completed`.");
    }

    protected override async Task<ToolResult> ExecuteAsync(
        TodoWriteArgs args,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            // Validate: all todos must have non-empty id and title
            var invalid = args.Todos.Where(t => string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Title)).ToList();
            if (invalid.Count > 0)
            {
                return ToolResult.Fail($"Each todo must have a non-empty 'id' and 'title'. {invalid.Count} item(s) are missing required fields.");
            }

            // Validate: only one in_progress allowed
            var inProgressCount = args.Todos.Count(t => t.Status == TodoStatus.InProgress);
            if (inProgressCount > 1)
            {
                return ToolResult.Fail(
                    $"Only one todo can be 'in_progress' at a time. Found {inProgressCount} in_progress todos.");
            }

            // Convert TodoInput to TodoItem (add timestamps)
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var todoItems = args.Todos.Select(t => new TodoItem
            {
                Id = t.Id,
                Title = t.Title,
                Status = t.Status,
                Assignee = t.Assignee,
                Notes = t.Notes,
                CreatedAt = now,
                UpdatedAt = now
            }).ToList();

            // Set todos on the agent
            if (context.Agent is { } agent)
            {
                await agent.SetTodosAsync(todoItems, cancellationToken);
                return ToolResult.Ok(new { ok = true, count = todoItems.Count });
            }

            return ToolResult.Fail("Todo service not enabled for this agent");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Failed to write todos: {ex.Message}");
        }
    }
}

/// <summary>
/// Arguments for todo_write tool.
/// </summary>
[GenerateToolSchema]
public class TodoWriteArgs
{
    /// <summary>
    /// The complete list of todo items.
    /// </summary>
    [ToolParameter(Description = "Array of todo items to set. Each todo must have an id and title. Status can be 'Pending', 'InProgress', or 'Completed'.")]
    public required IReadOnlyList<TodoInput> Todos { get; init; }
}
