using System.Net;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Items;
using LupiraTasksApi.Dtos.Items;
using Marten;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>
/// The share-link lifecycle over HTTP — the largest previously-untested surface: mint → consume (emails stripped)
/// → account-less write attributed <c>share:{label}</c> → read-only rejection → immediate revocation.
/// </summary>
public sealed class ShareLinkE2ETests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    [Fact]
    public async Task Shared_view_strips_member_emails()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        await CreateItemAsync(alice, list.Id, "Buy milk");
        var link = await MintShareLinkAsync(alice, list.Id, ShareAccess.Read);

        var anon = Factory.CreateClient();
        var resp = await anon.GetAsync($"/shared/{link.Token}");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Contains("Buy milk", body);
        Assert.DoesNotContain("alice@x.test", body); // a public link must not leak family emails
    }

    [Fact]
    public async Task Read_write_link_can_add_an_item_attributed_to_the_share_label()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var link = await MintShareLinkAsync(alice, list.Id, ShareAccess.ReadWrite, label: "fridge");

        var itemId = Guid.CreateVersion7();
        var anon = Factory.CreateClient();
        var resp = await SendJson(anon, HttpMethod.Post, $"/shared/{link.Token}/items",
            new CreateItemRequest { Id = itemId, Title = "Eggs", SortOrder = "a0" });
        resp.EnsureSuccessStatusCode();

        await using var q = Store.QuerySession();
        var item = await q.LoadAsync<Item>(itemId);
        Assert.NotNull(item);
        Assert.Equal("share:fridge", item!.CreatedBy); // attribution distinguishes a link write from a member
    }

    [Fact]
    public async Task Read_only_link_cannot_write()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var link = await MintShareLinkAsync(alice, list.Id, ShareAccess.Read);

        var anon = Factory.CreateClient();
        var resp = await SendJson(anon, HttpMethod.Post, $"/shared/{link.Token}/items",
            new CreateItemRequest { Id = Guid.CreateVersion7(), Title = "Nope", SortOrder = "a0" });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Revoking_a_link_rejects_the_next_request_immediately()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var link = await MintShareLinkAsync(alice, list.Id, ShareAccess.Read);

        var revoke = await SendJson(alice, HttpMethod.Delete, $"/lists/{list.Id}/shares/{link.ShareId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        var anon = Factory.CreateClient();
        var resp = await anon.GetAsync($"/shared/{link.Token}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
