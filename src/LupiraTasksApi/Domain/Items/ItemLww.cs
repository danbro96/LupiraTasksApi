namespace LupiraTasksApi.Domain.Items;

/// <summary>
/// Pure, DB-free conflict-resolution rules for an item. Every method mutates an
/// <see cref="ItemState"/> in place and is deterministic given (state, event data,
/// actor) — no Postgres, no Marten, no clock. This is the single source of truth
/// for the per-field last-writer-wins (LWW) semantics, shared by the server's
/// Marten <c>Item</c> snapshot and pinned by the LWW test-vector suite (the same
/// fixtures the client reducer must satisfy, for client/server convergence).
///
/// Rules:
///  * Per-field LWW keyed on the pair (<c>OccurredAt</c>, <c>CommandId</c>): a field
///    is written only when the incoming event is strictly newer than that field's
///    guard — i.e. its OccurredAt is later, OR its OccurredAt is equal and its
///    CommandId compares greater. An older event, or an equal (OccurredAt, CommandId)
///    replay, is a no-op (so a late-arriving stale edit never clobbers newer state,
///    and a redelivered command is idempotent). CommandId breaks exact-OccurredAt
///    ties deterministically so the server and the offline client reducer converge on
///    the same winner regardless of the order events are applied.
///  * Tag add/remove are commutative set deltas resolved per-tag by (OccurredAt, CommandId).
///  * <see cref="ApplyDeleted"/> is a tombstone: once Deleted, every later field
///    event is ignored. Each field apply checks Deleted first, so delete wins over
///    a concurrent edit regardless of OccurredAt.
/// </summary>
public static class ItemLww
{
    /// <summary>Creates the item. Seeds every field guard at the create (OccurredAt, CommandId).</summary>
    public static void ApplyAdded(ItemState s, ItemAdded e, string? actor)
    {
        s.Id = e.ItemId;
        s.ListId = e.ListId;
        s.ParentItemId = e.ParentItemId;
        s.Title = e.Title;
        s.SortOrder = e.SortOrder;
        s.CreatedBy = actor;
        s.CreatedAt = e.OccurredAt;
        s.UpdatedAt = e.OccurredAt;

        s.NameTs = e.OccurredAt;
        s.NameCmd = e.CommandId;
        s.MoveTs = e.OccurredAt;
        s.MoveCmd = e.CommandId;
    }

    public static void ApplyRenamed(ItemState s, ItemRenamed e)
    {
        if (s.Deleted || !Wins(e.OccurredAt, e.CommandId, s.NameTs, s.NameCmd)) return;
        s.Title = e.Title;
        s.NameTs = e.OccurredAt;
        s.NameCmd = e.CommandId;
        Touch(s, e.OccurredAt);
    }

    public static void ApplyNotesEdited(ItemState s, ItemNotesEdited e)
    {
        if (s.Deleted || !Wins(e.OccurredAt, e.CommandId, s.NotesTs, s.NotesCmd)) return;
        s.Notes = e.Notes;
        s.NotesTs = e.OccurredAt;
        s.NotesCmd = e.CommandId;
        Touch(s, e.OccurredAt);
    }

    public static void ApplyAssigned(ItemState s, ItemAssigned e)
    {
        if (s.Deleted || !Wins(e.OccurredAt, e.CommandId, s.AssigneeTs, s.AssigneeCmd)) return;
        s.AssignedTo = e.AssigneeEmail;
        s.AssigneeTs = e.OccurredAt;
        s.AssigneeCmd = e.CommandId;
        Touch(s, e.OccurredAt);
    }

    public static void ApplyDueDateSet(ItemState s, ItemDueDateSet e)
    {
        if (s.Deleted || !Wins(e.OccurredAt, e.CommandId, s.DueTs, s.DueCmd)) return;
        s.DueAt = e.DueAt;
        s.DueTs = e.OccurredAt;
        s.DueCmd = e.CommandId;
        Touch(s, e.OccurredAt);
    }

    public static void ApplyQuantitySet(ItemState s, ItemQuantitySet e)
    {
        if (s.Deleted || !Wins(e.OccurredAt, e.CommandId, s.QtyTs, s.QtyCmd)) return;
        s.Quantity = e.Quantity;
        s.Unit = e.Unit;
        s.QtyTs = e.OccurredAt;
        s.QtyCmd = e.CommandId;
        Touch(s, e.OccurredAt);
    }

    public static void ApplyCompleted(ItemState s, ItemCompleted e, string? actor)
    {
        if (s.Deleted || !Wins(e.OccurredAt, e.CommandId, s.CompletedTs, s.CompletedCmd)) return;
        s.Completed = true;
        s.CompletedAt = e.OccurredAt;
        s.CompletedBy = actor;
        s.CompletedTs = e.OccurredAt;
        s.CompletedCmd = e.CommandId;
        Touch(s, e.OccurredAt);
    }

    public static void ApplyReopened(ItemState s, ItemReopened e)
    {
        if (s.Deleted || !Wins(e.OccurredAt, e.CommandId, s.CompletedTs, s.CompletedCmd)) return;
        s.Completed = false;
        s.CompletedAt = null;
        s.CompletedBy = null;
        s.CompletedTs = e.OccurredAt;
        s.CompletedCmd = e.CommandId;
        Touch(s, e.OccurredAt);
    }

    public static void ApplyMoved(ItemState s, ItemMoved e)
    {
        if (s.Deleted || !Wins(e.OccurredAt, e.CommandId, s.MoveTs, s.MoveCmd)) return;
        s.ParentItemId = e.ParentItemId;
        s.SortOrder = e.SortOrder;
        s.MoveTs = e.OccurredAt;
        s.MoveCmd = e.CommandId;
        Touch(s, e.OccurredAt);
    }

    /// <summary>Commutative tag add — wins only if newer than the tag's last (OccurredAt, CommandId) touch.</summary>
    public static void ApplyTagAdded(ItemState s, ItemTagAdded e)
    {
        if (s.Deleted || !NewerTag(s, e.TagId, e.OccurredAt, e.CommandId)) return;
        if (!s.Tags.Contains(e.TagId)) s.Tags.Add(e.TagId);
        s.TagTs[e.TagId] = e.OccurredAt;
        s.TagCmd[e.TagId] = e.CommandId;
        Touch(s, e.OccurredAt);
    }

    /// <summary>Commutative tag remove — wins only if newer than the tag's last (OccurredAt, CommandId) touch.</summary>
    public static void ApplyTagRemoved(ItemState s, ItemTagRemoved e)
    {
        if (s.Deleted || !NewerTag(s, e.TagId, e.OccurredAt, e.CommandId)) return;
        s.Tags.Remove(e.TagId);
        s.TagTs[e.TagId] = e.OccurredAt;
        s.TagCmd[e.TagId] = e.CommandId;
        Touch(s, e.OccurredAt);
    }

    /// <summary>Tombstone. Unconditional and permanent — gates all later field events.</summary>
    public static void ApplyDeleted(ItemState s, ItemDeleted e)
    {
        s.Deleted = true;
        if (e.OccurredAt > s.UpdatedAt) s.UpdatedAt = e.OccurredAt;
    }

    /// <summary>
    /// True when an incoming event keyed (<paramref name="occurredAt"/>,
    /// <paramref name="commandId"/>) is strictly newer than a field's guard
    /// (<paramref name="guardTs"/>, <paramref name="guardCmd"/>): later OccurredAt, or
    /// equal OccurredAt with a greater CommandId. Equal pair (a replay) loses, so apply
    /// is idempotent.
    /// </summary>
    private static bool Wins(DateTimeOffset occurredAt, Guid commandId, DateTimeOffset guardTs, Guid guardCmd) =>
        occurredAt > guardTs || (occurredAt == guardTs && commandId.CompareTo(guardCmd) > 0);

    /// <summary>True when this tag op is strictly newer than the tag's last recorded (OccurredAt, CommandId).</summary>
    private static bool NewerTag(ItemState s, Guid tagId, DateTimeOffset occurredAt, Guid commandId)
    {
        if (!s.TagTs.TryGetValue(tagId, out var lastTs)) return true;
        s.TagCmd.TryGetValue(tagId, out var lastCmd);
        return Wins(occurredAt, commandId, lastTs, lastCmd);
    }

    private static void Touch(ItemState s, DateTimeOffset occurredAt)
    {
        if (occurredAt > s.UpdatedAt) s.UpdatedAt = occurredAt;
    }
}
