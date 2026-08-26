namespace LupiraTasksApi.Core.Dtos.Shares;

/// <summary>Envelope for a list's active share links.</summary>
public sealed class ShareCollectionResponse
{
    public required IReadOnlyList<ShareDto> Shares { get; set; }
}
