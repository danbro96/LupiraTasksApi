using System.ComponentModel;
using LupiraTasksApi.Domain.Items;
using ModelContextProtocol;

namespace LupiraTasksApi.Mcp;

/// <summary>One node of a nested batch — the shape the app's list export/import round-trips.</summary>
public sealed class TaskNode
{
    [Description("Task title.")]
    public required string Title { get; set; }

    [Description("Subtasks of this task (optional). Each has the same shape, so trees nest to any allowed depth.")]
    public IReadOnlyList<TaskNode>? Subtasks { get; set; }
}

/// <summary>A batch node resolved to the item it will create: its id, parent, title, and sort key.</summary>
public sealed record PlannedTask(Guid Id, Guid? ParentId, string Title, string SortOrder, int Level);

/// <summary>
/// Flattens a nested <see cref="TaskNode"/> tree into the ordered creates that build it. Pure (no
/// I/O) so it is unit-tested in the solution's fast test project. Mirrors the app importer's
/// op-builder (<c>ImportListScreen.buildImportOps</c>): a parent is the last id seen one level up,
/// and every task takes the next key of ONE ascending chain — within any sibling group the chain's
/// subsequence is still ascending, so one pass orders the whole tree without per-group bookkeeping.
/// </summary>
public static class TaskTree
{
    /// <summary>Matches the app importer's <c>IMPORT_MAX_TASKS</c>, so a list it can import fits in one call.</summary>
    public const int MaxTasks = 500;

    /// <summary>Nesting levels allowed in one call — a payload guard, not a model limit.</summary>
    public const int MaxDepth = 5;

    /// <summary>
    /// Plan the creates for <paramref name="roots"/>, continuing the key chain after
    /// <paramref name="afterSortOrder"/> (the list's current highest key, or <c>null</c> when empty).
    /// Rejects an empty batch, a blank title, and breaching <see cref="MaxTasks"/>/<see cref="MaxDepth"/>.
    /// </summary>
    public static IReadOnlyList<PlannedTask> Plan(IReadOnlyList<TaskNode>? roots, string? afterSortOrder = null)
    {
        if (roots is null || roots.Count == 0) throw new McpException("`tasks` must contain at least one task.");

        var planned = new List<PlannedTask>();
        var sortOrder = afterSortOrder;

        void Walk(IReadOnlyList<TaskNode> nodes, int level, Guid? parentId)
        {
            if (level >= MaxDepth)
                throw new McpException($"Tasks are nested too deeply — at most {MaxDepth} levels per call.");

            foreach (var node in nodes)
            {
                var title = node.Title?.Trim();
                if (string.IsNullOrEmpty(title)) throw new McpException("Every task needs a non-empty `title`.");
                if (planned.Count == MaxTasks)
                    throw new McpException($"Too many tasks — at most {MaxTasks} per call (split the batch).");

                var id = Guid.CreateVersion7();
                sortOrder = FractionalIndex.KeyAfter(sortOrder);
                planned.Add(new PlannedTask(id, parentId, title, sortOrder, level));

                if (node.Subtasks is { Count: > 0 }) Walk(node.Subtasks, level + 1, id);
            }
        }

        Walk(roots, 0, null);
        return planned;
    }
}
