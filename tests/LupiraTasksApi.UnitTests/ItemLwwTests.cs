using LupiraTasksApi.Domain.Items;
using Xunit;

namespace LupiraTasksApi.UnitTests;

/// <summary>
/// Shared last-writer-wins (LWW) test vectors for the pure <see cref="ItemLww"/>
/// engine. These run with no Postgres and no Marten — they exercise the exact
/// conflict-resolution rules the server's <c>Item</c> snapshot applies, and are the
/// same fixtures the offline client reducer must satisfy for client/server
/// convergence. If a rule changes here, both implementations must change together.
///
/// Resolution is keyed on the pair (OccurredAt, CommandId): OccurredAt is the
/// primary key; CommandId is the deterministic tiebreaker on an exact OccurredAt
/// tie. The tie vectors below assert CONVERGENCE — the same final state regardless
/// of the order events are applied — which is the contract the mobile reducer mirrors.
/// </summary>
public class ItemLwwTests
{
    private static readonly Guid ItemId = Guid.Parse("0190a000-0000-7000-8000-000000000001");
    private static readonly Guid ListId = Guid.Parse("0190a000-0000-7000-8000-000000000002");

    // Distinct command ids with a known ordering: CmdLo < CmdHi.
    private static readonly Guid CmdLo = Guid.Parse("0190a000-0000-7000-8000-0000000000c1");
    private static readonly Guid CmdHi = Guid.Parse("0190a000-0000-7000-8000-0000000000c2");
    // A default command id for vectors that don't exercise the tiebreaker.
    private static readonly Guid Cmd = Guid.Parse("0190a000-0000-7000-8000-0000000000c0");

    // A fixed timeline of strictly-increasing instants.
    private static readonly DateTimeOffset T0 = new(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int seconds) => T0.AddSeconds(seconds);

    private static ItemState NewItem(int createdAtSeconds = 0, string? actor = "alice@x.test")
    {
        var s = new ItemState();
        ItemLww.ApplyAdded(
            s,
            new ItemAdded(ItemId, ListId, ParentItemId: null, Title: "Milk", SortOrder: "a0", OccurredAt: At(createdAtSeconds), CommandId: Cmd),
            actor);
        return s;
    }

    // --- Creation ---

    [Fact]
    public void Added_seeds_fields_and_creator_from_actor()
    {
        var s = NewItem(createdAtSeconds: 5, actor: "bob@x.test");

        Assert.Equal(ItemId, s.Id);
        Assert.Equal(ListId, s.ListId);
        Assert.Equal("Milk", s.Title);
        Assert.Equal("a0", s.SortOrder);
        Assert.Equal("bob@x.test", s.CreatedBy);
        Assert.Equal(At(5), s.CreatedAt);
        Assert.Equal(At(5), s.NameTs);
        Assert.False(s.Deleted);
    }

    // --- Vector: older OccurredAt is a no-op ---

    [Fact]
    public void Rename_with_older_occurredAt_is_a_no_op()
    {
        var s = NewItem();
        ItemLww.ApplyRenamed(s, new ItemRenamed(ItemId, "Newer", At(20), Cmd));

        // An edit that happened earlier (e.g. a late-arriving offline edit) must not clobber.
        ItemLww.ApplyRenamed(s, new ItemRenamed(ItemId, "Stale", At(10), Cmd));

        Assert.Equal("Newer", s.Title);
        Assert.Equal(At(20), s.NameTs);
    }

    [Fact]
    public void Rename_with_equal_occurredAt_and_equal_command_is_a_no_op()
    {
        var s = NewItem();
        ItemLww.ApplyRenamed(s, new ItemRenamed(ItemId, "First", At(20), Cmd));
        // Same (OccurredAt, CommandId) is a replay → must not overwrite (idempotent).
        ItemLww.ApplyRenamed(s, new ItemRenamed(ItemId, "Replay", At(20), Cmd));

        Assert.Equal("First", s.Title);
    }

    [Fact]
    public void Newer_occurredAt_wins()
    {
        var s = NewItem();
        ItemLww.ApplyRenamed(s, new ItemRenamed(ItemId, "Old", At(10), Cmd));
        ItemLww.ApplyRenamed(s, new ItemRenamed(ItemId, "New", At(30), Cmd));

        Assert.Equal("New", s.Title);
        Assert.Equal(At(30), s.NameTs);
    }

    // --- Tie vector (a): two distinct renames at equal OccurredAt converge by CommandId ---

    [Fact]
    public void Two_renames_at_equal_occurredAt_converge_by_command_id_in_both_orders()
    {
        // Same instant, different command ids. The higher command id must win, and the
        // result must be identical whichever order the two renames are applied.
        var loThenHi = NewItem();
        ItemLww.ApplyRenamed(loThenHi, new ItemRenamed(ItemId, "FromLo", At(20), CmdLo));
        ItemLww.ApplyRenamed(loThenHi, new ItemRenamed(ItemId, "FromHi", At(20), CmdHi));

        var hiThenLo = NewItem();
        ItemLww.ApplyRenamed(hiThenLo, new ItemRenamed(ItemId, "FromHi", At(20), CmdHi));
        ItemLww.ApplyRenamed(hiThenLo, new ItemRenamed(ItemId, "FromLo", At(20), CmdLo));

        Assert.Equal("FromHi", loThenHi.Title);
        Assert.Equal("FromHi", hiThenLo.Title);
        Assert.Equal(loThenHi.Title, hiThenLo.Title);
        Assert.Equal(CmdHi, loThenHi.NameCmd);
        Assert.Equal(CmdHi, hiThenLo.NameCmd);
    }

    // --- Vector: edits to different fields both survive (independent of arrival order) ---

    [Fact]
    public void Edits_to_different_fields_both_survive()
    {
        var s = NewItem();

        // Two offline clients edited different fields; replay in arbitrary order.
        ItemLww.ApplyAssigned(s, new ItemAssigned(ItemId, "carol@x.test", At(40), Cmd));
        ItemLww.ApplyRenamed(s, new ItemRenamed(ItemId, "Oat milk", At(30), Cmd));
        ItemLww.ApplyDueDateSet(s, new ItemDueDateSet(ItemId, At(100), At(35), Cmd));

        Assert.Equal("Oat milk", s.Title);
        Assert.Equal("carol@x.test", s.AssignedTo);
        Assert.Equal(At(100), s.DueAt);
    }

    [Fact]
    public void Different_field_guards_are_independent()
    {
        var s = NewItem();
        ItemLww.ApplyAssigned(s, new ItemAssigned(ItemId, "carol@x.test", At(50), Cmd));

        // A rename older than the assign still applies — name has its own guard.
        ItemLww.ApplyRenamed(s, new ItemRenamed(ItemId, "Renamed", At(20), Cmd));

        Assert.Equal("Renamed", s.Title);
        Assert.Equal("carol@x.test", s.AssignedTo);
    }

    [Fact]
    public void Assign_to_null_unassigns_when_newer()
    {
        var s = NewItem();
        ItemLww.ApplyAssigned(s, new ItemAssigned(ItemId, "carol@x.test", At(10), Cmd));
        ItemLww.ApplyAssigned(s, new ItemAssigned(ItemId, AssigneeEmail: null, At(20), Cmd));

        Assert.Null(s.AssignedTo);
    }

    [Fact]
    public void Quantity_set_carries_unit_and_respects_lww()
    {
        var s = NewItem();
        ItemLww.ApplyQuantitySet(s, new ItemQuantitySet(ItemId, 3m, "l", At(20), Cmd));
        ItemLww.ApplyQuantitySet(s, new ItemQuantitySet(ItemId, 99m, "stale", At(10), Cmd));

        Assert.Equal(3m, s.Quantity);
        Assert.Equal("l", s.Unit);
    }

    [Fact]
    public void Priority_set_respects_lww()
    {
        var s = NewItem();
        Assert.Equal(0, s.Priority);   // default: none

        ItemLww.ApplyPrioritySet(s, new ItemPrioritySet(ItemId, 5, At(20), Cmd));
        // A stale (older) priority must not clobber the newer one.
        ItemLww.ApplyPrioritySet(s, new ItemPrioritySet(ItemId, 9, At(10), Cmd));
        Assert.Equal(5, s.Priority);

        // A newer set wins; clearing back to 0 (= none) is a normal write.
        ItemLww.ApplyPrioritySet(s, new ItemPrioritySet(ItemId, 0, At(30), Cmd));
        Assert.Equal(0, s.Priority);

        // An exact (OccurredAt, CommandId) replay is idempotent.
        ItemLww.ApplyPrioritySet(s, new ItemPrioritySet(ItemId, 7, At(30), Cmd));
        Assert.Equal(0, s.Priority);
    }

    [Fact]
    public void Priority_has_its_own_independent_guard()
    {
        var s = NewItem();
        ItemLww.ApplyPrioritySet(s, new ItemPrioritySet(ItemId, 4, At(50), Cmd));

        // An older edit to a different field still applies — guards are per-field.
        ItemLww.ApplyRenamed(s, new ItemRenamed(ItemId, "Renamed", At(20), Cmd));

        Assert.Equal(4, s.Priority);
        Assert.Equal("Renamed", s.Title);
    }

    // --- Vector: tag add/remove commutativity + add-vs-remove race by OccurredAt ---

    private static readonly Guid TagRed = Guid.Parse("0190a000-0000-7000-8000-0000000000a1");
    private static readonly Guid TagBlue = Guid.Parse("0190a000-0000-7000-8000-0000000000a2");

    [Fact]
    public void Tag_adds_are_commutative_and_idempotent()
    {
        var s1 = NewItem();
        ItemLww.ApplyTagAdded(s1, new ItemTagAdded(ItemId, TagRed, At(10), Cmd));
        ItemLww.ApplyTagAdded(s1, new ItemTagAdded(ItemId, TagBlue, At(20), Cmd));

        var s2 = NewItem();
        ItemLww.ApplyTagAdded(s2, new ItemTagAdded(ItemId, TagBlue, At(20), Cmd));
        ItemLww.ApplyTagAdded(s2, new ItemTagAdded(ItemId, TagRed, At(10), Cmd));
        ItemLww.ApplyTagAdded(s2, new ItemTagAdded(ItemId, TagRed, At(10), Cmd)); // duplicate replay

        Assert.Equal(new HashSet<Guid> { TagRed, TagBlue }, s1.Tags.ToHashSet());
        Assert.Equal(s1.Tags.ToHashSet(), s2.Tags.ToHashSet());
    }

    [Fact]
    public void Tag_add_then_later_remove_results_in_removed()
    {
        var s = NewItem();
        ItemLww.ApplyTagAdded(s, new ItemTagAdded(ItemId, TagRed, At(10), Cmd));
        ItemLww.ApplyTagRemoved(s, new ItemTagRemoved(ItemId, TagRed, At(20), Cmd));

        Assert.DoesNotContain(TagRed, s.Tags);
    }

    [Fact]
    public void Tag_add_vs_remove_race_resolves_by_occurredAt_regardless_of_arrival_order()
    {
        // Remove is newer than add → tag must end up removed, whichever order they replay.
        var addThenRemove = NewItem();
        ItemLww.ApplyTagAdded(addThenRemove, new ItemTagAdded(ItemId, TagRed, At(10), Cmd));
        ItemLww.ApplyTagRemoved(addThenRemove, new ItemTagRemoved(ItemId, TagRed, At(20), Cmd));

        var removeThenAdd = NewItem();
        ItemLww.ApplyTagRemoved(removeThenAdd, new ItemTagRemoved(ItemId, TagRed, At(20), Cmd));
        ItemLww.ApplyTagAdded(removeThenAdd, new ItemTagAdded(ItemId, TagRed, At(10), Cmd));

        Assert.DoesNotContain(TagRed, addThenRemove.Tags);
        Assert.DoesNotContain(TagRed, removeThenAdd.Tags);
    }

    [Fact]
    public void Tag_add_newer_than_remove_results_in_present_regardless_of_arrival_order()
    {
        // Add is newer than remove → tag present either way.
        var a = NewItem();
        ItemLww.ApplyTagRemoved(a, new ItemTagRemoved(ItemId, TagRed, At(10), Cmd));
        ItemLww.ApplyTagAdded(a, new ItemTagAdded(ItemId, TagRed, At(20), Cmd));

        var b = NewItem();
        ItemLww.ApplyTagAdded(b, new ItemTagAdded(ItemId, TagRed, At(20), Cmd));
        ItemLww.ApplyTagRemoved(b, new ItemTagRemoved(ItemId, TagRed, At(10), Cmd));

        Assert.Contains(TagRed, a.Tags);
        Assert.Contains(TagRed, b.Tags);
    }

    // --- Tie vector (c): tag add vs remove at equal OccurredAt converge by CommandId ---

    [Fact]
    public void Tag_add_vs_remove_at_equal_occurredAt_converge_by_command_id_in_both_orders()
    {
        // Same instant: the higher command id wins. Here remove (CmdHi) beats add (CmdLo),
        // so the tag ends up removed regardless of arrival order.
        var addThenRemove = NewItem();
        ItemLww.ApplyTagAdded(addThenRemove, new ItemTagAdded(ItemId, TagRed, At(20), CmdLo));
        ItemLww.ApplyTagRemoved(addThenRemove, new ItemTagRemoved(ItemId, TagRed, At(20), CmdHi));

        var removeThenAdd = NewItem();
        ItemLww.ApplyTagRemoved(removeThenAdd, new ItemTagRemoved(ItemId, TagRed, At(20), CmdHi));
        ItemLww.ApplyTagAdded(removeThenAdd, new ItemTagAdded(ItemId, TagRed, At(20), CmdLo));

        Assert.DoesNotContain(TagRed, addThenRemove.Tags);
        Assert.DoesNotContain(TagRed, removeThenAdd.Tags);
        Assert.Equal(addThenRemove.Tags.ToHashSet(), removeThenAdd.Tags.ToHashSet());

        // And the mirror: when add (CmdHi) beats remove (CmdLo) at the same instant, the tag
        // is present regardless of arrival order.
        var addWinsA = NewItem();
        ItemLww.ApplyTagAdded(addWinsA, new ItemTagAdded(ItemId, TagBlue, At(20), CmdHi));
        ItemLww.ApplyTagRemoved(addWinsA, new ItemTagRemoved(ItemId, TagBlue, At(20), CmdLo));

        var addWinsB = NewItem();
        ItemLww.ApplyTagRemoved(addWinsB, new ItemTagRemoved(ItemId, TagBlue, At(20), CmdLo));
        ItemLww.ApplyTagAdded(addWinsB, new ItemTagAdded(ItemId, TagBlue, At(20), CmdHi));

        Assert.Contains(TagBlue, addWinsA.Tags);
        Assert.Contains(TagBlue, addWinsB.Tags);
    }

    [Fact]
    public void Per_tag_guards_are_independent()
    {
        var s = NewItem();
        ItemLww.ApplyTagAdded(s, new ItemTagAdded(ItemId, TagRed, At(30), Cmd));
        // Older op on a *different* tag still applies — guards are per-tag.
        ItemLww.ApplyTagAdded(s, new ItemTagAdded(ItemId, TagBlue, At(10), Cmd));

        Assert.Equal(new HashSet<Guid> { TagRed, TagBlue }, s.Tags.ToHashSet());
    }

    // --- Vector: tombstone precedence ---

    [Fact]
    public void Delete_then_later_edit_is_ignored()
    {
        var s = NewItem();
        ItemLww.ApplyDeleted(s, new ItemDeleted(ItemId, At(20), Cmd));

        // Edit with a newer OccurredAt — tombstone still wins (delete-vs-edit → delete).
        ItemLww.ApplyRenamed(s, new ItemRenamed(ItemId, "ZombieName", At(100), Cmd));
        ItemLww.ApplyCompleted(s, new ItemCompleted(ItemId, At(100), Cmd), "mallory@x.test");
        ItemLww.ApplyTagAdded(s, new ItemTagAdded(ItemId, TagRed, At(100), Cmd));

        Assert.True(s.Deleted);
        Assert.Equal("Milk", s.Title);
        Assert.False(s.Completed);
        Assert.Empty(s.Tags);
    }

    [Fact]
    public void Edit_before_delete_then_delete_still_ends_deleted()
    {
        var s = NewItem();
        ItemLww.ApplyRenamed(s, new ItemRenamed(ItemId, "Edited", At(10), Cmd));
        ItemLww.ApplyDeleted(s, new ItemDeleted(ItemId, At(5), Cmd)); // delete with older ts

        // Tombstone is unconditional: once deleted, it stays deleted.
        Assert.True(s.Deleted);
    }

    // --- Vector: completed LWW ---

    [Fact]
    public void Complete_records_actor_and_timestamp()
    {
        var s = NewItem();
        ItemLww.ApplyCompleted(s, new ItemCompleted(ItemId, At(30), Cmd), "dave@x.test");

        Assert.True(s.Completed);
        Assert.Equal(At(30), s.CompletedAt);
        Assert.Equal("dave@x.test", s.CompletedBy);
    }

    [Fact]
    public void Reopen_newer_than_complete_wins()
    {
        var s = NewItem();
        ItemLww.ApplyCompleted(s, new ItemCompleted(ItemId, At(20), Cmd), "dave@x.test");
        ItemLww.ApplyReopened(s, new ItemReopened(ItemId, At(30), Cmd));

        Assert.False(s.Completed);
        Assert.Null(s.CompletedAt);
        Assert.Null(s.CompletedBy);
    }

    [Fact]
    public void Stale_reopen_does_not_override_newer_complete()
    {
        var s = NewItem();
        ItemLww.ApplyCompleted(s, new ItemCompleted(ItemId, At(30), Cmd), "dave@x.test");
        // Reopen arrives late but is older → must not reopen.
        ItemLww.ApplyReopened(s, new ItemReopened(ItemId, At(10), Cmd));

        Assert.True(s.Completed);
        Assert.Equal("dave@x.test", s.CompletedBy);
    }

    [Fact]
    public void Completed_state_converges_regardless_of_arrival_order()
    {
        // complete@20, reopen@30, complete@40 → final state completed (latest wins), any order.
        var ordered = NewItem();
        ItemLww.ApplyCompleted(ordered, new ItemCompleted(ItemId, At(20), Cmd), "a@x.test");
        ItemLww.ApplyReopened(ordered, new ItemReopened(ItemId, At(30), Cmd));
        ItemLww.ApplyCompleted(ordered, new ItemCompleted(ItemId, At(40), Cmd), "b@x.test");

        var shuffled = NewItem();
        ItemLww.ApplyCompleted(shuffled, new ItemCompleted(ItemId, At(40), Cmd), "b@x.test");
        ItemLww.ApplyReopened(shuffled, new ItemReopened(ItemId, At(30), Cmd));
        ItemLww.ApplyCompleted(shuffled, new ItemCompleted(ItemId, At(20), Cmd), "a@x.test");

        Assert.True(ordered.Completed);
        Assert.Equal("b@x.test", ordered.CompletedBy);
        Assert.Equal(At(40), ordered.CompletedAt);

        Assert.Equal(ordered.Completed, shuffled.Completed);
        Assert.Equal(ordered.CompletedBy, shuffled.CompletedBy);
        Assert.Equal(ordered.CompletedAt, shuffled.CompletedAt);
    }

    // --- Tie vector (b): complete vs reopen at equal OccurredAt converge by CommandId ---

    [Fact]
    public void Complete_vs_reopen_at_equal_occurredAt_converge_by_command_id_in_both_orders()
    {
        // Same instant on the shared completed guard: the higher command id wins. Here
        // reopen (CmdHi) beats complete (CmdLo), so the item ends up reopened either way.
        var completeThenReopen = NewItem();
        ItemLww.ApplyCompleted(completeThenReopen, new ItemCompleted(ItemId, At(20), CmdLo), "a@x.test");
        ItemLww.ApplyReopened(completeThenReopen, new ItemReopened(ItemId, At(20), CmdHi));

        var reopenThenComplete = NewItem();
        ItemLww.ApplyReopened(reopenThenComplete, new ItemReopened(ItemId, At(20), CmdHi));
        ItemLww.ApplyCompleted(reopenThenComplete, new ItemCompleted(ItemId, At(20), CmdLo), "a@x.test");

        Assert.False(completeThenReopen.Completed);
        Assert.False(reopenThenComplete.Completed);
        Assert.Equal(completeThenReopen.Completed, reopenThenComplete.Completed);

        // Mirror: complete (CmdHi) beats reopen (CmdLo) at the same instant → completed either way.
        var completeWinsA = NewItem();
        ItemLww.ApplyReopened(completeWinsA, new ItemReopened(ItemId, At(20), CmdLo));
        ItemLww.ApplyCompleted(completeWinsA, new ItemCompleted(ItemId, At(20), CmdHi), "b@x.test");

        var completeWinsB = NewItem();
        ItemLww.ApplyCompleted(completeWinsB, new ItemCompleted(ItemId, At(20), CmdHi), "b@x.test");
        ItemLww.ApplyReopened(completeWinsB, new ItemReopened(ItemId, At(20), CmdLo));

        Assert.True(completeWinsA.Completed);
        Assert.True(completeWinsB.Completed);
        Assert.Equal("b@x.test", completeWinsA.CompletedBy);
        Assert.Equal("b@x.test", completeWinsB.CompletedBy);
    }

    // --- UpdatedAt tracks the latest applied event ---

    [Fact]
    public void UpdatedAt_advances_to_latest_applied_event()
    {
        var s = NewItem(createdAtSeconds: 0);
        ItemLww.ApplyRenamed(s, new ItemRenamed(ItemId, "X", At(50), Cmd));
        ItemLww.ApplyRenamed(s, new ItemRenamed(ItemId, "stale", At(10), Cmd)); // no-op

        Assert.Equal(At(50), s.UpdatedAt);
    }

    // --- Adversarial: brute-force EVERY permutation of a same-field concurrent set ---
    // Each applier mutates a fresh item; we assert all 5! permutations of a hostile
    // event bag (equal OccurredAt across distinct command ids, plus a strictly-newer
    // and a strictly-older edit) converge to one final (Title, NameTs, NameCmd).

    [Fact]
    public void Rename_set_converges_under_every_permutation()
    {
        // CmdA < CmdB < CmdC by Guid.CompareTo — verify the assumption before relying on it.
        Assert.True(CmdA.CompareTo(CmdB) < 0);
        Assert.True(CmdB.CompareTo(CmdC) < 0);

        Action<ItemState>[] ops =
        [
            s => ItemLww.ApplyRenamed(s, new ItemRenamed(ItemId, "tieA", At(20), CmdA)),
            s => ItemLww.ApplyRenamed(s, new ItemRenamed(ItemId, "tieB", At(20), CmdB)),
            s => ItemLww.ApplyRenamed(s, new ItemRenamed(ItemId, "tieC", At(20), CmdC)),
            s => ItemLww.ApplyRenamed(s, new ItemRenamed(ItemId, "older", At(10), CmdC)),
            s => ItemLww.ApplyRenamed(s, new ItemRenamed(ItemId, "newer", At(30), CmdA)),
        ];

        (string Title, DateTimeOffset Ts, Guid Cmd)? expected = null;
        foreach (var perm in Permutations(ops))
        {
            var s = NewItem();
            foreach (var op in perm) op(s);
            var got = (s.Title, s.NameTs, s.NameCmd);
            expected ??= got;
            Assert.Equal(expected, got);
        }

        // The strictly-newest event (At(30)) must win outright over every tie at At(20).
        Assert.Equal(("newer", At(30), CmdA), expected!.Value);
    }

    [Fact]
    public void Completed_set_converges_under_every_permutation()
    {
        // complete/reopen share one guard; equal-instant ties must break by CommandId.
        Action<ItemState>[] ops =
        [
            s => ItemLww.ApplyCompleted(s, new ItemCompleted(ItemId, At(20), CmdA), "a@x.test"),
            s => ItemLww.ApplyReopened(s, new ItemReopened(ItemId, At(20), CmdB)),
            s => ItemLww.ApplyCompleted(s, new ItemCompleted(ItemId, At(20), CmdC), "c@x.test"),
            s => ItemLww.ApplyReopened(s, new ItemReopened(ItemId, At(15), CmdC)),
        ];

        (bool Completed, DateTimeOffset Ts, Guid Cmd, string? By)? expected = null;
        foreach (var perm in Permutations(ops))
        {
            var s = NewItem();
            foreach (var op in perm) op(s);
            var got = (s.Completed, s.CompletedTs, s.CompletedCmd, s.CompletedBy);
            expected ??= got;
            Assert.Equal(expected, got);
        }

        // CmdC is the highest at the winning instant At(20): the complete@CmdC wins.
        Assert.Equal((true, At(20), CmdC, "c@x.test"), expected!.Value);
    }

    [Fact]
    public void Tag_add_remove_set_converges_under_every_permutation()
    {
        Action<ItemState>[] ops =
        [
            s => ItemLww.ApplyTagAdded(s, new ItemTagAdded(ItemId, TagRed, At(20), CmdA)),
            s => ItemLww.ApplyTagRemoved(s, new ItemTagRemoved(ItemId, TagRed, At(20), CmdB)),
            s => ItemLww.ApplyTagAdded(s, new ItemTagAdded(ItemId, TagRed, At(20), CmdC)),
            s => ItemLww.ApplyTagRemoved(s, new ItemTagRemoved(ItemId, TagRed, At(10), CmdC)),
        ];

        (bool Present, DateTimeOffset Ts, Guid Cmd)? expected = null;
        foreach (var perm in Permutations(ops))
        {
            var s = NewItem();
            foreach (var op in perm) op(s);
            var got = (s.Tags.Contains(TagRed), s.TagTs[TagRed], s.TagCmd[TagRed]);
            expected ??= got;
            Assert.Equal(expected, got);
        }

        // CmdC add wins the At(20) tie → present.
        Assert.Equal((true, At(20), CmdC), expected!.Value);
    }

    private static readonly Guid CmdA = Guid.Parse("0190a000-0000-7000-8000-0000000000d1");
    private static readonly Guid CmdB = Guid.Parse("0190a000-0000-7000-8000-0000000000d2");
    private static readonly Guid CmdC = Guid.Parse("0190a000-0000-7000-8000-0000000000d3");

    private static IEnumerable<T[]> Permutations<T>(T[] items)
    {
        if (items.Length <= 1) { yield return items; yield break; }
        for (var i = 0; i < items.Length; i++)
        {
            var rest = new T[items.Length - 1];
            Array.Copy(items, 0, rest, 0, i);
            Array.Copy(items, i + 1, rest, i, items.Length - i - 1);
            foreach (var sub in Permutations(rest))
                yield return [items[i], .. sub];
        }
    }
}
