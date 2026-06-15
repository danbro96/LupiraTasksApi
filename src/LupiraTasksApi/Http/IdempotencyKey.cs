namespace LupiraTasksApi.Http;

/// <summary>
/// Reads the client's <c>Idempotency-Key</c> header off the request. This is the HTTP-boundary
/// half of idempotency; the dedup mechanism itself lives in the transport-neutral
/// <c>Application</c>/<c>Idempotency</c> service, which takes the resulting <see cref="System.Guid"/>.
/// </summary>
internal static class IdempotencyKey
{
    public const string HeaderName = "Idempotency-Key";

    /// <summary>The <c>Idempotency-Key</c> header as a GUID, or <c>null</c> if absent/malformed.</summary>
    public static Guid? From(HttpContext ctx) =>
        ctx.Request.Headers.TryGetValue(HeaderName, out var raw)
        && Guid.TryParse(raw.ToString(), out var key)
            ? key
            : null;
}
