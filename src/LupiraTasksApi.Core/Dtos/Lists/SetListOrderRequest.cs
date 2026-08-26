namespace LupiraTasksApi.Core.Dtos.Lists;

/// <summary>
/// Set the caller's own position for a list in their lists screen. <see cref="SortOrder"/> is a
/// fractional-index string (same scheme as item ordering) generated client-side between the
/// neighbours it was dropped between. Per-user: other members are unaffected.
/// </summary>
public sealed class SetListOrderRequest
{
    public required string SortOrder { get; set; }
}
