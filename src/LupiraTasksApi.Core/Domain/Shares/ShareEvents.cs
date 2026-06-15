namespace LupiraTasksApi.Domain.Shares;

/// <summary>
/// A public share link was minted for a list. The <see cref="Token"/> is the opaque secret a
/// recipient presents in the URL; <see cref="ShareId"/> (the stream id) is the non-secret handle
/// used in owner-facing management. The creator is carried out-of-band in the <c>actor</c> header.
/// </summary>
public record ShareLinkCreated(
    Guid ShareId,
    Guid ListId,
    string Token,
    ShareAccess Access,
    string Label,
    DateTimeOffset? ExpiresAt);

/// <summary>The link was revoked; the token is rejected on its next use. Revoker is in the <c>actor</c> header.</summary>
public record ShareLinkRevoked(Guid ShareId, string Reason);
