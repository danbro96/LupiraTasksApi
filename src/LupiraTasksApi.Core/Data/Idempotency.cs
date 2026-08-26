using JasperFx;
using LupiraTasksApi.Core.Domain;
using Marten;

namespace LupiraTasksApi.Core.Data;

/// <summary>
/// Offline-first idempotency gate. A mutation may carry an <c>Idempotency-Key</c> header (a client-minted GUIDv7
/// <c>command_id</c>); the serialized replay worker resends the same key after a lost response, so a redelivered
/// command must be a no-op returning the prior result.
/// <para>The dedup row and the event append share ONE <see cref="IDocumentSession"/> and ONE
/// <c>SaveChangesAsync</c>. The ledger row goes in via <see cref="IDocumentSession.Insert{T}"/> — a plain INSERT,
/// not an upsert — so a concurrent duplicate violates the <c>ProcessedCommand</c> primary key and rolls back the
/// whole transaction including the loser's already-staged events; the loser catches it and returns the existing
/// aggregate. This closes the check-then-write TOCTOU an upsert plus a version-less append would leave open.</para>
/// <para>With no key the append simply commits — clients should always send one, but the API stays usable
/// without, at the cost of no cross-request dedup.</para>
/// </summary>
public sealed class Idempotency
{
    private readonly IDocumentSession _session;

    public Idempotency(IDocumentSession session)
    {
        _session = session;
    }

    /// <summary>
    /// Returns the <see cref="ProcessedCommand"/> already recorded for
    /// <paramref name="commandId"/>, or <c>null</c> if this command is new (or no key was
    /// supplied). Handlers call this first; a non-null result means the mutation already
    /// happened and they should return the existing aggregate with no new event.
    /// </summary>
    public async Task<ProcessedCommand?> SeenAsync(Guid? commandId, CancellationToken ct) =>
        commandId is { } key ? await _session.LoadAsync<ProcessedCommand>(key, ct) : null;

    /// <summary>
    /// Appends events to an existing stream and records the dedup ledger row in the same session, committed by a
    /// single <c>SaveChangesAsync</c>. Returns the resulting version, or <c>null</c> when the commit lost the race.
    /// <para>The version comes from the stream's CURRENT head (<c>FetchStreamStateAsync</c>, not the loaded
    /// snapshot's possibly-stale <c>Version</c>) plus the event count, stored in
    /// <see cref="ProcessedCommand.ResultVersion"/>. A pre-save <c>StreamAction.Version</c> would not do: the
    /// Quick append modes assign it server-side at INSERT, so it reads 0 beforehand.</para>
    /// <para>A concurrent duplicate key throws <see cref="DocumentAlreadyExistsException"/> (or a Postgres
    /// unique-violation) out of <c>SaveChangesAsync</c>, rolling back the appended events; the caller returns the
    /// already-committed aggregate.</para>
    /// </summary>
    public async Task<int?> AppendDedupAsync(
        Guid? commandId,
        Guid aggregateId,
        IReadOnlyList<object> events,
        CancellationToken ct)
    {
        // Real current head (not the loaded snapshot's possibly-stale version).
        var state = await _session.Events.FetchStreamStateAsync(aggregateId, ct);
        var version = (int) (state?.Version ?? 0) + events.Count;

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
