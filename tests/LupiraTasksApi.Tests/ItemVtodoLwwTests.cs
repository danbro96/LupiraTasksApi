using LupiraTasksApi.Domain.Items;
using Xunit;

namespace LupiraTasksApi.Tests;

/// <summary>
/// LWW vectors for the CalDAV write event <see cref="ItemVtodoPut"/>. It must establish identity on
/// a new stream and otherwise compete field-by-field through the SAME (OccurredAt, CommandId) guards
/// as the granular REST/MCP events — so a phone PUT and a web edit converge regardless of order.
/// </summary>
public class ItemVtodoLwwTests
{
    private static readonly Guid ItemId = Guid.Parse("0190a000-0000-7000-8000-000000000101");
    private static readonly Guid ListId = Guid.Parse("0190a000-0000-7000-8000-000000000102");
    private static readonly Guid Cmd = Guid.Parse("0190a000-0000-7000-8000-0000000001c0");
    private static readonly DateTimeOffset T0 = new(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int s) => T0.AddSeconds(s);

    private static ItemVtodoPut Put(
        int atSeconds, Guid cmd, string title = "From phone", bool completed = false,
        IReadOnlyList<Guid>? tags = null, string uid = "phone-uid", string raw = "RAWVTODO") =>
        new(ItemId, ListId, uid, title, Notes: null, DueAt: null, completed,
            completed ? At(atSeconds) : null, tags ?? [], SortOrder: "~z", raw, At(atSeconds), cmd);

    [Fact]
    public void Put_on_a_new_stream_establishes_identity_and_fields()
    {
        var s = new ItemState();
        ItemLww.ApplyVtodoPut(s, Put(5, Cmd, title: "Walk dog", uid: "abc@phone", raw: "BLOB"), actor: "bob@x.test");

        Assert.Equal(ItemId, s.Id);
        Assert.Equal(ListId, s.ListId);
        Assert.Equal("abc@phone", s.Uid);            // client UID kept verbatim
        Assert.Equal("Walk dog", s.Title);
        Assert.Equal("bob@x.test", s.CreatedBy);
        Assert.Equal(At(5), s.CreatedAt);
        Assert.Equal("BLOB", s.SourceVtodo);
        Assert.False(s.Deleted);
    }

    [Fact]
    public void Newer_granular_rename_wins_over_an_older_put()
    {
        var s = new ItemState();
        ItemLww.ApplyVtodoPut(s, Put(5, Cmd, title: "phone title"), actor: "a@x.test");
        ItemLww.ApplyRenamed(s, new ItemRenamed(ItemId, "web title", At(10), Cmd));

        Assert.Equal("web title", s.Title);
    }

    [Fact]
    public void Older_granular_rename_does_not_clobber_a_newer_put()
    {
        var s = new ItemState();
        ItemLww.ApplyVtodoPut(s, Put(5, Cmd, title: "seed"), actor: "a@x.test");
        ItemLww.ApplyVtodoPut(s, Put(20, Cmd, title: "newer phone title"), actor: "a@x.test");
        ItemLww.ApplyRenamed(s, new ItemRenamed(ItemId, "stale web", At(10), Cmd));

        Assert.Equal("newer phone title", s.Title);
    }

    [Fact]
    public void Put_can_complete_then_reopen_via_a_later_put()
    {
        var s = new ItemState();
        ItemLww.ApplyVtodoPut(s, Put(5, Cmd, completed: true), actor: "a@x.test");
        Assert.True(s.Completed);

        ItemLww.ApplyVtodoPut(s, Put(10, Cmd, completed: false), actor: "a@x.test");
        Assert.False(s.Completed);
        Assert.Null(s.CompletedAt);
    }

    [Fact]
    public void Categories_reconcile_against_the_desired_set()
    {
        var tagA = Guid.Parse("0190a000-0000-7000-8000-0000000001a1");
        var tagB = Guid.Parse("0190a000-0000-7000-8000-0000000001a2");
        var s = new ItemState();

        ItemLww.ApplyVtodoPut(s, Put(5, Cmd, tags: [tagA, tagB]), actor: "a@x.test");
        Assert.Equal([tagA, tagB], s.Tags.OrderBy(g => g));

        // A newer PUT drops tagB.
        ItemLww.ApplyVtodoPut(s, Put(10, Cmd, tags: [tagA]), actor: "a@x.test");
        Assert.Equal([tagA], s.Tags);
    }

    [Fact]
    public void Tombstone_gates_a_later_put()
    {
        var s = new ItemState();
        ItemLww.ApplyVtodoPut(s, Put(5, Cmd, title: "alive"), actor: "a@x.test");
        ItemLww.ApplyDeleted(s, new ItemDeleted(ItemId, At(10), Cmd));

        ItemLww.ApplyVtodoPut(s, Put(20, Cmd, title: "resurrected?"), actor: "a@x.test");

        Assert.True(s.Deleted);
        Assert.Equal("alive", s.Title);   // delete wins; later field writes ignored
    }
}
