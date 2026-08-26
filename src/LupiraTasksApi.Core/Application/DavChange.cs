namespace LupiraTasksApi.Core.Application;

/// <summary>An item whose state changed since a sync token: its resource UID and current ETag, or a tombstone.</summary>
public sealed record DavChange(string Uid, string? Etag, bool Deleted);
