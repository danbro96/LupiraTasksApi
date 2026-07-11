using System.Net;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Dtos.Lists;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>
/// Membership + ownership invariants over HTTP (supersedes the old store-level MembershipTests): default role,
/// owner-only role changes, the last-owner-leave cascade-delete, and the keep-at-least-one-owner guard.
/// </summary>
public sealed class ListMembershipTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    private static async Task<ListResponse> Get(HttpClient api, Guid listId) =>
        await ReadAsync<ListResponse>(await api.GetAsync($"/lists/{listId}"));

    [Fact]
    public async Task Add_member_defaults_to_editor_then_role_change_sticks()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);

        await SendJson(alice, HttpMethod.Post, $"/lists/{list.Id}/members", new AddMemberRequest { Email = "bob@x.test" });
        var afterAdd = await Get(alice, list.Id);
        Assert.Contains(afterAdd.Members, m => m.Email == "bob@x.test" && m.Role == ListRole.Editor);
        var bobId = afterAdd.Members.First(m => m.Email == "bob@x.test").PrincipalId;

        await SendJson(alice, HttpMethod.Patch, $"/lists/{list.Id}/members/{bobId}", new UpdateMemberRoleRequest { Role = ListRole.Owner });
        var afterPromote = await Get(alice, list.Id);
        Assert.Contains(afterPromote.Members, m => m.Email == "bob@x.test" && m.Role == ListRole.Owner);
    }

    [Fact]
    public async Task Last_owner_leaving_deletes_the_list_for_everyone()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        await SendJson(alice, HttpMethod.Post, $"/lists/{list.Id}/members", new AddMemberRequest { Email = "bob@x.test", Role = ListRole.Editor });

        var aliceId = (await Get(alice, list.Id)).Members.First(m => m.Email == "alice@x.test").PrincipalId;
        var leave = await SendJson(alice, HttpMethod.Delete, $"/lists/{list.Id}/members/{aliceId}");
        Assert.Equal(HttpStatusCode.NoContent, leave.StatusCode);

        var bob = Factory.ApiClient("bob@x.test");
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/lists/{list.Id}")).StatusCode);
    }

    [Fact]
    public async Task Demoting_the_last_owner_is_rejected_400()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        await SendJson(alice, HttpMethod.Post, $"/lists/{list.Id}/members", new AddMemberRequest { Email = "bob@x.test", Role = ListRole.Editor });

        var aliceId = (await Get(alice, list.Id)).Members.First(m => m.Email == "alice@x.test").PrincipalId;
        var resp = await SendJson(alice, HttpMethod.Patch, $"/lists/{list.Id}/members/{aliceId}", new UpdateMemberRoleRequest { Role = ListRole.Editor });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains((await Get(alice, list.Id)).Members, m => m.Email == "alice@x.test" && m.Role == ListRole.Owner);
    }
}
