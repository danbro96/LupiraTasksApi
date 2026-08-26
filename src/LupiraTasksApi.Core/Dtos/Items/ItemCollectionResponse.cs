namespace LupiraTasksApi.Core.Dtos.Items;

/// <summary>Envelope for a list's items.</summary>
public sealed class ItemCollectionResponse
{
    public required IReadOnlyList<ItemResponse> Items { get; set; }
}
