using System.Net;
using Lupira.Sync;
using Lupira.Testing.Postgres;
using LupiraTasksApi.Core.Application;
using LupiraTasksApi.Core.Application.Sync;
using LupiraTasksApi.Core.Domain;
using LupiraTasksApi.Core.Dtos.Items;
using LupiraTasksApi.Core.Dtos.Lists;
using LupiraTasksApi.Core.Dtos.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>The offline-mirror feeds <c>GET /sync/lists</c> and <c>GET /sync/items</c>: full-sync paging, deltas,
/// item tombstones, scope resets when the caller's readable lists change, and per-field guards.</summary>
public sealed class SyncFeedTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    // The ItemLwwTests vectors: CmdLo < CmdHi.
    private static readonly Guid CmdLo = Guid.Parse("0190a000-0000-7000-8000-0000000000c1");
    private static readonly Guid CmdHi = Guid.Parse("0190a000-0000-7000-8000-0000000000c2");
    private static readonly DateTimeOffset T0 = new(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int seconds) => T0.AddSeconds(seconds);

    [Fact]
    public async Task Full_sync_pages_by_id_then_resumes_as_an_empty_delta()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var created = new List<Guid>();
        for (var i = 0; i < 5; i++) created.Add((await CreateItemAsync(alice, list.Id, $"Item {i}", $"a{i}")).Id);
        var gone = await CreateItemAsync(alice, list.Id, "Gone", "b0");
        await SendJson(alice, HttpMethod.Delete, $"/lists/{list.Id}/items/{gone.Id}");

        var pages = await DrainAsync<ItemSyncChange>(alice, "/sync/items", since: null, limit: 2);

        Assert.Equal(new[] { true, false, false }, pages.Select(p => p.Reset));
        Assert.Equal(new[] { 2, 2, 1 }, pages.Select(p => p.Changed.Count));
        Assert.All(pages, p => Assert.Empty(p.Deleted));
        Assert.Equal(created.Order(), pages.SelectMany(p => p.Changed).Select(c => c.Item.Id).Order());

        var next = await PageAsync<ItemSyncChange>(alice, "/sync/items", pages[^1].Cursor);
        Assert.False(next.Reset);
        Assert.False(next.HasMore);
        Assert.Empty(next.Changed);
        Assert.Equal(pages[^1].Cursor, next.Cursor);
    }

    [Fact]
    public async Task Lists_full_sync_pages_and_a_delta_returns_the_changed_list()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var lists = new List<ListDto>();
        for (var i = 0; i < 3; i++) lists.Add(await CreateListAsync(alice, $"List {i}"));

        var pages = await DrainAsync<ListDto>(alice, "/sync/lists", since: null, limit: 2);
        Assert.Equal(new[] { 2, 1 }, pages.Select(p => p.Changed.Count));
        Assert.Equal(lists.Select(l => l.Id).Order(), pages.SelectMany(p => p.Changed).Select(l => l.Id).Order());
        Assert.All(pages.SelectMany(p => p.Changed), l => Assert.Single(l.Members));

        await SendJson(alice, HttpMethod.Patch, $"/lists/{lists[1].Id}", new UpdateListRequest { Name = "Renamed" });
        var delta = await PageAsync<ListDto>(alice, "/sync/lists", pages[^1].Cursor);

        Assert.False(delta.Reset);
        Assert.Equal("Renamed", Assert.Single(delta.Changed).Name);
        Assert.Empty(delta.Deleted);
    }

    [Fact]
    public async Task Delta_returns_changed_and_new_items_and_tombstones_deleted_ones()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var keep = await CreateItemAsync(alice, list.Id, "Keep", "a0");
        var edit = await CreateItemAsync(alice, list.Id, "Edit", "a1");
        var drop = await CreateItemAsync(alice, list.Id, "Drop", "a2");
        var full = await PageAsync<ItemSyncChange>(alice, "/sync/items", since: null);

        await SendJson(alice, HttpMethod.Patch, $"/lists/{list.Id}/items/{edit.Id}", new UpdateItemRequest { Title = "Edited", TitleProvided = true });
        await SendJson(alice, HttpMethod.Delete, $"/lists/{list.Id}/items/{drop.Id}");
        var added = await CreateItemAsync(alice, list.Id, "Added", "a3");
        var delta = await PageAsync<ItemSyncChange>(alice, "/sync/items", full.Cursor);

        Assert.False(delta.Reset);
        Assert.Equal(new[] { edit.Id, added.Id }.Order(), delta.Changed.Select(c => c.Item.Id).Order());
        Assert.Equal("Edited", delta.Changed.Single(c => c.Item.Id == edit.Id).Item.Title);
        Assert.Equal(new[] { drop.Id }, delta.Deleted);
        Assert.DoesNotContain(delta.Changed, c => c.Item.Id == keep.Id);
    }

    [Fact]
    public async Task Delta_pages_across_more_changes_than_the_limit()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var items = new List<ItemDto>();
        for (var i = 0; i < 3; i++) items.Add(await CreateItemAsync(alice, list.Id, $"Item {i}", $"a{i}"));
        var full = await PageAsync<ItemSyncChange>(alice, "/sync/items", since: null);

        foreach (var item in items)
            await SendJson(alice, HttpMethod.Patch, $"/lists/{list.Id}/items/{item.Id}", new UpdateItemRequest { Notes = "n", NotesProvided = true });
        var pages = await DrainAsync<ItemSyncChange>(alice, "/sync/items", full.Cursor, limit: 2);

        Assert.Equal(new[] { 2, 1 }, pages.Select(p => p.Changed.Count));
        Assert.All(pages, p => Assert.False(p.Reset));
        Assert.Equal(items.Select(i => i.Id), pages.SelectMany(p => p.Changed).Select(c => c.Item.Id));
    }

    [Fact]
    public async Task Changes_to_lists_the_caller_cannot_read_never_reach_their_feed()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var bob = Factory.ApiClient("bob@x.test");
        var alicesList = await CreateListAsync(alice, "Alice");
        var alicesItem = await CreateItemAsync(alice, alicesList.Id, "Private", "a0");
        var bobsList = await CreateListAsync(bob, "Bob");
        await CreateItemAsync(bob, bobsList.Id, "Mine", "a0");
        var items = await PageAsync<ItemSyncChange>(bob, "/sync/items", since: null);
        var lists = await PageAsync<ListDto>(bob, "/sync/lists", since: null);

        await SendJson(alice, HttpMethod.Patch, $"/lists/{alicesList.Id}", new UpdateListRequest { Name = "Renamed" });
        await SendJson(alice, HttpMethod.Delete, $"/lists/{alicesList.Id}/items/{alicesItem.Id}");

        var itemDelta = await PageAsync<ItemSyncChange>(bob, "/sync/items", items.Cursor);
        var listDelta = await PageAsync<ListDto>(bob, "/sync/lists", lists.Cursor);
        Assert.Empty(itemDelta.Changed);
        Assert.Empty(itemDelta.Deleted);
        Assert.Empty(listDelta.Changed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Losing_membership_resets_both_feeds_without_the_list(bool leaves)
    {
        var alice = Factory.ApiClient("alice@x.test");
        var bob = Factory.ApiClient("bob@x.test");
        var shared = await CreateListAsync(alice, "Shared");
        await CreateItemAsync(alice, shared.Id, "Shared item", "a0");
        var own = await CreateListAsync(bob, "Own");
        var ownItem = await CreateItemAsync(bob, own.Id, "Own item", "a0");
        await SendJson(alice, HttpMethod.Post, $"/lists/{shared.Id}/members", new AddMemberRequest { Email = "bob@x.test", Role = ListRole.Viewer });
        var bobId = (await ReadAsync<ListDto>(await alice.GetAsync($"/lists/{shared.Id}"))).Members.Single(m => m.Email == "bob@x.test").PrincipalId;
        var lists = await PageAsync<ListDto>(bob, "/sync/lists", since: null);
        var items = await PageAsync<ItemSyncChange>(bob, "/sync/items", since: null);
        Assert.Equal(2, lists.Changed.Count);
        Assert.Equal(2, items.Changed.Count);

        var remove = await SendJson(leaves ? bob : alice, HttpMethod.Delete, $"/lists/{shared.Id}/members/{bobId}");
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);

        var listReset = await PageAsync<ListDto>(bob, "/sync/lists", lists.Cursor);
        var itemReset = await PageAsync<ItemSyncChange>(bob, "/sync/items", items.Cursor);
        Assert.True(listReset.Reset);
        Assert.Equal(new[] { own.Id }, listReset.Changed.Select(l => l.Id));
        Assert.True(itemReset.Reset);
        Assert.Equal(new[] { ownItem.Id }, itemReset.Changed.Select(c => c.Item.Id));
    }

    [Fact]
    public async Task Deleting_a_list_resets_both_feeds_without_it()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var keep = await CreateListAsync(alice, "Keep");
        var doomed = await CreateListAsync(alice, "Doomed");
        await CreateItemAsync(alice, doomed.Id, "Doomed item", "a0");
        var lists = await PageAsync<ListDto>(alice, "/sync/lists", since: null);
        var items = await PageAsync<ItemSyncChange>(alice, "/sync/items", since: null);

        await SendJson(alice, HttpMethod.Delete, $"/lists/{doomed.Id}");

        var listReset = await PageAsync<ListDto>(alice, "/sync/lists", lists.Cursor);
        var itemReset = await PageAsync<ItemSyncChange>(alice, "/sync/items", items.Cursor);
        Assert.True(listReset.Reset);
        Assert.Equal(new[] { keep.Id }, listReset.Changed.Select(l => l.Id));
        Assert.True(itemReset.Reset);
        Assert.Empty(itemReset.Changed);
    }

    [Fact]
    public async Task Guards_carry_the_lww_winner_of_each_field()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var itemId = Guid.CreateVersion7();
        var createCmd = Guid.CreateVersion7();
        var tagId = Guid.CreateVersion7();
        var url = $"/lists/{list.Id}/items/{itemId}";
        await SendJson(alice, HttpMethod.Post, $"/lists/{list.Id}/items",
            new CreateItemRequest { Id = itemId, Title = "Milk", SortOrder = "a0", OccurredAt = At(0) }, createCmd);

        // A stale rename loses to the newer one; an equal-OccurredAt tie goes to the greater command id.
        var newerRename = Guid.CreateVersion7();
        await SendJson(alice, HttpMethod.Patch, url, new UpdateItemRequest { Title = "Newer", TitleProvided = true, OccurredAt = At(20) }, newerRename);
        await SendJson(alice, HttpMethod.Patch, url, new UpdateItemRequest { Title = "Stale", TitleProvided = true, OccurredAt = At(10) }, Guid.CreateVersion7());
        await SendJson(alice, HttpMethod.Patch, url, new UpdateItemRequest { Notes = "FromHi", NotesProvided = true, OccurredAt = At(20) }, CmdHi);
        await SendJson(alice, HttpMethod.Patch, url, new UpdateItemRequest { Notes = "FromLo", NotesProvided = true, OccurredAt = At(20) }, CmdLo);
        var tagAdd = Guid.CreateVersion7();
        await SendJson(alice, HttpMethod.Patch, url, new UpdateItemRequest { AddTagIds = [tagId], OccurredAt = At(30) }, tagAdd);
        await SendJson(alice, HttpMethod.Patch, url, new UpdateItemRequest { RemoveTagIds = [tagId], OccurredAt = At(25) }, Guid.CreateVersion7());

        var change = Assert.Single((await PageAsync<ItemSyncChange>(alice, "/sync/items", since: null)).Changed);

        Assert.Equal("Newer", change.Item.Title);
        AssertGuard(change.Guards.Name, At(20), newerRename);
        Assert.Equal("FromHi", change.Item.Notes);
        AssertGuard(change.Guards.Notes, At(20), CmdHi);
        Assert.Equal(new[] { tagId }, change.Item.Tags);
        AssertGuard(Assert.Single(change.Guards.Tags, kv => kv.Key == tagId).Value, At(30), tagAdd);
        AssertGuard(change.Guards.Move, At(0), createCmd);
        AssertGuard(change.Guards.Due, default, Guid.Empty);
    }

    [Fact]
    public async Task Default_settle_lag_holds_back_changes_younger_than_the_lag()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var full = await PageAsync<ItemSyncChange>(alice, "/sync/items", since: null);
        await CreateItemAsync(alice, list.Id);

        var caller = Caller.Member(list.Owner.PrincipalId, "alice@x.test", []);
        var fenced = await InScope(sp => ActivatorUtilities
            .CreateInstance<SyncFeed>(sp, Options.Create(new SyncFeedOptions()))
            .ItemsAsync(caller, full.Cursor, limit: null, CancellationToken.None));

        Assert.Empty(fenced.Value.Changed);
        Assert.Equal(full.Cursor, fenced.Value.Cursor);
    }

    [Fact]
    public async Task Malformed_cursor_is_400()
    {
        var alice = Factory.ApiClient("alice@x.test");

        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync("/sync/items?since=not-a-cursor")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync("/sync/lists?since=1.abc.zz")).StatusCode);
    }

    private static void AssertGuard(SectionGuardDto guard, DateTimeOffset ts, Guid cmd)
    {
        Assert.Equal(ts, guard.Ts);
        Assert.Equal(cmd, guard.Cmd);
    }

    private static async Task<SyncPage<T>> PageAsync<T>(HttpClient api, string path, string? since, int? limit = null)
    {
        var query = new List<string>();
        if (since is not null) query.Add($"since={Uri.EscapeDataString(since)}");
        if (limit is not null) query.Add($"limit={limit}");
        var resp = await api.GetAsync(query.Count == 0 ? path : $"{path}?{string.Join('&', query)}");
        resp.EnsureSuccessStatusCode();
        return await ReadAsync<SyncPage<T>>(resp);
    }

    private static async Task<List<SyncPage<T>>> DrainAsync<T>(HttpClient api, string path, string? since, int limit)
    {
        var pages = new List<SyncPage<T>>();
        do
        {
            pages.Add(await PageAsync<T>(api, path, since, limit));
            since = pages[^1].Cursor;
        }
        while (pages[^1].HasMore && pages.Count < 10);
        return pages;
    }
}
