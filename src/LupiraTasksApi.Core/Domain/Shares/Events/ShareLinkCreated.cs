namespace LupiraTasksApi.Core.Domain.Shares.Events;

/// <summary>
/// A public share link was minted for a list. The <see cref="Token"/> is the opaque secret a
/// recipient presents in the URL; <see cref="ShareId"/> (the stream id) is the non-secret handle
/// used in owner-facing management. The creator is carried out-of-band in the <c>actor</c> header.
/// </summary>
public sealed record ShareLinkCreated(
    Guid ShareId,
    Guid ListId,
    string Token,
    ShareAccess Access,
    string Label,
    DateTimeOffset? ExpiresAt);
