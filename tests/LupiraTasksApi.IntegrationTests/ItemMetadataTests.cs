using System.Text.Json.Nodes;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Dtos.Items;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>
/// Server-side item metadata: whole-field JSON round-trip + LWW over REST, and the isolation guarantee —
/// it is never exposed to a public share-link viewer.
/// </summary>
public sealed class ItemMetadataTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    private const string Email = "alice@x.test";
    private static readonly DateTimeOffset T0 = new(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int seconds) => T0.AddSeconds(seconds);

    private static Task<ItemResponse> SetMetadataAsync(HttpClient api, Guid listId, Guid itemId, JsonNode? metadata, DateTimeOffset? at = null) =>
        SendJson(api, HttpMethod.Post, $"/lists/{listId}/items/{itemId}/metadata",
            new SetMetadataRequest { Metadata = metadata, OccurredAt = at })
            .ContinueWith(t => ReadAsync<ItemResponse>(t.Result.EnsureSuccessStatusCode())).Unwrap();

    [Fact]
    public async Task Metadata_round_trips_and_can_be_cleared()
    {
        var api = Factory.ApiClient(Email);
        var list = await CreateListAsync(api);
        var item = await CreateItemAsync(api, list.Id);
        Assert.Null(item.Metadata);

        var set = await SetMetadataAsync(api, list.Id, item.Id, JsonNode.Parse("""{"alertId":"abc","checks":3}"""));
        Assert.Equal("""{"alertId":"abc","checks":3}""", set.Metadata!.ToJsonString());

        var reloaded = await ReadAsync<ItemResponse>(await api.GetAsync($"/lists/{list.Id}/items/{item.Id}"));
        Assert.Equal("""{"alertId":"abc","checks":3}""", reloaded.Metadata!.ToJsonString());

        var cleared = await SetMetadataAsync(api, list.Id, item.Id, null);
        Assert.Null(cleared.Metadata);
    }

    [Fact]
    public async Task Metadata_is_whole_field_lww()
    {
        var api = Factory.ApiClient(Email);
        var list = await CreateListAsync(api);
        var item = await CreateItemAsync(api, list.Id);

        await SetMetadataAsync(api, list.Id, item.Id, JsonNode.Parse("""{"checks":2}"""), at: At(20));
        var stale = await SetMetadataAsync(api, list.Id, item.Id, JsonNode.Parse("""{"checks":1}"""), at: At(10));

        Assert.Equal("""{"checks":2}""", stale.Metadata!.ToJsonString()); // older OccurredAt loses
    }

    [Fact]
    public async Task Metadata_is_not_exposed_to_a_share_link_viewer()
    {
        var api = Factory.ApiClient(Email);
        var list = await CreateListAsync(api);
        var item = await CreateItemAsync(api, list.Id, "Buy milk");
        await SetMetadataAsync(api, list.Id, item.Id, JsonNode.Parse("""{"secret":"alert-xyz"}"""));
        var link = await MintShareLinkAsync(api, list.Id, ShareAccess.Read);

        var anon = Factory.CreateClient();
        var body = await (await anon.GetAsync($"/shared/{link.Token}")).Content.ReadAsStringAsync();

        Assert.Contains("Buy milk", body);
        Assert.DoesNotContain("alert-xyz", body); // server-side bookkeeping must not leak to a public link
        Assert.DoesNotContain("metadata", body, StringComparison.OrdinalIgnoreCase);
    }
}
