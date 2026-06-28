using JasperFx.Events;
using LupiraTasksApi.Domain;

namespace LupiraTasksApi.Domain.Items;

/// <summary>
/// Inline Marten snapshot for the <c>Item</c> stream (stream id = ItemId). All
/// conflict-resolution logic lives in the pure, DB-free <see cref="ItemLww"/>
/// engine operating on <see cref="State"/>; the <c>Apply(IEvent&lt;T&gt;)</c>
/// methods here are thin adapters that unwrap the event data plus the
/// <c>actor</c> metadata header and delegate. Keeping the rules out of this
/// Marten-bound type lets the same per-field LWW semantics be unit-tested without
/// Postgres and shared verbatim with the offline client reducer.
/// </summary>
public sealed class Item
{
    public Guid Id { get; set; }

    /// <summary>Populated by Marten from the event stream version.</summary>
    public int Version { get; set; }

    /// <summary>The full per-field state + LWW guards (serialized by Marten).</summary>
    public ItemState State { get; set; } = new();

    // --- Read-through snapshot fields (top-level for serialization/querying) ---
    public Guid ListId => State.ListId;
    public Guid? ParentItemId => State.ParentItemId;
    public string Title => State.Title;
    public string? Notes => State.Notes;
    public ItemStatus Status => State.Status;
    public string? StatusReason => State.StatusReason;
    /// <summary>Derived from the single lifecycle field: an item is completed iff its status is <see cref="ItemStatus.Done"/>.</summary>
    public bool Completed => State.Status == ItemStatus.Done;
    public DateTimeOffset? CompletedAt => State.CompletedAt;
    public string? CompletedBy => State.CompletedBy;
    public string? AssignedTo => State.AssignedTo;
    public DateTimeOffset? DueAt => State.DueAt;
    public decimal? Quantity => State.Quantity;
    public string? Unit => State.Unit;
    public int Priority => State.Priority;
    public IReadOnlyList<Guid> Tags => State.Tags;
    public string SortOrder => State.SortOrder;
    public string Uid => State.Uid;
    public string? SourceVtodo => State.SourceVtodo;
    public string? CreatedBy => State.CreatedBy;
    public DateTimeOffset CreatedAt => State.CreatedAt;
    public DateTimeOffset UpdatedAt => State.UpdatedAt;
    public bool Deleted => State.Deleted;

    public void Apply(IEvent<ItemAdded> e) => ItemLww.ApplyAdded(State, e.Data, EventActor.Of(e));
    public void Apply(IEvent<ItemRenamed> e) => ItemLww.ApplyRenamed(State, e.Data);
    public void Apply(IEvent<ItemNotesEdited> e) => ItemLww.ApplyNotesEdited(State, e.Data);
    public void Apply(IEvent<ItemAssigned> e) => ItemLww.ApplyAssigned(State, e.Data);
    public void Apply(IEvent<ItemDueDateSet> e) => ItemLww.ApplyDueDateSet(State, e.Data);
    public void Apply(IEvent<ItemTagAdded> e) => ItemLww.ApplyTagAdded(State, e.Data);
    public void Apply(IEvent<ItemTagRemoved> e) => ItemLww.ApplyTagRemoved(State, e.Data);
    public void Apply(IEvent<ItemQuantitySet> e) => ItemLww.ApplyQuantitySet(State, e.Data);
    public void Apply(IEvent<ItemPrioritySet> e) => ItemLww.ApplyPrioritySet(State, e.Data);
    public void Apply(IEvent<ItemCompleted> e) => ItemLww.ApplyCompleted(State, e.Data, EventActor.Of(e));
    public void Apply(IEvent<ItemReopened> e) => ItemLww.ApplyReopened(State, e.Data);
    public void Apply(IEvent<ItemStatusChanged> e) => ItemLww.ApplyStatusChanged(State, e.Data, EventActor.Of(e));
    public void Apply(IEvent<ItemMoved> e) => ItemLww.ApplyMoved(State, e.Data);
    public void Apply(IEvent<ItemDeleted> e) => ItemLww.ApplyDeleted(State, e.Data);
    public void Apply(IEvent<ItemVtodoPut> e) => ItemLww.ApplyVtodoPut(State, e.Data, EventActor.Of(e));
}
