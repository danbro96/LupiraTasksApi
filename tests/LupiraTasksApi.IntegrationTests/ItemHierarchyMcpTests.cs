using LupiraTasksApi.Mcp;
using ModelContextProtocol;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>
/// Task hierarchy over the MCP surface, end to end against Postgres: nesting on create, the parent
/// guards the services deliberately leave to this surface, reparent/promote/reorder, and a nested
/// batch whose read-back reconstructs the tree it was given.
/// </summary>
public sealed class ItemHierarchyMcpTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    private const string Agent = "agent@x.test";

    [Fact]
    public async Task Add_task_nests_under_a_parent_and_the_read_side_exposes_it()
    {
        var (listId, dish, ingredient) = await AsAgent(Agent, async tools =>
        {
            var list = await tools.CreateList("Dinner");
            var parent = await tools.AddTask(list.Id, "Pasta");
            var child = await tools.AddTask(list.Id, "Tomatoes", parentTaskId: parent.Id);
            return (list.Id, parent, child);
        });

        Assert.Null(dish.ParentTaskId);
        Assert.Equal(dish.Id, ingredient.ParentTaskId);

        var all = await AsAgent(Agent, tools => tools.SearchTasks(listId));
        Assert.Equal([dish.Id, ingredient.Id], all.Select(t => t.Id));

        var roots = await AsAgent(Agent, tools => tools.SearchTasks(listId, rootsOnly: true));
        Assert.Equal([dish.Id], roots.Select(t => t.Id));

        var children = await AsAgent(Agent, tools => tools.SearchTasks(listId, parentTaskId: dish.Id));
        Assert.Equal([ingredient.Id], children.Select(t => t.Id));
    }

    [Fact]
    public async Task Add_task_rejects_a_parent_in_another_list()
    {
        var (otherListTask, targetListId) = await AsAgent(Agent, async tools =>
        {
            var other = await tools.CreateList("Other");
            var task = await tools.AddTask(other.Id, "Elsewhere");
            var target = await tools.CreateList("Target");
            return (task, target.Id);
        });

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            AsAgent(Agent, tools => tools.AddTask(targetListId, "Orphan", parentTaskId: otherListTask.Id)));
        Assert.Contains("different list", ex.Message);
    }

    [Fact]
    public async Task Add_task_rejects_a_parent_that_does_not_exist()
    {
        var listId = await AsAgent(Agent, async tools => (await tools.CreateList("Dinner")).Id);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            AsAgent(Agent, tools => tools.AddTask(listId, "Orphan", parentTaskId: Guid.CreateVersion7())));
        Assert.Contains("No task found", ex.Message);
    }

    [Fact]
    public async Task Move_task_rejects_self_parenting_and_cycles()
    {
        var (parent, child, grandchild) = await AsAgent(Agent, async tools =>
        {
            var list = await tools.CreateList("Project");
            var a = await tools.AddTask(list.Id, "Phase");
            var b = await tools.AddTask(list.Id, "Step", parentTaskId: a.Id);
            var c = await tools.AddTask(list.Id, "Detail", parentTaskId: b.Id);
            return (a, b, c);
        });

        var self = await Assert.ThrowsAsync<McpException>(() =>
            AsAgent(Agent, tools => tools.MoveTask(parent.Id, parentTaskId: parent.Id)));
        Assert.Contains("its own parent", self.Message);

        var cycle = await Assert.ThrowsAsync<McpException>(() =>
            AsAgent(Agent, tools => tools.MoveTask(parent.Id, parentTaskId: grandchild.Id)));
        Assert.Contains("cycle", cycle.Message);

        // The subtree is untouched by the rejected moves.
        var tree = await AsAgent(Agent, tools => tools.SearchTasks(parent.ListId));
        Assert.Equal(child.Id, tree.Single(t => t.Id == grandchild.Id).ParentTaskId);
        Assert.Null(tree.Single(t => t.Id == parent.Id).ParentTaskId);
    }

    [Fact]
    public async Task Move_task_reparents_promotes_and_reorders()
    {
        var (listId, first, second, third) = await AsAgent(Agent, async tools =>
        {
            var list = await tools.CreateList("Shopping");
            var a = await tools.AddTask(list.Id, "Bread");
            var b = await tools.AddTask(list.Id, "Milk");
            var c = await tools.AddTask(list.Id, "Cheese");
            return (list.Id, a, b, c);
        });

        // Nest the third under the first, then promote it back to the top level between the other two.
        var nested = await AsAgent(Agent, tools => tools.MoveTask(third.Id, parentTaskId: first.Id));
        Assert.Equal(first.Id, nested.ParentTaskId);

        var promoted = await AsAgent(Agent, tools => tools.MoveTask(third.Id, afterTaskId: first.Id));
        Assert.Null(promoted.ParentTaskId);
        Assert.Equal([first.Id, third.Id, second.Id], (await AsAgent(Agent, tools => tools.SearchTasks(listId))).Select(t => t.Id));

        var moved = await AsAgent(Agent, tools => tools.MoveTask(second.Id, atStart: true));
        Assert.Null(moved.ParentTaskId);
        Assert.Equal([second.Id, first.Id, third.Id], (await AsAgent(Agent, tools => tools.SearchTasks(listId))).Select(t => t.Id));
    }

    [Fact]
    public async Task Move_task_rejects_a_position_sibling_under_a_different_parent()
    {
        var (parent, child, other) = await AsAgent(Agent, async tools =>
        {
            var list = await tools.CreateList("Project");
            var a = await tools.AddTask(list.Id, "Phase");
            var b = await tools.AddTask(list.Id, "Step", parentTaskId: a.Id);
            var c = await tools.AddTask(list.Id, "Unrelated");
            return (a, b, c);
        });

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            AsAgent(Agent, tools => tools.MoveTask(other.Id, parentTaskId: parent.Id, afterTaskId: parent.Id)));
        Assert.Contains("not among the siblings", ex.Message);
        Assert.Equal(parent.Id, child.ParentTaskId);
    }

    [Fact]
    public async Task Batch_round_trips_a_nested_list_in_the_order_given()
    {
        TaskNode Node(string title, params TaskNode[] subtasks) =>
            new() { Title = title, Subtasks = subtasks.Length > 0 ? subtasks : null };

        var input = new[]
        {
            Node("Pasta", Node("Tomatoes"), Node("Basil")),
            Node("Salad", Node("Lettuce"), Node("Feta", Node("Sheep's milk"))),
            Node("Bread"),
        };

        var (listId, created) = await AsAgent(Agent, async tools =>
        {
            var list = await tools.CreateList("Dinner");
            return (list.Id, await tools.AddTasksBatch(list.Id, input));
        });

        Assert.Equal(
            ["Pasta", "Tomatoes", "Basil", "Salad", "Lettuce", "Feta", "Sheep's milk", "Bread"],
            created.Select(t => t.Title));

        // Read back and rebuild the tree the way a client does — group by parent, each group in
        // sortOrder (the read side's ordering), depth first. It must be the input, unchanged.
        var read = await AsAgent(Agent, tools => tools.SearchTasks(listId));
        var rebuilt = Rebuild(read, null);
        Assert.Equal(Flatten(input), rebuilt);
    }

    [Fact]
    public async Task Batch_appends_after_the_list_s_existing_tasks()
    {
        var (listId, existing) = await AsAgent(Agent, async tools =>
        {
            var list = await tools.CreateList("Dinner");
            return (list.Id, await tools.AddTask(list.Id, "Already here"));
        });

        await AsAgent(Agent, tools => tools.AddTasksBatch(listId, [new TaskNode { Title = "Added later" }]));

        var read = await AsAgent(Agent, tools => tools.SearchTasks(listId));
        Assert.Equal(["Already here", "Added later"], read.Select(t => t.Title));
        Assert.Equal(existing.Id, read[0].Id);
    }

    /// <summary>Depth-first titles with their nesting level, rebuilt from the flat read model.</summary>
    private static List<string> Rebuild(IReadOnlyList<TaskTools.TaskSummary> tasks, Guid? parentId, int level = 0)
    {
        var rows = new List<string>();
        foreach (var task in tasks.Where(t => t.ParentTaskId == parentId))
        {
            rows.Add($"{new string(' ', level * 2)}{task.Title}");
            rows.AddRange(Rebuild(tasks, task.Id, level + 1));
        }
        return rows;
    }

    /// <summary>The same depth-first titles+levels straight from the input tree.</summary>
    private static List<string> Flatten(IReadOnlyList<TaskNode> nodes, int level = 0)
    {
        var rows = new List<string>();
        foreach (var node in nodes)
        {
            rows.Add($"{new string(' ', level * 2)}{node.Title}");
            if (node.Subtasks is { Count: > 0 }) rows.AddRange(Flatten(node.Subtasks, level + 1));
        }
        return rows;
    }
}
