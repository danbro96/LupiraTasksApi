using System.Net;
using LupiraTasksApi.Core.Dtos.Items;
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

        var fetched = await ReadAsync<ItemDto>(await alice.GetAsync($"/lists/{list.Id}/items/{item.Id}"));
        Assert.False(fetched.Completed);

        var completed = await ReadAsync<ItemDto>(await SendJson(alice, HttpMethod.Post, $"/lists/{list.Id}/items/{item.Id}/complete"));
        Assert.True(completed.Completed);
        Assert.Equal("alice@x.test", completed.CompletedBy?.Email);

        var reopened = await ReadAsync<ItemDto>(await SendJson(alice, HttpMethod.Post, $"/lists/{list.Id}/items/{item.Id}/reopen"));
        Assert.False(reopened.Completed);

        var del = await SendJson(alice, HttpMethod.Delete, $"/lists/{list.Id}/items/{item.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/lists/{list.Id}/items/{item.Id}")).StatusCode);
        var live = await ReadAsync<List<ItemDto>>(await alice.GetAsync($"/lists/{list.Id}/items"));
        Assert.DoesNotContain(live, i => i.Id == item.Id);
    }
}
