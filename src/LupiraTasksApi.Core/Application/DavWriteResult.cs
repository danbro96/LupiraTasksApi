namespace LupiraTasksApi.Application;

/// <summary>
/// Outcome of a CalDAV VTODO PUT: whether the resource was created (→ 201) vs updated (→ 204),
/// plus its new ETag (the item's stream <c>Version</c>, unquoted — the router wraps it in quotes).
/// </summary>
public readonly record struct DavWriteResult(bool Created, string Etag);
