using LupiraTasksApi.Application;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Items;
using LupiraTasksApi.Domain.Lists;
using Marten;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>
/// Exercises the CalDAV write path (<see cref="TaskDavService"/>) store-level against the shared real Postgres:
/// a VTODO PUT lands as an event-sourced <c>Item</c>, ETag preconditions enforce concurrency, and — the key
/// guarantee — a DAV write is immediately visible on the JSON/Item surface (one source of truth).
/// </summary>
public sealed class TaskDavServiceTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    private const string RawVtodo =
        "BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//phone//EN\n" +
        "BEGIN:VTODO\nUID:dav-uid-1\nSUMMARY:Buy milk\nDESCRIPTION:2 litres\n" +
        "END:VTODO\nEND:VCALENDAR\n";

    private static Caller Alice => Caller.Member("alice@x.test", []);
    private static Caller Stranger => Caller.Member("mallory@x.test", []);

    private async Task<Guid> SeedListAsync()
    {
        var listId = Guid.CreateVersion7();
        await using var s = Store.LightweightSession();
        s.Events.StartStream<TodoList>(listId, new ListCreated(listId, "Groceries", ListKind.Todo, null, "alice@x.test"));
        await s.SaveChangesAsync();
        return listId;
    }

    private async Task<T> WithDav<T>(Func<TaskDavService, Task<T>> f)
    {
        await using var s = Store.LightweightSession();
        var dav = new TaskDavService(s, new AccessResolver(s));
        return await f(dav);
    }

    [Fact]
    public async Task Put_creates_a_vtodo_visible_on_the_item_surface()
    {
        var listId = await SeedListAsync();

        var put = await WithDav(d => d.PutVtodoAsync(Alice, listId, "dav-uid-1", RawVtodo, ifMatch: null, ifNoneMatchStar: false, CancellationToken.None));
        Assert.Equal(OpStatus.Ok, put.Status);
        Assert.True(put.Value.Created);

        // Cross-surface: the DAV write is a normal Item, queryable by the JSON/Item surface.
        await using var q = Store.QuerySession();
        var items = await q.Query<Item>().Where(i => i.ListId == listId && !i.Deleted).ToListAsync();
        var item = Assert.Single(items);
        Assert.Equal("dav-uid-1", item.Uid);
        Assert.Equal("Buy milk", item.Title);
        Assert.Equal("2 litres", item.Notes);
        Assert.Equal("alice@x.test", item.CreatedBy);
        Assert.False(item.Completed);
    }

    [Fact]
    public async Task Put_with_priority_is_visible_on_the_item_surface()
    {
        var listId = await SeedListAsync();
        const string withPriority =
            "BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//phone//EN\n" +
            "BEGIN:VTODO\nUID:dav-uid-2\nSUMMARY:Pay rent\nPRIORITY:2\n" +
            "END:VTODO\nEND:VCALENDAR\n";

        var put = await WithDav(d => d.PutVtodoAsync(Alice, listId, "dav-uid-2", withPriority, ifMatch: null, ifNoneMatchStar: false, CancellationToken.None));
        Assert.Equal(OpStatus.Ok, put.Status);

        await using var q = Store.QuerySession();
        var item = Assert.Single(await q.Query<Item>().Where(i => i.ListId == listId && !i.Deleted).ToListAsync());
        Assert.Equal(2, item.Priority);
    }

    [Fact]
    public async Task Put_with_if_none_match_star_conflicts_when_the_resource_exists()
    {
        var listId = await SeedListAsync();
        await WithDav(d => d.PutVtodoAsync(Alice, listId, "dav-uid-1", RawVtodo, null, false, CancellationToken.None));

        var second = await WithDav(d => d.PutVtodoAsync(Alice, listId, "dav-uid-1", RawVtodo, ifMatch: null, ifNoneMatchStar: true, CancellationToken.None));
        Assert.Equal(OpStatus.Conflict, second.Status);
    }

    [Fact]
    public async Task Update_requires_a_matching_if_match_etag()
    {
        var listId = await SeedListAsync();
        var created = await WithDav(d => d.PutVtodoAsync(Alice, listId, "dav-uid-1", RawVtodo, null, false, CancellationToken.None));
        var etag = created.Value.Etag;

        // Stale ETag → 412 Conflict.
        var stale = await WithDav(d => d.PutVtodoAsync(Alice, listId, "dav-uid-1", RawVtodo, ifMatch: "999", ifNoneMatchStar: false, CancellationToken.None));
        Assert.Equal(OpStatus.Conflict, stale.Status);

        // Correct ETag → update succeeds and the version (ETag) advances.
        var ok = await WithDav(d => d.PutVtodoAsync(Alice, listId, "dav-uid-1", RawVtodo, ifMatch: etag, ifNoneMatchStar: false, CancellationToken.None));
        Assert.Equal(OpStatus.Ok, ok.Status);
        Assert.False(ok.Value.Created);
        Assert.NotEqual(etag, ok.Value.Etag);
    }

    [Fact]
    public async Task Non_member_cannot_write_and_does_not_leak_existence()
    {
        var listId = await SeedListAsync();
        var result = await WithDav(d => d.PutVtodoAsync(Stranger, listId, "dav-uid-1", RawVtodo, null, false, CancellationToken.None));
        Assert.Equal(OpStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Delete_tombstones_the_item()
    {
        var listId = await SeedListAsync();
        await WithDav(d => d.PutVtodoAsync(Alice, listId, "dav-uid-1", RawVtodo, null, false, CancellationToken.None));

        var del = await WithDav(d => d.DeleteByUidAsync(Alice, listId, "dav-uid-1", ifMatch: null, CancellationToken.None));
        Assert.Equal(OpStatus.Ok, del.Status);

        var found = await WithDav(d => d.FindAsync(listId, "dav-uid-1", CancellationToken.None));
        Assert.Null(found);
    }
}
