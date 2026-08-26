using LupiraTasksApi.Core.Domain;

namespace LupiraTasksApi.Core.Dtos.Shares;

/// <summary>A share link as the owner sees it, including the opaque <see cref="Token"/> and ready-to-copy <see cref="Url"/>.</summary>
public sealed class ShareDto
{
    public required Guid ShareId { get; set; }

    public required string Token { get; set; }

    public required string Url { get; set; }

    public required ShareAccess Access { get; set; }

    public required string Label { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public required bool Revoked { get; set; }
}
