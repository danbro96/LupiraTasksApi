using LupiraTasksApi.Core.Domain.Items;
using LupiraTasksApi.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Xunit;

namespace LupiraTasksApi.UnitTests;

/// <summary>
/// The <c>add_tasks_batch</c> planner: the nested input the app's export/import round-trips flattened
/// into the ordered creates that rebuild it. Pins the two things a batch gets wrong silently — a task
/// hung off the wrong parent, and sort keys that don't render in the order given.
/// </summary>
public class TaskTreeTests
{
    private static TaskNode Node(string title, params TaskNode[] subtasks) =>
        new() { Title = title, Subtasks = subtasks.Length > 0 ? subtasks : null };

    [Fact]
    public void Plan_hangs_each_task_off_the_last_task_one_level_up()
    {
        var planned = TaskTree.Plan([
            Node("Pasta", Node("Tomatoes"), Node("Basil", Node("Fresh, not dried"))),
            Node("Salad", Node("Lettuce")),
        ]);

        Assert.Equal(
            ["Pasta", "Tomatoes", "Basil", "Fresh, not dried", "Salad", "Lettuce"],
            planned.Select(p => p.Title));
        Assert.Equal([0, 1, 1, 2, 0, 1], planned.Select(p => p.Level));

        var byTitle = planned.ToDictionary(p => p.Title);
        Assert.Null(byTitle["Pasta"].ParentId);
        Assert.Null(byTitle["Salad"].ParentId);
        Assert.Equal(byTitle["Pasta"].Id, byTitle["Tomatoes"].ParentId);
        Assert.Equal(byTitle["Pasta"].Id, byTitle["Basil"].ParentId);
        Assert.Equal(byTitle["Basil"].Id, byTitle["Fresh, not dried"].ParentId);
        Assert.Equal(byTitle["Salad"].Id, byTitle["Lettuce"].ParentId);
    }

    [Fact]
    public void Plan_keys_the_whole_tree_as_one_ascending_chain()
    {
        var planned = TaskTree.Plan([Node("A", Node("A1"), Node("A2")), Node("B")]);

        var keys = planned.Select(p => p.SortOrder).ToList();
        Assert.Equal(keys, keys.Order(StringComparer.Ordinal).ToList());
        Assert.Equal(keys.Count, keys.Distinct().Count());

        // Within a sibling group the chain's subsequence is still ascending — that is what orders each level.
        foreach (var group in planned.GroupBy(p => p.ParentId))
        {
            var siblings = group.Select(p => p.SortOrder).ToList();
            Assert.Equal(siblings, siblings.Order(StringComparer.Ordinal).ToList());
        }
    }

    [Fact]
    public void Plan_continues_the_chain_above_the_list_s_existing_keys()
    {
        var planned = TaskTree.Plan([Node("Next"), Node("After that")], afterSortOrder: "a5");

        Assert.All(planned, p => Assert.True(string.CompareOrdinal("a5", p.SortOrder) < 0));
        Assert.Equal("a6", planned[0].SortOrder);
    }

    [Fact]
    public void Plan_keeps_keys_short_across_a_full_batch()
    {
        var planned = TaskTree.Plan(Enumerable.Range(0, TaskTree.MaxTasks).Select(i => Node($"Task {i}")).ToList());

        Assert.Equal(TaskTree.MaxTasks, planned.Count);
        Assert.Equal(3, planned.Max(p => p.SortOrder.Length));
        Assert.All(planned, p => Assert.True(FractionalIndex.IsValid(p.SortOrder)));
    }

    [Fact]
    public void Plan_rejects_more_tasks_than_the_cap()
    {
        var tasks = Enumerable.Range(0, TaskTree.MaxTasks + 1).Select(i => Node($"Task {i}")).ToList();
        Assert.Contains($"{TaskTree.MaxTasks}", Assert.Throws<McpException>(() => TaskTree.Plan(tasks)).Message);
    }

    [Fact]
    public void Plan_rejects_nesting_past_the_depth_cap()
    {
        var deepest = Node("level 5");
        for (var level = TaskTree.MaxDepth - 1; level > 0; level--) deepest = Node($"level {level}", deepest);
        Assert.Equal(TaskTree.MaxDepth, TaskTree.Plan([deepest]).Count);

        Assert.Contains($"{TaskTree.MaxDepth}", Assert.Throws<McpException>(
            () => TaskTree.Plan([Node("one more", deepest)])).Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Plan_rejects_a_blank_title(string title) =>
        Assert.Throws<McpException>(() => TaskTree.Plan([Node("Fine"), Node(title)]));

    [Fact]
    public void Plan_rejects_an_empty_batch()
    {
        Assert.Throws<McpException>(() => TaskTree.Plan(null));
        Assert.Throws<McpException>(() => TaskTree.Plan([]));
    }

    [Fact]
    public void Plan_trims_titles()
    {
        Assert.Equal("Tomatoes", TaskTree.Plan([Node("  Tomatoes  ")])[0].Title);
    }

    /// <summary>The nested input is a self-referential type; the tool schema must still resolve it
    /// (an SDK that dropped recursion support would silently ship a batch tool an agent can't call).</summary>
    [Fact]
    public void Batch_tool_schema_resolves_the_recursive_node()
    {
        var tool = McpServerTool.Create(
            typeof(TaskTools).GetMethod(nameof(TaskTools.AddTasksBatch))!,
            new McpServerToolCreateOptions { Name = "add_tasks_batch" });

        var schema = tool.ProtocolTool.InputSchema.ToString();
        Assert.Contains("\"subtasks\"", schema);
        Assert.Contains("$ref", schema);
    }
}
