using System.Net;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Dtos.Lists;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>Archive / restore / delete and the soft-delete filtering behind the `?archived=` query — through HTTP.</summary>
public sealed class ListLifecycleTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    [Fact]
    public async Task Agent_kind_is_set_at_creation_and_round_trips()
    {
        var agent = Factory.ApiClient("agent@x.test");
        var list = await CreateListAsync(agent, name: "Assistant backlog", kind: ListKind.Agent);
        Assert.Equal(ListKind.Agent, list.Kind);

        var byId = await ReadAsync<ListResponse>(await agent.GetAsync($"/lists/{list.Id}"));
        Assert.Equal(ListKind.Agent, byId.Kind);

        var collection = await ReadAsync<ListCollectionResponse>(await agent.GetAsync("/lists"));
        Assert.Equal(ListKind.Agent, collection.Lists.Single(l => l.Id == list.Id).Kind);
    }

    private static async Task<bool> ListVisible(HttpClient api, Guid listId, bool archived)
    {
        var resp = await ReadAsync<ListCollectionResponse>(await api.GetAsync($"/lists?archived={archived.ToString().ToLowerInvariant()}"));
        return resp.Lists.Any(l => l.Id == listId);
    }

    [Fact]
    public async Task Simple_priority_defaults_true_and_can_be_toggled()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        Assert.True(list.SimplePriority);

        var updated = await ReadAsync<ListResponse>(await SendJson(alice, HttpMethod.Patch, $"/lists/{list.Id}",
            new UpdateListRequest { SimplePriority = false }));
        Assert.False(updated.SimplePriority);

        var reloaded = await ReadAsync<ListResponse>(await alice.GetAsync($"/lists/{list.Id}"));
        Assert.False(reloaded.SimplePriority);
    }

    [Fact]
    public async Task Created_list_is_active_not_archived()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);

        Assert.True(await ListVisible(alice, list.Id, archived: false));
        Assert.False(await ListVisible(alice, list.Id, archived: true));
    }

    [Fact]
    public async Task Archive_hides_from_active_and_restore_brings_it_back()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);

        await SendJson(alice, HttpMethod.Post, $"/lists/{list.Id}/archive");
        Assert.False(await ListVisible(alice, list.Id, archived: false));
        Assert.True(await ListVisible(alice, list.Id, archived: true));

        await SendJson(alice, HttpMethod.Post, $"/lists/{list.Id}/restore");
        Assert.True(await ListVisible(alice, list.Id, archived: false));
        Assert.False(await ListVisible(alice, list.Id, archived: true));
    }

    [Fact]
    public async Task Delete_removes_from_all_queries()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);

        var del = await SendJson(alice, HttpMethod.Delete, $"/lists/{list.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        Assert.False(await ListVisible(alice, list.Id, archived: false));
        Assert.False(await ListVisible(alice, list.Id, archived: true));
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync($"/lists/{list.Id}")).StatusCode);
    }
}
