using LupiraTasksApi.Core.Data;
using LupiraTasksApi.Core.Domain;
using LupiraTasksApi.Core.Domain.Items.Events;
using Xunit;

namespace LupiraTasksApi.UnitTests;

public class EventUpcastsTests
{
    private static readonly Guid ItemId = Guid.Parse("0190a000-0000-7000-8000-000000000201");
    private static readonly Guid ListId = Guid.Parse("0190a000-0000-7000-8000-000000000202");
    private static readonly Guid Cmd = Guid.Parse("0190a000-0000-7000-8000-0000000002c0");
    private static readonly DateTimeOffset At = new(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Due = new(2026, 6, 20, 21, 59, 0, TimeSpan.Zero);

    [Fact]
    public void Due_date_set_v1_reads_as_a_timed_due()
    {
        var v2 = EventUpcasts.DueDateSet(new ItemDueDateSet(ItemId, Due, At, Cmd));

        Assert.Equal(new ItemDueDateSetV2(ItemId, DueOn: null, Due, At, Cmd), v2);
    }

    [Fact]
    public void Vtodo_put_v1_keeps_every_field_and_reads_its_due_as_timed()
    {
        var tags = new[] { Guid.NewGuid() };
        var v1 = new ItemVtodoPut(ItemId, ListId, "uid", "Title", "Notes", Due, ItemStatus.Done, At, tags, "a0", "RAW", At, Cmd, Priority: 3);

        var v2 = EventUpcasts.VtodoPut(v1);

        Assert.Equal(
            new ItemVtodoPutV2(ItemId, ListId, "uid", "Title", "Notes", DueOn: null, Due, ItemStatus.Done, At, tags, "a0", "RAW", At, Cmd, Priority: 3),
            v2);
    }
}
