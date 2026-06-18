namespace LupiraTasksApi.Dtos.Items;

/// <summary>
/// Patch an item. Emits exactly one event per changed field. A field changes only when
/// its <c>*Provided</c> flag is true, so a <c>null</c> value can be sent intentionally
/// (e.g. unassign, clear due date) without being confused with "leave unchanged".
/// <see cref="OccurredAt"/> is the client wall-clock used for per-field LWW.
/// </summary>
public sealed class UpdateItemRequest
{
    public string? Title { get; set; }
    public bool TitleProvided { get; set; }

    public string? Notes { get; set; }
    public bool NotesProvided { get; set; }

    public DateTimeOffset? DueAt { get; set; }
    public bool DueAtProvided { get; set; }

    public string? AssigneeEmail { get; set; }
    public bool AssigneeEmailProvided { get; set; }

    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public bool QuantityProvided { get; set; }

    /// <summary>Standard iCalendar priority, 0..9 (0 = none). Only applied when <see cref="PriorityProvided"/> is true.</summary>
    public int Priority { get; set; }
    public bool PriorityProvided { get; set; }

    /// <summary>Tag ids to add (commutative delta).</summary>
    public IReadOnlyList<Guid>? AddTagIds { get; set; }

    /// <summary>Tag ids to remove (commutative delta).</summary>
    public IReadOnlyList<Guid>? RemoveTagIds { get; set; }

    /// <summary>Client wall-clock at the moment of the change (LWW key). Defaults to server now.</summary>
    public DateTimeOffset? OccurredAt { get; set; }
}
