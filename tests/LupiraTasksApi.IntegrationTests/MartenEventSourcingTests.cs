using JasperFx;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Items;
using Marten;
using Xunit;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>
/// Exercises the Marten event store against the shared real Postgres (store-level, not over HTTP).
/// Closes the runtime gaps the unit tests can't: inline snapshot replay + actor attribution, and that
/// the idempotency dedup truly fails-fast and rolls back the appended events on a duplicate command id.
/// </summary>
public sealed class MartenEventSourcingTests(TasksApiTestFactory factory) : IntegrationTest(factory)
{
    [Fact]
    public async Task Item_event_stream_replays_into_inline_snapshot_with_actor_attribution()
    {
        var itemId = Guid.CreateVersion7();
        var listId = Guid.CreateVersion7();
        var t0 = DateTimeOffset.UtcNow;

        await using (var s = Store.LightweightSession())
        {
            s.SetHeader(EventActor.HeaderKey, "alice@lupira.com");
            s.Events.StartStream<Item>(
                itemId,
                new ItemAdded(itemId, listId, null, "Milk", "a", t0, Guid.CreateVersion7()),
                new ItemRenamed(itemId, "Oat milk", t0.AddSeconds(1), Guid.CreateVersion7()),
                new ItemCompleted(itemId, t0.AddSeconds(2), Guid.CreateVersion7()));
            await s.SaveChangesAsync();
        }

        await using var q = Store.QuerySession();
        var item = await q.LoadAsync<Item>(itemId);

        Assert.NotNull(item);
        Assert.Equal(listId, item!.ListId);
        Assert.Equal("Oat milk", item.Title);   // last rename wins
        Assert.True(item.Completed);
        Assert.Equal("alice@lupira.com", item.CreatedBy);
        Assert.Equal("alice@lupira.com", item.CompletedBy);
    }

    [Fact]
    public async Task Duplicate_command_id_insert_fails_fast_and_keeps_one_row()
    {
        var cmd = Guid.CreateVersion7();
        var agg = Guid.CreateVersion7();

        await using (var s1 = Store.LightweightSession())
        {
            s1.Insert(new ProcessedCommand { CommandId = cmd, AggregateId = agg, ResultVersion = 1, ProcessedAt = DateTimeOffset.UtcNow });
            await s1.SaveChangesAsync();
        }

        Exception? ex;
        await using (var s2 = Store.LightweightSession())
        {
            s2.Insert(new ProcessedCommand { CommandId = cmd, AggregateId = agg, ResultVersion = 9, ProcessedAt = DateTimeOffset.UtcNow });
            ex = await Record.ExceptionAsync(() => s2.SaveChangesAsync());
        }

        // Must fail fast (not silently upsert) — this is what the handler's catch relies on.
        Assert.NotNull(ex);
        Assert.True(
            ex is DocumentAlreadyExistsException,
            $"Expected JasperFx.DocumentAlreadyExistsException (the type the handlers catch); got {ex!.GetType().FullName}: {ex.Message}");

        await using var q = Store.QuerySession();
        var count = await q.Query<ProcessedCommand>().CountAsync(c => c.CommandId == cmd);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Append_plus_duplicate_ledger_insert_rolls_back_the_events_too()
    {
        // Mirrors AppendDedupAsync: append an event AND insert the dedup ledger row in one
        // transaction. A duplicate command id must roll back the WHOLE transaction — the
        // appended event must NOT survive (this is the TOCTOU/atomicity guarantee).
        var itemId = Guid.CreateVersion7();
        var listId = Guid.CreateVersion7();
        var cmd = Guid.CreateVersion7();
        var t = DateTimeOffset.UtcNow;

        await using (var s0 = Store.LightweightSession())
        {
            s0.Insert(new ProcessedCommand { CommandId = cmd, AggregateId = itemId, ResultVersion = 1, ProcessedAt = t });
            s0.Events.StartStream<Item>(itemId, new ItemAdded(itemId, listId, null, "Original", "a", t, Guid.CreateVersion7()));
            await s0.SaveChangesAsync();
        }

        await using (var s1 = Store.LightweightSession())
        {
            s1.Events.Append(itemId, new ItemRenamed(itemId, "Should NOT persist", t.AddSeconds(1), cmd));
            s1.Insert(new ProcessedCommand { CommandId = cmd, AggregateId = itemId, ResultVersion = 2, ProcessedAt = t });
            var ex = await Record.ExceptionAsync(() => s1.SaveChangesAsync());
            Assert.NotNull(ex); // duplicate command id rolls the whole thing back
        }

        await using var q = Store.QuerySession();
        var item = await q.LoadAsync<Item>(itemId);
        Assert.Equal("Original", item!.Title); // the rename rolled back with the failed ledger insert
    }
}
