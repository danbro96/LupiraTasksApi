namespace LupiraTasksApi.Core.Dtos.Items;

/// <summary>
/// Body for complete/reopen/delete. Carries only the LWW timestamp; the item id is in
/// the route. <see cref="OccurredAt"/> defaults to server now when omitted.
/// </summary>
public sealed class ItemTimestampRequest
{
    /// <summary>Client wall-clock at the moment of the change (LWW key). Defaults to server now.</summary>
    public DateTimeOffset? OccurredAt { get; set; }
}
