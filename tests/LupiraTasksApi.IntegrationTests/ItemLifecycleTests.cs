using System.Net;
using LupiraTasksApi.Dtos.Items;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>Item CRUD + completion + tombstone through HTTP: create → complete → reopen → delete (excluded + 404).</summary>
public sealed class ItemLifecycleTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    [Fact]
    public async Task Create_complete_reopen_delete_round_trip()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var item = await CreateItemAsync(alice, list.Id, "Buy milk");

        var fetched = await ReadAsync<ItemResponse>(await alice.GetAsync($"/lists/{list.Id}/items/{item.Id}"));
        Assert.False(fetched.Completed);

        var completed = await ReadAsync<ItemResponse>(await SendJson(alice, HttpMethod.Post, $"/lists/{list.Id}/items/{item.Id}/complete"));
        Assert.True(completed.Completed);
        Assert.Equal("alice@x.test", completed.CompletedBy?.Email);

        var reopened = await ReadAsync<ItemResponse>(await SendJson(alice, HttpMethod.Post, $"/lists/{list.Id}/items/{item.Id}/reopen"));
        Assert.False(reopened.Completed);

        var del = await SendJson(alice, HttpMethod.Delete, $"/lists/{list.Id}/items/{item.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/lists/{list.Id}/items/{item.Id}")).StatusCode);
        var live = await ReadAsync<ItemCollectionResponse>(await alice.GetAsync($"/lists/{list.Id}/items"));
        Assert.DoesNotContain(live.Items, i => i.Id == item.Id);
    }
}
