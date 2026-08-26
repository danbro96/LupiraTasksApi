using System.ComponentModel;

namespace LupiraTasksApi.Mcp;

/// <summary>One node of a nested batch — the shape the app's list export/import round-trips.</summary>
public sealed class TaskNode
{
    [Description("Task title.")]
    public required string Title { get; set; }

    [Description("Subtasks of this task (optional). Each has the same shape, so trees nest to any allowed depth.")]
    public IReadOnlyList<TaskNode>? Subtasks { get; set; }
}
