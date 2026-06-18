using LupiraTasksApi.Domain;
using LupiraTasksApi.Dtos.Lists;
using LupiraTasksApi.Dtos.Me;
using LupiraTasksApi.Dtos.Users;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>`GET /me` provisioning + admin-group resolution, and the `/users/directory` people search.</summary>
public sealed class MeAndDirectoryTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    [Fact]
    public async Task Me_returns_the_caller_and_resolves_admin_from_groups()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var me = await ReadAsync<MeResponse>(await alice.GetAsync("/me"));
        Assert.Equal("alice@x.test", me.Email);
        Assert.False(me.IsAdmin);

        var admin = Factory.ApiClient("root@x.test", "platform-admins");
        var adminMe = await ReadAsync<MeResponse>(await admin.GetAsync("/me"));
        Assert.True(adminMe.IsAdmin);
    }

    [Fact]
    public async Task Directory_lists_members_across_the_callers_lists_and_filters_by_q()
    {
        var alice = Factory.ApiClient("alice@x.test");
        var list = await CreateListAsync(alice);
        await SendJson(alice, HttpMethod.Post, $"/lists/{list.Id}/members", new AddMemberRequest { Email = "bob@x.test" });

        var all = await ReadAsync<DirectoryResponse>(await alice.GetAsync("/users/directory"));
        Assert.Contains(all.People, p => p.Email == "bob@x.test");

        var filtered = await ReadAsync<DirectoryResponse>(await alice.GetAsync("/users/directory?q=bob"));
        Assert.Contains(filtered.People, p => p.Email == "bob@x.test");

        var miss = await ReadAsync<DirectoryResponse>(await alice.GetAsync("/users/directory?q=zzzznomatch"));
        Assert.DoesNotContain(miss.People, p => p.Email == "bob@x.test");
    }
}
