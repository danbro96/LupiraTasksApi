using System.Net;
using System.Text.Json.Nodes;
using Lupira.Testing.Postgres;
using LupiraTasksApi.Core.Dtos.Items;
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
        var pays = await ReadAsync<List<ItemDto>>(await alice.GetAsync("/items?query=pay"));
        Assert.Equal(["Pay electricity bill", "Pay parking fine"], pays.Select(i => i.Title).OrderBy(t => t));
        Assert.Contains(pays, i => i.Id == pay.Id && i.ListId == bills.Id);

        var open = await ReadAsync<List<ItemDto>>(await alice.GetAsync("/items?query=pay&completed=false"));
        Assert.Equal("Pay electricity bill", Assert.Single(open).Title);
    }

    [Fact]
    public async Task Search_bounds_dueAt_half_open_and_excludes_undated()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice, "Deadlines");

        var from = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);

        await CreateItemAsync(alice, list.Id, "On from boundary", dueAt: from);
        await CreateItemAsync(alice, list.Id, "Inside", dueAt: from.AddDays(3));
        await CreateItemAsync(alice, list.Id, "On to boundary", dueAt: to);
        await CreateItemAsync(alice, list.Id, "Before", dueAt: from.AddDays(-1));
        await CreateItemAsync(alice, list.Id, "Undated");

        static string Iso(DateTimeOffset d) => Uri.EscapeDataString(d.ToString("O"));

        var window = await ReadAsync<List<ItemDto>>(
            await alice.GetAsync($"/items?dueFrom={Iso(from)}&dueTo={Iso(to)}"));
        Assert.Equal(["Inside", "On from boundary"], window.Select(i => i.Title).OrderBy(t => t));

        var fromOnly = await ReadAsync<List<ItemDto>>(await alice.GetAsync($"/items?dueFrom={Iso(from)}"));
        Assert.Equal(["Inside", "On from boundary", "On to boundary"], fromOnly.Select(i => i.Title).OrderBy(t => t));

        var toOnly = await ReadAsync<List<ItemDto>>(await alice.GetAsync($"/items?dueTo={Iso(to)}"));
        Assert.Equal(["Before", "Inside", "On from boundary"], toOnly.Select(i => i.Title).OrderBy(t => t));
    }

    [Fact]
    public async Task Search_composes_due_window_with_completion()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice, "Deadlines");
        var due = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
        await CreateItemAsync(alice, list.Id, "Still open", dueAt: due);
        var done = await CreateItemAsync(alice, list.Id, "Already done", dueAt: due);
        await SendJson(alice, HttpMethod.Post, $"/lists/{list.Id}/items/{done.Id}/complete");

        static string Iso(DateTimeOffset d) => Uri.EscapeDataString(d.ToString("O"));

        var open = await ReadAsync<List<ItemDto>>(await alice.GetAsync(
            $"/items?completed=false&dueFrom={Iso(due.AddDays(-1))}&dueTo={Iso(due.AddDays(1))}"));
        Assert.Equal("Still open", Assert.Single(open).Title);
    }

    [Fact]
    public async Task Search_excludes_lists_the_caller_is_not_a_member_of()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var bob = Factory.ApiClient("bob@x.test");
        var aliceList = await CreateListAsync(alice, "Alice");
        await CreateItemAsync(alice, aliceList.Id, "Secret errand");

        var bobResults = await ReadAsync<List<ItemDto>>(await bob.GetAsync("/items?query=secret"));
        Assert.Empty(bobResults);
    }

    [Fact]
    public async Task Update_by_id_resolves_the_list_and_edits()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var item = await CreateItemAsync(alice, list.Id, "Draft title");

        var updated = await ReadAsync<ItemDto>(await SendJson(alice, HttpMethod.Patch, $"/items/{item.Id}",
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

    [Fact]
    public async Task Set_metadata_by_id_resolves_the_list_and_persists()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var item = await CreateItemAsync(alice, list.Id, "Pay bill");

        var body = new SetMetadataRequest { Metadata = new JsonObject { ["kind"] = "bill", ["invoiceNumber"] = "INV-42" } };
        var updated = await ReadAsync<ItemDto>(await SendJson(alice, HttpMethod.Post, $"/items/{item.Id}/metadata", body));
        Assert.Equal("INV-42", updated.Metadata?["invoiceNumber"]?.GetValue<string>());
    }

    [Fact]
    public async Task Set_metadata_by_id_is_404_for_a_non_member()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var bob = Factory.ApiClient("bob@x.test");
        var list = await CreateListAsync(alice);
        var item = await CreateItemAsync(alice, list.Id, "Alice's task");

        var resp = await SendJson(bob, HttpMethod.Post, $"/items/{item.Id}/metadata",
            new SetMetadataRequest { Metadata = new JsonObject { ["x"] = 1 } });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
