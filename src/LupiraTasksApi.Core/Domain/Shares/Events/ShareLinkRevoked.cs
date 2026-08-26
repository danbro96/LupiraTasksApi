namespace LupiraTasksApi.Core.Domain.Shares.Events;

/// <summary>The link was revoked; the token is rejected on its next use. Revoker is in the <c>actor</c> header.</summary>
public sealed record ShareLinkRevoked(Guid ShareId, string Reason);
