namespace LupiraTasksApi.Core.Dtos.Lists;

/// <summary>Envelope for the caller's lists.</summary>
public sealed class ListCollectionResponse
{
    public required IReadOnlyList<ListResponse> Lists { get; set; }
}
