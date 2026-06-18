namespace LupiraTasksApi.Domain.Items;

/// <summary>
/// The mutable per-field state of an item, plus the last-writer-wins (LWW) guard
/// timestamps used to resolve out-of-order offline edits. This is a plain,
/// DB-free, framework-free type so the conflict-resolution rules in
/// <see cref="ItemLww"/> can be exercised by unit tests without Postgres or Marten.
///
/// The Marten <c>Item</c> snapshot composes one of these and exposes its fields.
/// </summary>
public sealed class ItemState
{
    // --- Snapshot fields ---
    public Guid Id { get; set; }
    public Guid ListId { get; set; }
    public Guid? ParentItemId { get; set; }

    public string Title { get; set; } = "";
    public string? Notes { get; set; }

    public bool Completed { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CompletedBy { get; set; }

    public string? AssignedTo { get; set; }
    public DateTimeOffset? DueAt { get; set; }

    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }

    public List<Guid> Tags { get; set; } = [];

    public string SortOrder { get; set; } = "";

    /// <summary>
    /// The stable external identifier used as the CalDAV resource name (<c>{Uid}.ics</c>).
    /// Never null once created: a DAV-created item keeps the client-supplied VTODO UID; an
    /// item created on any other surface defaults it to <c>Id.ToString()</c> (set in
    /// <see cref="ItemLww.ApplyAdded"/>). Must round-trip verbatim so a DAV client's local
    /// resource URL stays valid across syncs.
    /// </summary>
    public string Uid { get; set; } = "";

    /// <summary>
    /// The raw VTODO blob from the last CalDAV PUT, kept so properties this model doesn't
    /// represent (PRIORITY, RRULE, X-*, …) survive a phone→server→phone round-trip. Null for
    /// items never written over DAV.
    /// </summary>
    public string? SourceVtodo { get; set; }

    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool Deleted { get; set; }

    // --- Per-field LWW guards ---
    // Each field's guard is the (OccurredAt, CommandId) of the event that last set it.
    // OccurredAt is the primary key; CommandId is the deterministic tiebreaker on an
    // exact OccurredAt tie, so concurrent same-field edits converge identically on the
    // server snapshot and the offline client reducer regardless of apply order.
    public DateTimeOffset NameTs { get; set; }
    public Guid NameCmd { get; set; }
    public DateTimeOffset NotesTs { get; set; }
    public Guid NotesCmd { get; set; }
    public DateTimeOffset AssigneeTs { get; set; }
    public Guid AssigneeCmd { get; set; }
    public DateTimeOffset DueTs { get; set; }
    public Guid DueCmd { get; set; }
    public DateTimeOffset QtyTs { get; set; }
    public Guid QtyCmd { get; set; }
    public DateTimeOffset CompletedTs { get; set; }
    public Guid CompletedCmd { get; set; }
    public DateTimeOffset MoveTs { get; set; }
    public Guid MoveCmd { get; set; }

    /// <summary>Guard for the raw <see cref="SourceVtodo"/> blob written by a CalDAV PUT.</summary>
    public DateTimeOffset VtodoTs { get; set; }
    public Guid VtodoCmd { get; set; }

    /// <summary>
    /// Per-tag last-touched guard (OccurredAt + tiebreaker CommandId), so a tag add and
    /// remove that arrive out of order converge on whichever has the later
    /// (OccurredAt, CommandId) — a commutative set op.
    /// </summary>
    public Dictionary<Guid, DateTimeOffset> TagTs { get; set; } = [];
    public Dictionary<Guid, Guid> TagCmd { get; set; } = [];
}
