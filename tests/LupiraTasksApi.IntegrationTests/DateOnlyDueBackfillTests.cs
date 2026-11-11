using Lupira.Sync;
using Lupira.Testing.Postgres;
using LupiraTasksApi.Core.Application.Items;
using LupiraTasksApi.Core.Domain.Items;
using LupiraTasksApi.Core.Dtos.Items;
using LupiraTasksApi.Core.Dtos.Sync;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

public sealed class DateOnlyDueBackfillTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    // Europe/Stockholm is UTC+2 in June.
    private static readonly DateTimeOffset EndOfDay = new(2026, 6, 20, 21, 59, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Midnight = new(2026, 6, 20, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Morning = new(2026, 6, 21, 7, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Afternoon = new(2026, 6, 21, 12, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Written = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Dry_run_counts_timed_dues_by_local_time_and_writes_nothing()
    {
        var (alice, list) = await SeedAsync();
        var endOfDay = await CreateAsync(alice, list, "End of day", EndOfDay);
        await CreateAsync(alice, list, "Midnight", Midnight);
        await CreateAsync(alice, list, "Morning", Morning);
        await CreateAsync(alice, list, "Afternoon", Afternoon);
        await CreateItemAsync(alice, list, "Undated");

        var report = await RunAsync(dryRun: true);

        Assert.Equal(new Dictionary<string, int> { ["23:59"] = 1, ["00:00"] = 1, ["09:00"] = 1, ["other"] = 1 }, report.Counts);
        Assert.Contains(report.Samples["23:59"], s => s.StartsWith(endOfDay.Id.ToString()));
        Assert.Equal(0, report.Converted);
        var item = await LoadAsync(endOfDay.Id);
        Assert.Equal(EndOfDay, item.DueAt);
        Assert.Null(item.DueOn);
    }

    [Fact]
    public async Task Run_turns_end_of_day_dues_into_date_only_and_re_run_is_a_no_op()
    {
        var (alice, list) = await SeedAsync();
        var endOfDay = await CreateAsync(alice, list, "End of day", EndOfDay);
        var morning = await CreateAsync(alice, list, "Morning", Morning);
        var cursor = (await ReadAsync<SyncPage<ItemSyncChange>>(await alice.GetAsync("/sync/items"))).Cursor;

        Assert.Equal(1, (await RunAsync(dryRun: false)).Converted);

        var item = await LoadAsync(endOfDay.Id);
        Assert.Equal(new DateOnly(2026, 6, 20), item.DueOn);
        Assert.Null(item.DueAt);
        Assert.Equal(Written.AddTicks(1), item.State.DueTs);
        Assert.Equal(Morning, (await LoadAsync(morning.Id)).DueAt);

        var delta = await ReadAsync<SyncPage<ItemSyncChange>>(await alice.GetAsync($"/sync/items?since={Uri.EscapeDataString(cursor)}"));
        Assert.Equal(new DateOnly(2026, 6, 20), Assert.Single(delta.Changed).Item.DueOn);

        var version = item.Version;
        Assert.Equal(0, (await RunAsync(dryRun: false)).Converted);
        Assert.Equal(version, (await LoadAsync(endOfDay.Id)).Version);
    }

    [Fact]
    public async Task Newer_due_edit_wins_over_the_backfill()
    {
        var (alice, list) = await SeedAsync();
        var endOfDay = await CreateAsync(alice, list, "End of day", EndOfDay);
        await RunAsync(dryRun: false);

        var stale = await SendJson(alice, HttpMethod.Patch, $"/lists/{list}/items/{endOfDay.Id}",
            new UpdateItemRequest { DueAt = Afternoon, DueAtProvided = true, OccurredAt = Written });
        stale.EnsureSuccessStatusCode();
        Assert.Equal(new DateOnly(2026, 6, 20), (await LoadAsync(endOfDay.Id)).DueOn);

        var newer = await SendJson(alice, HttpMethod.Patch, $"/lists/{list}/items/{endOfDay.Id}",
            new UpdateItemRequest { DueAt = Afternoon, DueAtProvided = true, OccurredAt = Written.AddMilliseconds(1) });
        newer.EnsureSuccessStatusCode();
        var item = await LoadAsync(endOfDay.Id);
        Assert.Null(item.DueOn);
        Assert.Equal(Afternoon, item.DueAt);
    }

    private async Task<(HttpClient Api, Guid ListId)> SeedAsync()
    {
        var alice = Factory.ApiClient("alice@x.test");
        return (alice, (await CreateListAsync(alice)).Id);
    }

    private static async Task<ItemDto> CreateAsync(HttpClient api, Guid listId, string title, DateTimeOffset dueAt)
    {
        var resp = await SendJson(api, HttpMethod.Post, $"/lists/{listId}/items",
            new CreateItemRequest { Id = Guid.CreateVersion7(), Title = title, SortOrder = "a0", DueAt = dueAt, OccurredAt = Written });
        resp.EnsureSuccessStatusCode();
        return await ReadAsync<ItemDto>(resp);
    }

    private Task<DateOnlyDueBackfillReport> RunAsync(bool dryRun) =>
        InScope(sp => ActivatorUtilities.CreateInstance<DateOnlyDueBackfill>(sp).RunAsync(dryRun, CancellationToken.None));

    private async Task<Item> LoadAsync(Guid id)
    {
        await using var session = Store.QuerySession();
        return (await session.LoadAsync<Item>(id))!;
    }
}
