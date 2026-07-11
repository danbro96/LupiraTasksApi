using LupiraTasksApi.Application;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Items;
using LupiraTasksApi.Domain.Lists;
using LupiraTasksApi.Dtos.Items;
using Marten;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>
/// The unified item lifecycle over REST + the CalDAV write path: status transitions, the done-state
/// interplay (Done == Completed), single-guard LWW between status changes and complete, and the
/// VTODO STATUS round-trip (including Blocked/Waiting via the X-prop).
/// </summary>
public sealed class ItemStatusTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    private const string Email = "alice@x.test";
    // Store-level DAV tests build the Caller + ListCreated directly, so they need a fixed principal id
    // (the HTTP tests go through dev-auth, which resolves the same email to its own provisioned principal).
    private static readonly Guid PrincipalId = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000d5");
    private static readonly DateTimeOffset T0 = new(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int seconds) => T0.AddSeconds(seconds);

    private static Task<ItemResponse> SetStatusAsync(HttpClient api, Guid listId, Guid itemId, ItemStatus status, string? reason = null, DateTimeOffset? at = null) =>
        SendJson(api, HttpMethod.Post, $"/lists/{listId}/items/{itemId}/status",
            new SetStatusRequest { Status = status, Reason = reason, OccurredAt = at })
            .ContinueWith(t => ReadAsync<ItemResponse>(t.Result.EnsureSuccessStatusCode())).Unwrap();

    [Fact]
    public async Task Status_transition_with_reason_round_trips()
    {
        var api = Factory.ApiClient(Email);
        var list = await CreateListAsync(api);
        var item = await CreateItemAsync(api, list.Id);
        Assert.Equal(ItemStatus.Open, item.Status);

        var blocked = await SetStatusAsync(api, list.Id, item.Id, ItemStatus.Blocked, "waiting on vendor");
        Assert.Equal(ItemStatus.Blocked, blocked.Status);
        Assert.Equal("waiting on vendor", blocked.StatusReason);
        Assert.False(blocked.Completed);

        var reloaded = await ReadAsync<ItemResponse>(await api.GetAsync($"/lists/{list.Id}/items/{item.Id}"));
        Assert.Equal(ItemStatus.Blocked, reloaded.Status);
        Assert.Equal("waiting on vendor", reloaded.StatusReason);
    }

    [Fact]
    public async Task Complete_sets_done_and_reopen_sets_open()
    {
        var api = Factory.ApiClient(Email);
        var list = await CreateListAsync(api);
        var item = await CreateItemAsync(api, list.Id);

        var done = await ReadAsync<ItemResponse>(
            (await SendJson(api, HttpMethod.Post, $"/lists/{list.Id}/items/{item.Id}/complete")).EnsureSuccessStatusCode());
        Assert.Equal(ItemStatus.Done, done.Status);
        Assert.True(done.Completed);
        Assert.Equal(Email, done.CompletedBy?.Email);

        var reopened = await ReadAsync<ItemResponse>(
            (await SendJson(api, HttpMethod.Post, $"/lists/{list.Id}/items/{item.Id}/reopen")).EnsureSuccessStatusCode());
        Assert.Equal(ItemStatus.Open, reopened.Status);
        Assert.False(reopened.Completed);
        Assert.Null(reopened.CompletedAt);
    }

    [Fact]
    public async Task Status_change_and_complete_resolve_on_one_guard()
    {
        var api = Factory.ApiClient(Email);
        var list = await CreateListAsync(api);
        var item = await CreateItemAsync(api, list.Id);

        await SendJson(api, HttpMethod.Post, $"/lists/{list.Id}/items/{item.Id}/complete",
            new ItemTimestampRequest { OccurredAt = At(10) });

        // A newer status change wins over the older complete (one lifecycle guard).
        var blocked = await SetStatusAsync(api, list.Id, item.Id, ItemStatus.Blocked, at: At(20));
        Assert.Equal(ItemStatus.Blocked, blocked.Status);
        Assert.False(blocked.Completed);

        // An older status change loses.
        var stale = await SetStatusAsync(api, list.Id, item.Id, ItemStatus.Waiting, at: At(15));
        Assert.Equal(ItemStatus.Blocked, stale.Status);
    }

    [Fact]
    public async Task Status_filter_finds_blocked_items()
    {
        var api = Factory.ApiClient(Email);
        var list = await CreateListAsync(api);
        var a = await CreateItemAsync(api, list.Id, "A", "a0");
        var b = await CreateItemAsync(api, list.Id, "B", "a1");
        await SetStatusAsync(api, list.Id, a.Id, ItemStatus.Blocked, "x");

        var blocked = await ReadAsync<ItemCollectionResponse>(
            await api.GetAsync($"/lists/{list.Id}/items?status=Blocked"));
        Assert.Equal(a.Id, Assert.Single(blocked.Items).Id);
        Assert.DoesNotContain(blocked.Items, i => i.Id == b.Id);
    }

    [Theory]
    [InlineData("STATUS:IN-PROCESS", ItemStatus.InProgress)]
    [InlineData("STATUS:CANCELLED", ItemStatus.Cancelled)]
    [InlineData("STATUS:NEEDS-ACTION\nX-LUPIRA-STATUS:Blocked", ItemStatus.Blocked)]
    [InlineData("STATUS:COMPLETED", ItemStatus.Done)]
    public async Task Dav_put_status_round_trips(string statusLines, ItemStatus expected)
    {
        var listId = await SeedListAsync();
        var raw =
            "BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//phone//EN\n" +
            $"BEGIN:VTODO\nUID:dav-status\nSUMMARY:x\n{statusLines}\n" +
            "END:VTODO\nEND:VCALENDAR\n";

        await using var s = Store.LightweightSession();
        var dav = new TaskDavService(s, new AccessResolver(s));
        var put = await dav.PutVtodoAsync(Caller.Member(PrincipalId, Email, []), listId, "dav-status", raw, null, false, CancellationToken.None);
        Assert.Equal(OpStatus.Ok, put.Status);

        await using var q = Store.QuerySession();
        var item = Assert.Single(await q.Query<Item>().Where(i => i.ListId == listId && !i.Deleted).ToListAsync());
        Assert.Equal(expected, item.Status);
        Assert.Equal(expected == ItemStatus.Done, item.Completed);
    }

    private async Task<Guid> SeedListAsync()
    {
        var listId = Guid.CreateVersion7();
        await using var s = Store.LightweightSession();
        s.Events.StartStream<TodoList>(listId, new ListCreated(listId, "Groceries", ListKind.Todo, null, PrincipalId));
        await s.SaveChangesAsync();
        return listId;
    }
}
