using System.Net;
using LupiraTasksApi.Dtos.Items;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>Item priority through HTTP (the primary surface): set on create, patch, clear, read back, and reject out-of-range.</summary>
public sealed class PriorityE2ETests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    [Fact]
    public async Task Priority_can_be_set_on_create_patched_and_cleared()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);

        // Create with a priority.
        var itemId = Guid.CreateVersion7();
        var createResp = await SendJson(alice, HttpMethod.Post, $"/lists/{list.Id}/items",
            new CreateItemRequest { Id = itemId, Title = "Buy milk", SortOrder = "a0", Priority = 3 });
        var created = await ReadAsync<ItemResponse>(createResp);
        Assert.Equal(3, created.Priority);

        // Patch the priority.
        var patched = await ReadAsync<ItemResponse>(await SendJson(alice, HttpMethod.Patch, $"/lists/{list.Id}/items/{itemId}",
            new UpdateItemRequest { Priority = 7, PriorityProvided = true }));
        Assert.Equal(7, patched.Priority);

        // GET reflects the patched value.
        var fetched = await ReadAsync<ItemResponse>(await alice.GetAsync($"/lists/{list.Id}/items/{itemId}"));
        Assert.Equal(7, fetched.Priority);

        // Clearing = setting back to 0 (the default / "none").
        var cleared = await ReadAsync<ItemResponse>(await SendJson(alice, HttpMethod.Patch, $"/lists/{list.Id}/items/{itemId}",
            new UpdateItemRequest { Priority = 0, PriorityProvided = true }));
        Assert.Equal(0, cleared.Priority);
    }

    [Fact]
    public async Task Default_priority_is_zero()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var item = await CreateItemAsync(alice, list.Id, "No priority");
        Assert.Equal(0, item.Priority);
    }

    [Fact]
    public async Task Out_of_range_priority_is_rejected()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);

        var badCreate = await SendJson(alice, HttpMethod.Post, $"/lists/{list.Id}/items",
            new CreateItemRequest { Id = Guid.CreateVersion7(), Title = "Too eager", SortOrder = "a0", Priority = 12 });
        Assert.Equal(HttpStatusCode.BadRequest, badCreate.StatusCode);

        var item = await CreateItemAsync(alice, list.Id, "Buy milk");
        var badPatch = await SendJson(alice, HttpMethod.Patch, $"/lists/{list.Id}/items/{item.Id}",
            new UpdateItemRequest { Priority = 10, PriorityProvided = true });
        Assert.Equal(HttpStatusCode.BadRequest, badPatch.StatusCode);
    }
}
