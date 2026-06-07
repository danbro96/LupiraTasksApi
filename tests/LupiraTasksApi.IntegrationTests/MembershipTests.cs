using System.Security.Claims;
using JasperFx;
using LupiraTasksApi;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Lists;
using LupiraTasksApi.Handlers;
using LupiraTasksApi.Models.Lists;
using LupiraTasksApi.Services;
using Marten;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>
/// Drives <see cref="ListsHandler"/> against a REAL Postgres to cover the new membership flows —
/// especially the **last-owner-leave → auto-delete** cascade, which is handler logic the unit
/// tests can't reach. Each handler call uses a FRESH session (as a scoped HTTP request would).
/// </summary>
public sealed class MembershipTests : IAsyncLifetime
{
    private static string ConnString =>
        Environment.GetEnvironmentVariable("TASKS_IT_DB")
        ?? "Host=localhost;Port=5433;Database=tasks_db;Username=tasks;Password=devpw";

    private DocumentStore _store = null!;

    public Task InitializeAsync()
    {
        _store = DocumentStore.For(opts =>
        {
            opts.Connection(ConnString);
            opts.DatabaseSchemaName = "tasks_it";
            opts.UseSystemTextJsonForSerialization();
            opts.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
            MartenRegistrations.Configure(opts);
        });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _store?.Dispose();
        return Task.CompletedTask;
    }

    private static HttpContext CtxFor(string email)
    {
        var identity = new ClaimsIdentity(new[] { new Claim("email", email) }, "test", "email", "groups");
        return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    private async Task<Guid> SeedListAsync(string owner)
    {
        var listId = Guid.CreateVersion7();
        await using var s = _store.LightweightSession();
        s.SetHeader(EventActor.HeaderKey, owner);
        s.Events.StartStream<TodoList>(listId, new ListCreated(listId, "L", ListKind.Todo, null, owner));
        await s.SaveChangesAsync();
        return listId;
    }

    /// <summary>Run a handler action with a fresh session (mirrors a scoped HTTP request).</summary>
    private async Task<TResult> WithHandlerAsync<TResult>(string actor, Func<ListsHandler, HttpContext, Task<TResult>> action)
    {
        await using var s = _store.LightweightSession();
        var ctx = CtxFor(actor);
        var handler = new ListsHandler(s, new CurrentUser(new HttpContextAccessor { HttpContext = ctx }), new AccessResolver(s), new Idempotency(s));
        return await action(handler, ctx);
    }

    private async Task<TodoList?> LoadAsync(Guid listId)
    {
        await using var q = _store.QuerySession();
        return await q.LoadAsync<TodoList>(listId);
    }

    [Fact]
    public async Task Add_member_then_change_role_updates_the_snapshot()
    {
        var listId = await SeedListAsync("owner@x");

        await WithHandlerAsync("owner@x", (h, ctx) =>
            h.AddMemberAsync(ctx, listId, new AddMemberRequest { Email = "bob@x" }, CancellationToken.None));
        var list = await LoadAsync(listId);
        Assert.Contains(list!.Members, m => m.Email == "bob@x" && m.Role == ListRole.Editor);

        await WithHandlerAsync("owner@x", (h, ctx) =>
            h.ChangeMemberRoleAsync(ctx, listId, "bob@x", new UpdateMemberRoleRequest { Role = ListRole.Owner }, CancellationToken.None));
        list = await LoadAsync(listId);
        Assert.Contains(list!.Members, m => m.Email == "bob@x" && m.Role == ListRole.Owner);
    }

    [Fact]
    public async Task Last_owner_leaving_auto_deletes_the_list()
    {
        var listId = await SeedListAsync("owner@x");

        // An editor remains, but the only OWNER leaves → cascade delete.
        await WithHandlerAsync("owner@x", (h, ctx) =>
            h.AddMemberAsync(ctx, listId, new AddMemberRequest { Email = "bob@x", Role = ListRole.Editor }, CancellationToken.None));
        await WithHandlerAsync("owner@x", (h, ctx) =>
            h.RemoveMemberAsync(ctx, listId, "owner@x", CancellationToken.None));

        var list = await LoadAsync(listId);
        Assert.True(list!.IsDeleted);
    }

    [Fact]
    public async Task Non_last_owner_leaving_keeps_the_list()
    {
        var listId = await SeedListAsync("owner@x");

        await WithHandlerAsync("owner@x", (h, ctx) =>
            h.AddMemberAsync(ctx, listId, new AddMemberRequest { Email = "bob@x", Role = ListRole.Owner }, CancellationToken.None));
        await WithHandlerAsync("owner@x", (h, ctx) =>
            h.RemoveMemberAsync(ctx, listId, "owner@x", CancellationToken.None));

        var list = await LoadAsync(listId);
        Assert.False(list!.IsDeleted);
        Assert.DoesNotContain(list.Members, m => m.Email == "owner@x");
        Assert.Contains(list.Members, m => m.Email == "bob@x" && m.Role == ListRole.Owner);
    }

    [Fact]
    public async Task Demoting_the_last_owner_is_rejected()
    {
        var listId = await SeedListAsync("owner@x");

        await WithHandlerAsync("owner@x", (h, ctx) =>
            h.AddMemberAsync(ctx, listId, new AddMemberRequest { Email = "bob@x", Role = ListRole.Editor }, CancellationToken.None));
        var result = await WithHandlerAsync("owner@x", (h, ctx) =>
            h.ChangeMemberRoleAsync(ctx, listId, "owner@x", new UpdateMemberRoleRequest { Role = ListRole.Editor }, CancellationToken.None));

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>(result.Result);
        var list = await LoadAsync(listId);
        Assert.Contains(list!.Members, m => m.Email == "owner@x" && m.Role == ListRole.Owner);
    }
}
