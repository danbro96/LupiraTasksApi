using System.Net;
using LupiraTasksApi.Dtos.Items;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>The cross-list item surface: GET /items (title search over the caller's lists) and the
/// id-only PATCH /items/{id} (list resolved server-side). Both stay scoped to the caller's membership.</summary>
public sealed class CrossListItemsTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    [Fact]
    public async Task Search_spans_the_callers_lists_and_filters_by_title_and_completion()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var todo = await CreateListAsync(alice, "Todo");
        var bills = await CreateListAsync(alice, "Bills");

        var pay = await CreateItemAsync(alice, bills.Id, "Pay electricity bill");
        await CreateItemAsync(alice, todo.Id, "Buy milk");
        var done = await CreateItemAsync(alice, todo.Id, "Pay parking fine");
        await SendJson(alice, HttpMethod.Post, $"/lists/{todo.Id}/items/{done.Id}/complete");

        // Title substring, across both lists.
        var pays = await ReadAsync<ItemCollectionResponse>(await alice.GetAsync("/items?query=pay"));
        Assert.Equal(["Pay electricity bill", "Pay parking fine"], pays.Items.Select(i => i.Title).OrderBy(t => t));
        Assert.Contains(pays.Items, i => i.Id == pay.Id && i.ListId == bills.Id);

        // Open only.
        var open = await ReadAsync<ItemCollectionResponse>(await alice.GetAsync("/items?query=pay&completed=false"));
        Assert.Equal("Pay electricity bill", Assert.Single(open.Items).Title);
    }

    [Fact]
    public async Task Search_excludes_lists_the_caller_is_not_a_member_of()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var bob = Factory.ApiClient("bob@x.test");
        var aliceList = await CreateListAsync(alice, "Alice");
        await CreateItemAsync(alice, aliceList.Id, "Secret errand");

        var bobResults = await ReadAsync<ItemCollectionResponse>(await bob.GetAsync("/items?query=secret"));
        Assert.Empty(bobResults.Items);
    }

    [Fact]
    public async Task Update_by_id_resolves_the_list_and_edits()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var item = await CreateItemAsync(alice, list.Id, "Draft title");

        var updated = await ReadAsync<ItemResponse>(await SendJson(alice, HttpMethod.Patch, $"/items/{item.Id}",
            new UpdateItemRequest { Title = "Final title", TitleProvided = true }));
        Assert.Equal("Final title", updated.Title);
        Assert.Equal(list.Id, updated.ListId);
    }

    [Fact]
    public async Task Update_by_id_is_404_for_a_non_member()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var bob = Factory.ApiClient("bob@x.test");
        var list = await CreateListAsync(alice);
        var item = await CreateItemAsync(alice, list.Id, "Alice's task");

        var resp = await SendJson(bob, HttpMethod.Patch, $"/items/{item.Id}",
            new UpdateItemRequest { Title = "hijacked", TitleProvided = true });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Update_by_id_is_404_for_an_unknown_id() =>
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendJson(Factory.ApiClient("alice@x.test"), HttpMethod.Patch, $"/items/{Guid.NewGuid()}",
                new UpdateItemRequest { Title = "x", TitleProvided = true })).StatusCode);
}
