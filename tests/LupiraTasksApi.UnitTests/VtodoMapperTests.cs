using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Items;
using LupiraTasksApi.Domain.Lists;
using LupiraTasksApi.Ical;
using Xunit;

namespace LupiraTasksApi.UnitTests;

/// <summary>
/// Covers the <see cref="VtodoMapper"/> Item↔VTODO mapping (Ical.Net): the modeled-field round-trip,
/// completion/category mapping, and — critically — that GET regenerates from the snapshot while
/// preserving the unmodeled properties (PRIORITY/RRULE/X-*) carried in the last PUT's raw blob.
/// </summary>
public class VtodoMapperTests
{
    private static readonly DateTimeOffset Due = new(2026, 6, 20, 10, 0, 0, TimeSpan.Zero);

    private static Item ItemWith(Action<ItemState> cfg)
    {
        var s = new ItemState
        {
            Id = Guid.Parse("0190a000-0000-7000-8000-000000000001"),
            ListId = Guid.Parse("0190a000-0000-7000-8000-000000000002"),
            Uid = "task-uid-1",
            Title = "Buy milk",
            CreatedAt = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero),
        };
        cfg(s);
        return new Item { Id = s.Id, State = s };
    }

    [Fact]
    public void Modeled_fields_round_trip()
    {
        var item = ItemWith(s => { s.Notes = "2 litres"; s.DueAt = Due; });

        var raw = VtodoMapper.ToVtodo(item, [], sourceRaw: null);
        var parsed = VtodoMapper.Parse(raw);

        Assert.Equal("Buy milk", parsed.Title);
        Assert.Equal("2 litres", parsed.Notes);
        Assert.Equal(Due, parsed.DueAt);
        Assert.Equal(ItemStatus.Open, parsed.Status);
    }

    [Fact]
    public void Completed_item_serializes_as_completed_and_parses_back()
    {
        var completedAt = new DateTimeOffset(2026, 6, 3, 8, 0, 0, TimeSpan.Zero);
        var item = ItemWith(s => { s.Status = ItemStatus.Done; s.CompletedAt = completedAt; });

        var raw = VtodoMapper.ToVtodo(item, [], sourceRaw: null);

        Assert.Contains("STATUS:COMPLETED", raw);
        Assert.Contains("PERCENT-COMPLETE:100", raw);

        var parsed = VtodoMapper.Parse(raw);
        Assert.Equal(ItemStatus.Done, parsed.Status);
        Assert.Equal(completedAt, parsed.CompletedAt);
    }

    [Fact]
    public void Open_item_serializes_as_needs_action()
    {
        var raw = VtodoMapper.ToVtodo(ItemWith(_ => { }), [], sourceRaw: null);
        Assert.Contains("STATUS:NEEDS-ACTION", raw);
        Assert.Equal(ItemStatus.Open, VtodoMapper.Parse(raw).Status);
    }

    [Theory]
    [InlineData(ItemStatus.Cancelled, "STATUS:CANCELLED")]
    [InlineData(ItemStatus.InProgress, "STATUS:IN-PROCESS")]
    public void Standard_statuses_round_trip(ItemStatus status, string expectedLine)
    {
        var raw = VtodoMapper.ToVtodo(ItemWith(s => s.Status = status), [], sourceRaw: null);
        Assert.Contains(expectedLine, raw);
        Assert.Equal(status, VtodoMapper.Parse(raw).Status);
    }

    [Theory]
    [InlineData(ItemStatus.Blocked)]
    [InlineData(ItemStatus.Waiting)]
    public void Blocked_and_waiting_round_trip_via_x_prop(ItemStatus status)
    {
        var raw = VtodoMapper.ToVtodo(ItemWith(s => s.Status = status), [], sourceRaw: null);

        // No native VTODO status — they ride NEEDS-ACTION plus the precise X-prop.
        Assert.Contains("STATUS:NEEDS-ACTION", raw);
        Assert.Contains($"X-LUPIRA-STATUS:{status}", raw);
        Assert.Equal(status, VtodoMapper.Parse(raw).Status);
    }

    [Fact]
    public void Completed_status_wins_over_a_stale_x_status_prop()
    {
        // A client preserved X-LUPIRA-STATUS:Blocked but then completed the task — done-detection must win.
        const string raw =
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//phone//EN\r\n" +
            "BEGIN:VTODO\r\nUID:task-uid-1\r\nSUMMARY:x\r\nSTATUS:COMPLETED\r\nX-LUPIRA-STATUS:Blocked\r\n" +
            "END:VTODO\r\nEND:VCALENDAR\r\n";
        Assert.Equal(ItemStatus.Done, VtodoMapper.Parse(raw).Status);
    }

    [Fact]
    public void Tags_map_to_categories_by_label()
    {
        var tagId = Guid.Parse("0190a000-0000-7000-8000-0000000000a1");
        var item = ItemWith(s => s.Tags = [tagId]);
        var listTags = new List<TagDef> { new() { Id = tagId, Label = "urgent" } };

        var raw = VtodoMapper.ToVtodo(item, listTags, sourceRaw: null);

        Assert.Contains("urgent", raw);
        Assert.Contains("urgent", VtodoMapper.Parse(raw).Categories);
    }

    [Fact]
    public void Regeneration_preserves_unmodeled_properties_but_owns_modeled_ones()
    {
        // A prior DAV PUT set RRULE (unmodeled) plus a now-stale PRIORITY. A subsequent GET must
        // regenerate SUMMARY and PRIORITY from the snapshot (PRIORITY is now a modeled field) while
        // still keeping the unmodeled RRULE alive.
        const string source =
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//phone//EN\r\n" +
            "BEGIN:VTODO\r\nUID:task-uid-1\r\nSUMMARY:stale title\r\nPRIORITY:1\r\nRRULE:FREQ=DAILY\r\n" +
            "END:VTODO\r\nEND:VCALENDAR\r\n";
        var item = ItemWith(s => { s.Title = "current title"; s.Priority = 6; });

        var raw = VtodoMapper.ToVtodo(item, [], sourceRaw: source);

        Assert.Contains("SUMMARY:current title", raw);   // regenerated from the snapshot
        Assert.DoesNotContain("stale title", raw);
        Assert.Contains("PRIORITY:6", raw);              // from the snapshot, not the stale blob
        Assert.DoesNotContain("PRIORITY:1", raw);
        Assert.Contains("RRULE:FREQ=DAILY", raw);        // unmodeled property preserved
    }

    [Fact]
    public void Priority_round_trips_through_vtodo()
    {
        var raw = VtodoMapper.ToVtodo(ItemWith(s => s.Priority = 3), [], sourceRaw: null);
        Assert.Contains("PRIORITY:3", raw);
        Assert.Equal(3, VtodoMapper.Parse(raw).Priority);

        // Default 0 (= none) emits no PRIORITY and parses back as 0.
        var none = VtodoMapper.ToVtodo(ItemWith(_ => { }), [], sourceRaw: null);
        Assert.DoesNotContain("PRIORITY", none);
        Assert.Equal(0, VtodoMapper.Parse(none).Priority);
    }

    [Fact]
    public void Parse_rejects_payload_without_a_vtodo()
    {
        Assert.Throws<FormatException>(() => VtodoMapper.Parse("not an icalendar document"));
    }
}
