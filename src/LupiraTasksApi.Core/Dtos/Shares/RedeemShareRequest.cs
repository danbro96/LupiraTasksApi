using LupiraTasksApi.Domain;

namespace LupiraTasksApi.Dtos.Shares;

/// <summary>Redeem a share link as the authenticated caller (the token, not in the URL path here).</summary>
public sealed class RedeemShareRequest
{
    public required string Token { get; set; }
}

/// <summary>The list the caller joined and the role they now hold on it.</summary>
public sealed class RedeemShareResponse
{
    public required Guid ListId { get; set; }
    public required ListRole Role { get; set; }
}
