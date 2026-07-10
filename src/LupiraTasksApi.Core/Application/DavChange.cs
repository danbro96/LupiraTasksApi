namespace LupiraTasksApi.Application;

/// <summary>An item whose state changed since a sync token: its resource UID and current ETag, or a tombstone.</summary>
public sealed record DavChange(string Uid, string? Etag, bool Deleted);

/// <summary>The changes in a list since a sync token, plus the new token (Marten's global event sequence,
/// opaque to the DAV gateway).</summary>
public sealed record DavChangesResult(long Token, IReadOnlyList<DavChange> Changes);
