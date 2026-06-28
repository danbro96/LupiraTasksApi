using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LupiraTasksApi.Dtos.Relations;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>
/// Cross-API relations over REST: create → list → delete, plus the idempotency the spec requires
/// (re-link is a no-op, re-unlink is a no-op) and the membership/existence gating (404, never 403).
/// </summary>
public sealed class RelationsTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    private const string Email = "alice@x.test";

    private static CreateRelationRequest Monitor(string toRef = "cal-item-1", JsonNode? metadata = null) => new()
    {
        ToKind = "cal-item",
        ToRef = toRef,
        RelationType = "monitors",
        Metadata = metadata,
    };

    private static string RelationsUrl(Guid listId, Guid itemId) => $"/lists/{listId}/items/{itemId}/relations";

    [Fact]
    public async Task Link_list_and_unlink_round_trip()
    {
        var api = Factory.ApiClient(Email);
        var list = await CreateListAsync(api);
        var item = await CreateItemAsync(api, list.Id);

        var metadata = JsonNode.Parse("""{"note":"release watch","checks":0}""");
        var link = await SendJson(api, HttpMethod.Post, RelationsUrl(list.Id, item.Id), Monitor(metadata: metadata));
        link.EnsureSuccessStatusCode();
        var dto = await ReadAsync<RelationDto>(link);
        Assert.Equal("task", dto.FromKind);
        Assert.Equal(item.Id, dto.FromId);
        Assert.Equal("cal-item", dto.ToKind);
        Assert.Equal("monitors", dto.RelationType);
        Assert.Equal(metadata!.ToJsonString(), dto.Metadata!.ToJsonString());

        var listed = await ReadAsync<List<RelationDto>>(await api.GetAsync(RelationsUrl(list.Id, item.Id)));
        Assert.Single(listed);
        Assert.Equal(dto.Id, listed[0].Id);

        var del = await SendJson(api, HttpMethod.Delete,
            $"{RelationsUrl(list.Id, item.Id)}?toKind=cal-item&toRef=cal-item-1&relationType=monitors");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var afterDelete = await ReadAsync<List<RelationDto>>(await api.GetAsync(RelationsUrl(list.Id, item.Id)));
        Assert.Empty(afterDelete);
    }

    [Fact]
    public async Task Re_linking_the_same_edge_is_idempotent_and_refreshes_metadata()
    {
        var api = Factory.ApiClient(Email);
        var list = await CreateListAsync(api);
        var item = await CreateItemAsync(api, list.Id);

        var first = await ReadAsync<RelationDto>(
            await SendJson(api, HttpMethod.Post, RelationsUrl(list.Id, item.Id), Monitor()));
        var second = await ReadAsync<RelationDto>(
            await SendJson(api, HttpMethod.Post, RelationsUrl(list.Id, item.Id),
                Monitor(metadata: JsonNode.Parse("""{"checks":3}"""))));

        Assert.Equal(first.Id, second.Id); // same edge tuple → same deterministic id
        var listed = await ReadAsync<List<RelationDto>>(await api.GetAsync(RelationsUrl(list.Id, item.Id)));
        Assert.Single(listed);
        Assert.Equal("""{"checks":3}""", listed[0].Metadata!.ToJsonString());
    }

    [Fact]
    public async Task Unlinking_a_missing_edge_is_a_no_op()
    {
        var api = Factory.ApiClient(Email);
        var list = await CreateListAsync(api);
        var item = await CreateItemAsync(api, list.Id);

        var del = await SendJson(api, HttpMethod.Delete,
            $"{RelationsUrl(list.Id, item.Id)}?toKind=url&toRef=never-linked&relationType=relates-to");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
    }

    [Fact]
    public async Task Link_on_a_missing_item_is_not_found()
    {
        var api = Factory.ApiClient(Email);
        var list = await CreateListAsync(api);

        var resp = await SendJson(api, HttpMethod.Post, RelationsUrl(list.Id, Guid.NewGuid()), Monitor());
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Link_as_a_non_member_is_not_found()
    {
        var alice = Factory.ApiClient(Email);
        var list = await CreateListAsync(alice);
        var item = await CreateItemAsync(alice, list.Id);

        var bob = Factory.ApiClient("bob@x.test");
        var resp = await SendJson(bob, HttpMethod.Post, RelationsUrl(list.Id, item.Id), Monitor());
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Link_with_a_blank_field_is_bad_request()
    {
        var api = Factory.ApiClient(Email);
        var list = await CreateListAsync(api);
        var item = await CreateItemAsync(api, list.Id);

        var resp = await SendJson(api, HttpMethod.Post, RelationsUrl(list.Id, item.Id),
            new CreateRelationRequest { ToKind = "cal-item", ToRef = "  ", RelationType = "monitors" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
