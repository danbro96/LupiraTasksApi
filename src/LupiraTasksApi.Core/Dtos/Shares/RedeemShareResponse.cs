using LupiraTasksApi.Core.Domain;

namespace LupiraTasksApi.Core.Dtos.Shares;

/// <summary>The list the caller joined and the role they now hold on it.</summary>
public sealed class RedeemShareResponse
{
    public required Guid ListId { get; set; }
    public required ListRole Role { get; set; }
}
