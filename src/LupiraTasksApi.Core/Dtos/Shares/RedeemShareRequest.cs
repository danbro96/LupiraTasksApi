namespace LupiraTasksApi.Core.Dtos.Shares;

/// <summary>Redeem a share link as the authenticated caller (the token, not in the URL path here).</summary>
public sealed class RedeemShareRequest
{
    public required string Token { get; set; }
}
