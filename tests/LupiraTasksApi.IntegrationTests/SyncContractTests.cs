using System.Net;
using LupiraTasksApi.Dtos.Sync;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>The offline delta-pull contract (`GET /lists/{id}/sync`): full live snapshot + cursor, deleted excluded, member-gated.</summary>
public sealed class SyncContractTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    [Fact]
    public async Task Sync_returns_live_items_with_a_cursor_and_excludes_deleted()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var keep = await CreateItemAsync(alice, list.Id, "Keep", "a0");
        var drop = await CreateItemAsync(alice, list.Id, "Drop", "a1");
        await SendJson(alice, HttpMethod.Delete, $"/lists/{list.Id}/items/{drop.Id}");

        var sync = await ReadAsync<SyncResponse>(await alice.GetAsync($"/lists/{list.Id}/sync"));

        Assert.Equal(list.Id, sync.List.Id);
        Assert.Single(sync.Items);
        Assert.Equal(keep.Id, sync.Items[0].Id);
        Assert.True(sync.NextCursor > 0);
    }

    [Fact]
    public async Task Non_member_sync_is_404()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);

        var bob = Factory.ApiClient("bob@x.test");
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/lists/{list.Id}/sync")).StatusCode);
    }
}
