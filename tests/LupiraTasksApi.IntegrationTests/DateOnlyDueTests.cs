using System.Net;
using Lupira.Testing.Postgres;
using LupiraTasksApi.Core.Domain.Items;
using LupiraTasksApi.Core.Domain.Items.Events;
using LupiraTasksApi.Core.Dtos.Items;
using ModelContextProtocol;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>A due is either a whole day (<c>dueOn</c>) or an instant (<c>dueAt</c>), never both.</summary>
public sealed class DateOnlyDueTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    private static readonly DateOnly Day = new(2026, 6, 20);

    // Europe/Stockholm is UTC+2 in June, so the day starts at 22:00Z the evening before.
    private static readonly DateTimeOffset DayStart = new(2026, 6, 19, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Date_only_due_counts_from_its_local_day_start_in_due_ranges()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice, "Deadlines");
        await CreateAsync(alice, list.Id, "Whole day", dueOn: Day);
        await CreateItemAsync(alice, list.Id, "Timed", dueAt: DayStart.AddHours(12));

        Assert.Equal(["Timed", "Whole day"], await TitlesDueAsync(alice, DayStart, DayStart.AddDays(1)));
        Assert.Equal(["Whole day"], await TitlesDueAsync(alice, DayStart, DayStart.AddTicks(10)));
        Assert.Empty(await TitlesDueAsync(alice, DayStart.AddDays(-1), DayStart));
        Assert.Equal(["Timed"], await TitlesDueAsync(alice, DayStart.AddTicks(10), DayStart.AddDays(1)));
    }

    [Fact]
    public async Task Create_and_update_take_at_most_one_due_shape()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);

        var both = await SendJson(alice, HttpMethod.Post, $"/lists/{list.Id}/items",
            new CreateItemRequest { Id = Guid.CreateVersion7(), Title = "Both", SortOrder = "a0", DueOn = Day, DueAt = DayStart });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);

        var item = await CreateAsync(alice, list.Id, "Milk", dueOn: Day);
        Assert.Equal(Day, item.DueOn);
        Assert.Null(item.DueAt);

        var timed = await PatchAsync(alice, list.Id, item.Id, new UpdateItemRequest { DueAt = DayStart, DueAtProvided = true });
        Assert.Null(timed.DueOn);
        Assert.Equal(DayStart, timed.DueAt);

        var rejected = await SendJson(alice, HttpMethod.Patch, $"/lists/{list.Id}/items/{item.Id}",
            new UpdateItemRequest { DueOn = Day, DueAt = DayStart, DueAtProvided = true });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        var dated = await PatchAsync(alice, list.Id, item.Id, new UpdateItemRequest { DueOn = Day, DueAtProvided = true });
        Assert.Equal(Day, dated.DueOn);
        Assert.Null(dated.DueAt);

        var cleared = await PatchAsync(alice, list.Id, item.Id, new UpdateItemRequest { DueAtProvided = true });
        Assert.Null(cleared.DueOn);
        Assert.Null(cleared.DueAt);
    }

    [Fact]
    public async Task Mcp_tools_set_and_clear_either_due_shape()
    {
        const string agent = "agent@x.test";
        var task = await AsAgent(agent, async tools =>
        {
            var list = await tools.CreateList("Chores");
            return await tools.AddTask(list.Id, "Bins", dueOn: Day);
        });
        Assert.Equal(Day, task.DueOn);
        Assert.Null(task.DueAt);

        var timed = await AsAgent(agent, tools => tools.UpdateTask(task.Id, dueAt: DayStart));
        Assert.Null(timed.DueOn);
        Assert.Equal(DayStart, timed.DueAt);

        await Assert.ThrowsAsync<McpException>(() => AsAgent(agent, tools => tools.UpdateTask(task.Id, dueOn: Day, clearDue: true)));

        var cleared = await AsAgent(agent, tools => tools.UpdateTask(task.Id, clearDue: true));
        Assert.Null(cleared.DueOn);
        Assert.Null(cleared.DueAt);
    }

    [Fact]
    public async Task Persisted_v1_due_events_read_back_as_timed_dues()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var item = await CreateItemAsync(alice, list.Id);

        await using (var session = Store.LightweightSession())
        {
            session.Events.Append(item.Id, new ItemDueDateSet(item.Id, DayStart, DateTimeOffset.UtcNow, Guid.CreateVersion7()));
            await session.SaveChangesAsync();
        }

        await using var query = Store.QuerySession();
        var events = await query.Events.FetchStreamAsync(item.Id);
        var due = Assert.IsType<ItemDueDateSetV2>(events[^1].Data);
        Assert.Null(due.DueOn);
        Assert.Equal(DayStart, due.DueAt);

        var aggregate = await query.Events.AggregateStreamAsync<Item>(item.Id);
        Assert.Equal(DayStart, aggregate!.DueAt);
        Assert.Null(aggregate.DueOn);
    }

    private static async Task<ItemDto> CreateAsync(HttpClient api, Guid listId, string title, DateOnly dueOn)
    {
        var resp = await SendJson(api, HttpMethod.Post, $"/lists/{listId}/items",
            new CreateItemRequest { Id = Guid.CreateVersion7(), Title = title, SortOrder = "a0", DueOn = dueOn });
        resp.EnsureSuccessStatusCode();
        return await ReadAsync<ItemDto>(resp);
    }

    private static async Task<ItemDto> PatchAsync(HttpClient api, Guid listId, Guid itemId, UpdateItemRequest body)
    {
        var resp = await SendJson(api, HttpMethod.Patch, $"/lists/{listId}/items/{itemId}", body);
        resp.EnsureSuccessStatusCode();
        return await ReadAsync<ItemDto>(resp);
    }

    private static async Task<List<string>> TitlesDueAsync(HttpClient api, DateTimeOffset from, DateTimeOffset to)
    {
        static string Iso(DateTimeOffset d) => Uri.EscapeDataString(d.ToString("O"));
        var items = await ReadAsync<List<ItemDto>>(await api.GetAsync($"/items?dueFrom={Iso(from)}&dueTo={Iso(to)}"));
        return items.Select(i => i.Title).OrderBy(t => t).ToList();
    }
}
