namespace LupiraTasksApi.Core.Application.Dav;

/// <summary>The changes in a list since a sync token, plus the new token (Marten's global event sequence,
/// opaque to the DAV gateway).</summary>
public sealed record DavChangesResult(long Token, IReadOnlyList<DavChange> Changes);
