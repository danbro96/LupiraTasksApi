using LupiraTasksApi.Core.Domain;

namespace LupiraTasksApi.Core.Dtos.Items;

/// <summary>Sets an item's lifecycle status, with an optional reason (e.g. why it's Blocked/Waiting).</summary>
public sealed class SetStatusRequest
{
    public required ItemStatus Status { get; set; }
    public string? Reason { get; set; }

    /// <summary>Client wall-clock for LWW; defaults to server now when omitted.</summary>
    public DateTimeOffset? OccurredAt { get; set; }
}
