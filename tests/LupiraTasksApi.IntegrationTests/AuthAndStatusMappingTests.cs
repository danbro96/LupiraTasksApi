using System.Net;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Dtos.Lists;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>
/// The auth + <c>OpResult</c>→HTTP-status contract, exercised through the real pipeline (routing → auth →
/// handler → status mapping). This is the layer the service/store-level tests never touch.
/// </summary>
public sealed class AuthAndStatusMappingTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    [Fact]
    public async Task Unauthenticated_request_is_401()
    {
        var anon = Factory.CreateClient(); // no X-Dev-User
        var resp = await anon.GetAsync("/lists");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Non_member_read_is_404_not_403()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);

        var bob = Factory.ApiClient("bob@x.test");
        var resp = await bob.GetAsync($"/lists/{list.Id}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode); // existence not leaked
    }

    [Fact]
    public async Task Member_but_not_owner_role_change_is_403()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        await SendJson(alice, HttpMethod.Post, $"/lists/{list.Id}/members",
            new AddMemberRequest { Email = "bob@x.test", Role = ListRole.Editor });

        var bob = Factory.ApiClient("bob@x.test");
        var resp = await SendJson(bob, HttpMethod.Patch, $"/lists/{list.Id}/members/alice@x.test",
            new UpdateMemberRoleRequest { Role = ListRole.Viewer });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Invalid_body_is_400_problem()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var resp = await SendJson(alice, HttpMethod.Post, "/lists",
            new CreateListRequest { Id = Guid.CreateVersion7(), Name = "", Kind = ListKind.Todo });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("application/problem+json", resp.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task Health_probes_are_anonymous_and_ok()
    {
        var anon = Factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/livez")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/readyz")).StatusCode);
    }
}
