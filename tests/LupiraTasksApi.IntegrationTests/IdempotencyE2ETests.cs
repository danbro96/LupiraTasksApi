using LupiraTasksApi.Domain;
using LupiraTasksApi.Dtos.Items;
using LupiraTasksApi.Dtos.Lists;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>
/// The <c>Idempotency-Key</c> header → dedup-ledger wiring, exercised over HTTP (the header is read at the
/// transport boundary, so only an end-to-end test covers it). A retried mutation must not create a second stream.
/// </summary>
public sealed class IdempotencyE2ETests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    [Fact]
    public async Task Repeated_list_create_with_same_key_yields_one_list()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var key = Guid.CreateVersion7();
        var body = new CreateListRequest { Id = Guid.CreateVersion7(), Name = "Groceries", Kind = ListKind.Todo };

        var first = await ReadAsync<ListResponse>(await SendJson(alice, HttpMethod.Post, "/lists", body, key));
        var second = await ReadAsync<ListResponse>(await SendJson(alice, HttpMethod.Post, "/lists", body, key));

        Assert.Equal(first.Id, second.Id);
        var all = await ReadAsync<ListCollectionResponse>(await alice.GetAsync("/lists"));
        Assert.Single(all.Lists, l => l.Id == first.Id);
    }

    [Fact]
    public async Task Replay_with_same_key_but_different_body_id_returns_the_original()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var key = Guid.CreateVersion7();

        var original = await ReadAsync<ListResponse>(await SendJson(alice, HttpMethod.Post, "/lists",
            new CreateListRequest { Id = Guid.CreateVersion7(), Name = "A", Kind = ListKind.Todo }, key));
        var replay = await ReadAsync<ListResponse>(await SendJson(alice, HttpMethod.Post, "/lists",
            new CreateListRequest { Id = Guid.CreateVersion7(), Name = "B", Kind = ListKind.Todo }, key));

        Assert.Equal(original.Id, replay.Id);   // the new body id is ignored on replay
        var all = await ReadAsync<ListCollectionResponse>(await alice.GetAsync("/lists"));
        Assert.Single(all.Lists);
    }

    [Fact]
    public async Task Repeated_item_create_with_same_key_yields_one_item()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var key = Guid.CreateVersion7();
        var body = new CreateItemRequest { Id = Guid.CreateVersion7(), Title = "Milk", SortOrder = "a0" };

        await SendJson(alice, HttpMethod.Post, $"/lists/{list.Id}/items", body, key);
        await SendJson(alice, HttpMethod.Post, $"/lists/{list.Id}/items", body, key);

        var items = await ReadAsync<ItemCollectionResponse>(await alice.GetAsync($"/lists/{list.Id}/items"));
        Assert.Single(items.Items);
    }
}
