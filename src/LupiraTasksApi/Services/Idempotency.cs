using JasperFx;
using LupiraTasksApi.Domain;
using Marten;

namespace LupiraTasksApi.Services;

/// <summary>
/// Offline-first idempotency gate. Every mutation may carry an <c>Idempotency-Key</c>
/// header (a client-generated GUIDv7 <c>command_id</c>); the serialized replay worker
/// resends the same key on retry after a lost response, so a redelivered command must
/// be a no-op that returns the prior result.
///
/// <para>
/// The dedup row and the event append live in the SAME <see cref="IDocumentSession"/>,
/// committed by a SINGLE <c>SaveChangesAsync</c>. The ledger row is written with
/// <see cref="IDocumentSession.Insert{T}"/> — a plain INSERT, NOT an upsert — so a
/// second concurrent command bearing the same key violates the <c>ProcessedCommand</c>
/// primary key and rolls back the WHOLE transaction (including the loser's already-staged
/// events). Only one writer wins; the loser catches the duplicate and treats it as
/// idempotent success (re-resolve and return the existing aggregate). This closes the
/// check-then-write TOCTOU that an upsert + version-less append would leave open.
/// </para>
///
/// <para>
/// With no key present the append simply commits — clients are expected to always send
/// one, but the API stays usable without it (at the cost of no cross-request dedup).
/// </para>
/// </summary>
public sealed class Idempotency
{
    public const string HeaderName = "Idempotency-Key";

    private readonly IDocumentSession _session;

    public Idempotency(IDocumentSession session)
    {
        _session = session;
    }

    /// <summary>Reads the <c>Idempotency-Key</c> header as a GUID, or <c>null</c> if absent/malformed.</summary>
    public static Guid? KeyFrom(HttpContext ctx) =>
        ctx.Request.Headers.TryGetValue(HeaderName, out var raw)
        && Guid.TryParse(raw.ToString(), out var key)
            ? key
            : null;

    /// <summary>
    /// Returns the <see cref="ProcessedCommand"/> already recorded for
    /// <paramref name="commandId"/>, or <c>null</c> if this command is new (or no key was
    /// supplied). Handlers call this first; a non-null result means the mutation already
    /// happened and they should return the existing aggregate with no new event.
    /// </summary>
    public async Task<ProcessedCommand?> SeenAsync(Guid? commandId, CancellationToken ct) =>
        commandId is { } key ? await _session.LoadAsync<ProcessedCommand>(key, ct) : null;

    /// <summary>
    /// Append events to an existing stream and, in the same session, record the dedup
    /// ledger row, then commit with a single <c>SaveChangesAsync</c>.
    /// <para>
    /// The resulting version is computed from the stream's CURRENT head — read fresh via
    /// <c>FetchStreamStateAsync</c> rather than the snapshot's possibly-stale loaded
    /// <c>Version</c> — plus the number of events appended, and stored in
    /// <see cref="ProcessedCommand.ResultVersion"/>. (A pre-save <c>StreamAction.Version</c>
    /// is unreliable: under the Quick append modes the version is assigned server-side at
    /// INSERT time and reads as 0 beforehand.)
    /// </para>
    /// <para>
    /// On a concurrent duplicate key the <c>Insert</c> of the ledger row throws
    /// <see cref="DocumentAlreadyExistsException"/> (or a Postgres unique-violation) from
    /// <c>SaveChangesAsync</c>, rolling back the appended events; the caller catches it and
    /// returns the already-committed aggregate. Returns the resulting version, or
    /// <c>null</c> when the commit lost the dedup race.
    /// </para>
    /// </summary>
    public async Task<int?> AppendDedupAsync(
        Guid? commandId,
        Guid aggregateId,
        IReadOnlyList<object> events,
        CancellationToken ct)
    {
        // Real current head (not the loaded snapshot's possibly-stale version).
        var state = await _session.Events.FetchStreamStateAsync(aggregateId, ct);
        var version = (int)(state?.Version ?? 0) + events.Count;

        _session.Events.Append(aggregateId, events.ToArray());
        Record(commandId, aggregateId, version);
        try
        {
            await _session.SaveChangesAsync(ct);
        }
        catch (DocumentAlreadyExistsException)
        {
            // Lost the dedup race: another request with the same key committed first.
            // Its events are authoritative; our staged append rolled back. Idempotent.
            return null;
        }
        return version;
    }

    /// <summary>
    /// Record the dedup ledger row with a fail-fast <c>Insert</c> (plain INSERT, not an
    /// upsert): a duplicate <paramref name="commandId"/> rolls back the whole
    /// <c>SaveChangesAsync</c> so only one writer wins. <paramref name="version"/> is the
    /// resulting stream version. Does not save — the caller owns the single commit so the
    /// duplicate can be caught around it.
    /// </summary>
    public void Record(Guid? commandId, Guid aggregateId, int version)
    {
        if (commandId is { } id)
        {
            _session.Insert(new ProcessedCommand
            {
                CommandId = id,
                AggregateId = aggregateId,
                ResultVersion = version,
                ProcessedAt = DateTimeOffset.UtcNow,
            });
        }
    }
}
