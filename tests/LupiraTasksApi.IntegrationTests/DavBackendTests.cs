using LupiraTasksApi.Dav;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>
/// The /dav-backend contract as the LupiraDavApi gateway consumes it: collection listing (the caller's
/// lists as TodoList collections), query/multiget, VTODO round-trip with version ETags, PUT/DELETE with
/// preconditions, and the sync-token changes feed with tombstones. HTTP-level promotion of the
/// service-level coverage in <see cref="TaskDavServiceTests"/>.
/// </summary>
public sealed class DavBackendTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    private const string Email = "alice@example.com";

    private static string Base(string email = Email) => $"/dav-backend/u/{Uri.EscapeDataString(email)}";

    [Fact]
    public async Task Collections_list_the_callers_lists_as_todo_collections()
    {
        var api = Factory.ApiClient(Email);
        var list = await CreateListAsync(api, "Groceries");

        var resp = await api.GetAsync($"{Base()}/collections");
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<DavCollectionsDto>();

        var col = Assert.Single(dto!.Collections);
        Assert.Equal(list.Id, col.Id);
        Assert.Equal(DavCollectionKind.TodoList, col.Kind);
        Assert.Equal("Groceries", col.DisplayName);
        Assert.StartsWith("seq-", col.Ctag);
    }

    [Fact]
    public async Task Put_get_roundtrip_returns_the_vtodo_with_a_version_etag()
    {
        var api = Factory.ApiClient(Email);
        var list = await CreateListAsync(api);

        var put = await PutVtodoAsync(api, list.Id, "todo-1@x", MinimalVtodo("todo-1@x", "Buy milk"));
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        var etag = put.Headers.ETag!.Tag.Trim('"');

        var get = await api.GetAsync($"{Base()}/collections/{list.Id}/resources/todo-1@x");
        get.EnsureSuccessStatusCode();
        Assert.Equal(etag, get.Headers.ETag!.Tag.Trim('"'));
        var body = await get.Content.ReadAsStringAsync();
        Assert.Contains("SUMMARY:Buy milk", body);
        Assert.Contains("UID:todo-1@x", body);
    }

    [Fact]
    public async Task Query_lists_uids_and_multiget_includes_content()
    {
        var api = Factory.ApiClient(Email);
        var list = await CreateListAsync(api);
        await PutVtodoAsync(api, list.Id, "a@x", MinimalVtodo("a@x", "A"));
        await PutVtodoAsync(api, list.Id, "b@x", MinimalVtodo("b@x", "B"));

        var listing = await api.PostAsJsonAsync($"{Base()}/collections/{list.Id}/query", new DavQueryRequest());
        var all = (await listing.Content.ReadFromJsonAsync<DavResourcesDto>())!.Resources;
        Assert.Equal(2, all.Count);
        Assert.All(all, r => Assert.Null(r.Content));

        var multiget = await api.PostAsJsonAsync($"{Base()}/collections/{list.Id}/query",
            new DavQueryRequest { Uids = ["a@x"], IncludeContent = true });
        var one = Assert.Single((await multiget.Content.ReadFromJsonAsync<DavResourcesDto>())!.Resources);
        Assert.Equal("a@x", one.Uid);
        Assert.Contains("SUMMARY:A", one.Content);
    }

    [Fact]
    public async Task Put_preconditions_guard_create_and_update()
    {
        var api = Factory.ApiClient(Email);
        var list = await CreateListAsync(api);
        var vtodo = MinimalVtodo("c@x", "Original");

        var create = await PutVtodoAsync(api, list.Id, "c@x", vtodo, ifNoneMatchStar: true);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var etag = create.Headers.ETag!.Tag.Trim('"');

        Assert.Equal(HttpStatusCode.PreconditionFailed,
            (await PutVtodoAsync(api, list.Id, "c@x", vtodo, ifNoneMatchStar: true)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed,
            (await PutVtodoAsync(api, list.Id, "c@x", MinimalVtodo("c@x", "Renamed"), ifMatch: "999")).StatusCode);

        var update = await PutVtodoAsync(api, list.Id, "c@x", MinimalVtodo("c@x", "Renamed"), ifMatch: etag);
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        Assert.NotEqual(etag, update.Headers.ETag!.Tag.Trim('"'));   // Version bumped
    }

    [Fact]
    public async Task Changes_diff_and_tombstone_deletes()
    {
        var api = Factory.ApiClient(Email);
        var list = await CreateListAsync(api);
        await PutVtodoAsync(api, list.Id, "keep@x", MinimalVtodo("keep@x", "Keep"));
        await PutVtodoAsync(api, list.Id, "gone@x", MinimalVtodo("gone@x", "Gone"));

        var full = await ChangesAsync(api, list.Id, null);
        Assert.Equal(2, full.Changed.Count);
        Assert.Empty(full.Deleted);

        Assert.Equal(HttpStatusCode.NoContent,
            (await api.DeleteAsync($"{Base()}/collections/{list.Id}/resources/gone@x")).StatusCode);
        var diff = await ChangesAsync(api, list.Id, full.SyncToken);
        Assert.Contains("gone@x", diff.Deleted);
        Assert.DoesNotContain(diff.Changed, c => c.Uid == "gone@x");

        // Unknown token degrades to the full live listing (self-healing resync).
        var healed = await ChangesAsync(api, list.Id, "garbage");
        Assert.Single(healed.Changed, c => c.Uid == "keep@x");
        Assert.Empty(healed.Deleted);
    }

    [Fact]
    public async Task Non_members_get_an_opaque_404_and_anonymous_a_401()
    {
        var alice = Factory.ApiClient(Email);
        var list = await CreateListAsync(alice);

        var mallory = Factory.ApiClient("mallory@example.com");
        Assert.Equal(HttpStatusCode.NotFound,
            (await mallory.PostAsJsonAsync($"{Base("mallory@example.com")}/collections/{list.Id}/query", new DavQueryRequest())).StatusCode);
        Assert.Empty((await (await mallory.GetAsync($"{Base("mallory@example.com")}/collections")).Content
            .ReadFromJsonAsync<DavCollectionsDto>())!.Collections);

        var anon = Factory.AnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"{Base()}/collections")).StatusCode);
    }

    private static async Task<HttpResponseMessage> PutVtodoAsync(
        HttpClient client, Guid listId, string uid, string vtodo, string? ifMatch = null, bool ifNoneMatchStar = false)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, $"{Base()}/collections/{listId}/resources/{uid}");
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        if (ifNoneMatchStar) req.Headers.TryAddWithoutValidation("If-None-Match", "*");
        req.Content = new StringContent(vtodo, Encoding.UTF8, "text/calendar");
        return await client.SendAsync(req);
    }

    private static async Task<DavChangesDto> ChangesAsync(HttpClient api, Guid listId, string? since)
    {
        var url = $"{Base()}/collections/{listId}/changes" + (since is null ? "" : $"?since={Uri.EscapeDataString(since)}");
        var resp = await api.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<DavChangesDto>())!;
    }
}
