using System.Linq;
using System.Net;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Dtos.Lists;
using LupiraTasksApi.Dtos.Shares;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>
/// Member-side share redemption (<c>POST /shares/redeem</c>): an authenticated caller "cashes in" a
/// link to join the list — access→role mapping, idempotency, and rejection of dead tokens / anonymous callers.
/// </summary>
public sealed class ShareRedeemTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    [Fact]
    public async Task ReadWrite_link_joins_caller_as_editor()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var link = await MintShareLinkAsync(alice, list.Id, ShareAccess.ReadWrite);

        var bob = Factory.ApiClient("bob@x.test");
        var resp = await SendJson(bob, HttpMethod.Post, "/shares/redeem", new RedeemShareRequest { Token = link.Token });
        resp.EnsureSuccessStatusCode();
        var redeemed = await ReadAsync<RedeemShareResponse>(resp);

        Assert.Equal(list.Id, redeemed.ListId);
        Assert.Equal(ListRole.Editor, redeemed.Role);

        // Bob is now a member: a previously-forbidden GET on the list now succeeds.
        var view = await ReadAsync<ListResponse>(await bob.GetAsync($"/lists/{list.Id}"));
        Assert.Contains(view.Members, m => m.Email == "bob@x.test" && m.Role == ListRole.Editor);
    }

    [Fact]
    public async Task Read_link_joins_caller_as_viewer()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var link = await MintShareLinkAsync(alice, list.Id, ShareAccess.Read);

        var bob = Factory.ApiClient("bob@x.test");
        var redeemed = await ReadAsync<RedeemShareResponse>(
            await SendJson(bob, HttpMethod.Post, "/shares/redeem", new RedeemShareRequest { Token = link.Token }));

        Assert.Equal(ListRole.Viewer, redeemed.Role);
    }

    [Fact]
    public async Task Redeeming_twice_is_idempotent_with_no_duplicate_membership()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var link = await MintShareLinkAsync(alice, list.Id, ShareAccess.ReadWrite);

        var bob = Factory.ApiClient("bob@x.test");
        await SendJson(bob, HttpMethod.Post, "/shares/redeem", new RedeemShareRequest { Token = link.Token });
        var again = await SendJson(bob, HttpMethod.Post, "/shares/redeem", new RedeemShareRequest { Token = link.Token });
        again.EnsureSuccessStatusCode();
        Assert.Equal(ListRole.Editor, (await ReadAsync<RedeemShareResponse>(again)).Role);

        var view = await ReadAsync<ListResponse>(await bob.GetAsync($"/lists/{list.Id}"));
        Assert.Equal(1, view.Members.Count(m => m.Email == "bob@x.test"));
    }

    [Fact]
    public async Task Revoked_link_cannot_be_redeemed()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var link = await MintShareLinkAsync(alice, list.Id, ShareAccess.ReadWrite);
        await SendJson(alice, HttpMethod.Delete, $"/lists/{list.Id}/shares/{link.ShareId}");

        var bob = Factory.ApiClient("bob@x.test");
        var resp = await SendJson(bob, HttpMethod.Post, "/shares/redeem", new RedeemShareRequest { Token = link.Token });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Redeem_requires_authentication()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        var link = await MintShareLinkAsync(alice, list.Id, ShareAccess.ReadWrite);

        var anon = Factory.CreateClient();
        var resp = await SendJson(anon, HttpMethod.Post, "/shares/redeem", new RedeemShareRequest { Token = link.Token });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
