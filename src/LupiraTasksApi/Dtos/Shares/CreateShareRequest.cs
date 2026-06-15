using LupiraTasksApi.Domain;

namespace LupiraTasksApi.Dtos.Shares;

/// <summary>
/// Mint a share link for a list. <see cref="Access"/> picks read vs read/write; an optional
/// <see cref="ExpiresAt"/> auto-expires the link; <see cref="Label"/> is a human name used for
/// attribution of writes (<c>share:{label}</c>) and shown in the owner's list of links.
/// </summary>
public sealed class CreateShareRequest
{
    public required ShareAccess Access { get; set; }
    public string? Label { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
