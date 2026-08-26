using System.Net;
using LupiraTasksApi.Core.Domain;
using LupiraTasksApi.Core.Dtos.Lists;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>
/// Per-user list ordering (<c>POST /lists/{id}/order</c>) and the order <c>GET /lists</c> returns:
/// the caller's own fractional keys first, never-ordered lists by name after them, and archived lists
/// most-recently-archived first. The ordering lives on the caller's membership row, so the same shared
/// list can sit in a different position for every member.
/// </summary>
public sealed class ListOrderTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    private static async Task<HttpResponseMessage> SetOrder(HttpClient api, Guid listId, string sortOrder) =>
        await SendJson(api, HttpMethod.Post, $"/lists/{listId}/order", new SetListOrderRequest { SortOrder = sortOrder });

    private static async Task<List<ListDto>> Lists(HttpClient api, bool archived = false)
    {
        var resp = await ReadAsync<ListCollectionResponse>(
            await api.GetAsync($"/lists?archived={archived.ToString().ToLowerInvariant()}"));
        return [.. resp.Lists];
    }

    [Fact]
    public async Task Ordered_lists_come_first_then_never_ordered_by_name()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var apple = await CreateListAsync(alice, "Apple");
        await CreateListAsync(alice, "Mango");
        var zebra = await CreateListAsync(alice, "Zebra");

        (await SetOrder(alice, zebra.Id, "a0")).EnsureSuccessStatusCode();

        var lists = await Lists(alice);
        Assert.Equal(["Zebra", "Apple", "Mango"], lists.Select(l => l.Name));
        Assert.Equal("a0", lists.Single(l => l.Id == zebra.Id).SortOrder);
        Assert.Null(lists.Single(l => l.Id == apple.Id).SortOrder);
    }

    [Fact]
    public async Task Keyed_lists_sort_by_key_not_by_name()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var apple = await CreateListAsync(alice, "Apple");
        var zebra = await CreateListAsync(alice, "Zebra");

        await SetOrder(alice, zebra.Id, "a0");
        await SetOrder(alice, apple.Id, "a1");

        Assert.Equal(["Zebra", "Apple"], (await Lists(alice)).Select(l => l.Name));
    }

    [Fact]
    public async Task Order_is_per_member_and_leaves_the_list_untouched_for_others()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var bob = Factory.ApiClient("bob@x.test");
        var shared = await CreateListAsync(alice, "Shared");
        await SendJson(alice, HttpMethod.Post, $"/lists/{shared.Id}/members", new AddMemberRequest { Email = "bob@x.test" });

        var before = (await Lists(bob)).Single(l => l.Id == shared.Id);
        await SetOrder(alice, shared.Id, "a0");
        var after = (await Lists(bob)).Single(l => l.Id == shared.Id);

        Assert.Equal("a0", (await Lists(alice)).Single(l => l.Id == shared.Id).SortOrder);
        Assert.Null(after.SortOrder);
        // A personal reorder is not a change to the list: bob's client must not see it as a remote edit.
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task Two_members_can_hold_opposite_orders_of_the_same_lists()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var bob = Factory.ApiClient("bob@x.test");
        var first = await CreateListAsync(alice, "First");
        var second = await CreateListAsync(alice, "Second");
        foreach (var l in new[] { first, second })
            await SendJson(alice, HttpMethod.Post, $"/lists/{l.Id}/members", new AddMemberRequest { Email = "bob@x.test" });

        await SetOrder(alice, first.Id, "a0");
        await SetOrder(alice, second.Id, "a1");
        await SetOrder(bob, second.Id, "a0");
        await SetOrder(bob, first.Id, "a1");

        Assert.Equal(["First", "Second"], (await Lists(alice)).Select(l => l.Name));
        Assert.Equal(["Second", "First"], (await Lists(bob)).Select(l => l.Name));
    }

    [Fact]
    public async Task A_viewer_can_order_their_own_screen()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var bob = Factory.ApiClient("bob@x.test");
        var list = await CreateListAsync(alice, "Read only");
        await SendJson(alice, HttpMethod.Post, $"/lists/{list.Id}/members",
            new AddMemberRequest { Email = "bob@x.test", Role = ListRole.Viewer });

        var resp = await SetOrder(bob, list.Id, "a0");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("a0", (await ReadAsync<ListDto>(resp)).SortOrder);
    }

    [Fact]
    public async Task Non_members_get_404_and_malformed_keys_get_400()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var stranger = Factory.ApiClient("stranger@x.test");
        var list = await CreateListAsync(alice);

        Assert.Equal(HttpStatusCode.NotFound, (await SetOrder(stranger, list.Id, "a0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetOrder(alice, list.Id, "")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetOrder(alice, list.Id, "a 0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetOrder(alice, list.Id, new string('a', 65))).StatusCode);
    }

    [Fact]
    public async Task Replaying_the_same_idempotency_key_does_not_re_order()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var key = Guid.CreateVersion7();

        await SendJson(alice, HttpMethod.Post, $"/lists/{list.Id}/order", new SetListOrderRequest { SortOrder = "a0" }, key);
        var replay = await SendJson(alice, HttpMethod.Post, $"/lists/{list.Id}/order", new SetListOrderRequest { SortOrder = "zz" }, key);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("a0", (await Lists(alice)).Single(l => l.Id == list.Id).SortOrder);
    }

    [Fact]
    public async Task Archived_lists_come_back_most_recently_archived_first()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var early = await CreateListAsync(alice, "Early");
        var late = await CreateListAsync(alice, "Late");

        await SendJson(alice, HttpMethod.Post, $"/lists/{early.Id}/archive");
        await SendJson(alice, HttpMethod.Post, $"/lists/{late.Id}/archive");

        var archived = await Lists(alice, archived: true);
        Assert.Equal(["Late", "Early"], archived.Select(l => l.Name));
        Assert.All(archived, l => Assert.NotNull(l.ArchivedAt));
    }

    [Fact]
    public async Task A_later_edit_does_not_move_an_archived_list_to_the_top()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var early = await CreateListAsync(alice, "Early");
        var late = await CreateListAsync(alice, "Late");
        await SendJson(alice, HttpMethod.Post, $"/lists/{early.Id}/archive");
        await SendJson(alice, HttpMethod.Post, $"/lists/{late.Id}/archive");

        // Renaming bumps UpdatedAt; the archived view sorts on ArchivedAt, so the order must hold.
        await SendJson(alice, HttpMethod.Patch, $"/lists/{early.Id}", new UpdateListRequest { Name = "Early renamed" });

        Assert.Equal(["Late", "Early renamed"], (await Lists(alice, archived: true)).Select(l => l.Name));
    }

    [Fact]
    public async Task Restore_clears_archived_at_and_keeps_the_order_key()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var kept = await CreateListAsync(alice, "Kept");
        await CreateListAsync(alice, "Aaa other");
        await SetOrder(alice, kept.Id, "a0");

        await SendJson(alice, HttpMethod.Post, $"/lists/{kept.Id}/archive");
        Assert.NotNull((await Lists(alice, archived: true)).Single(l => l.Id == kept.Id).ArchivedAt);

        await SendJson(alice, HttpMethod.Post, $"/lists/{kept.Id}/restore");

        var restored = (await Lists(alice)).Single(l => l.Id == kept.Id);
        Assert.Null(restored.ArchivedAt);
        Assert.Equal("a0", restored.SortOrder);
        // Keeping the key means it returns to where it was dragged, not to the end.
        Assert.Equal("Kept", (await Lists(alice))[0].Name);
    }
}
