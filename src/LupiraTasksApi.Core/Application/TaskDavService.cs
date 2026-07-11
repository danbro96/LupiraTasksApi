using LupiraTasksApi.Auth;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Items;
using LupiraTasksApi.Domain.Lists;
using LupiraTasksApi.Ical;
using Marten;

namespace LupiraTasksApi.Application;

/// <summary>
/// The CalDAV (VTODO) core: enumerates and mutates items for the <c>/dav</c> surface, reusing the
/// same Marten <c>Item</c> streams, <see cref="AccessResolver"/>, and per-field LWW engine as REST/MCP
/// — so a phone edit and a web edit converge identically. Writes are addressed by the resource UID
/// (not the stream id); concurrency is governed by CalDAV ETag preconditions (the item's stream
/// <c>Version</c>), so this path deliberately bypasses the REST idempotency ledger.
/// </summary>
public sealed class TaskDavService
{
    private readonly IDocumentSession _session;
    private readonly AccessResolver _access;

    public TaskDavService(IDocumentSession session, AccessResolver access)
    {
        _session = session;
        _access = access;
    }

    /// <summary>The ETag for an item = its stream version, unquoted (the router adds the quotes).</summary>
    public static string Etag(Item item) => item.Version.ToString();

    /// <summary>All live (non-deleted) items in a list — the collection a CalDAV PROPFIND/REPORT enumerates.</summary>
    public async Task<IReadOnlyList<Item>> ItemsAsync(Guid listId, CancellationToken ct) =>
        await _session.Query<Item>().Where(i => i.ListId == listId && !i.Deleted).ToListAsync(ct);

    /// <summary>The live item addressed by its CalDAV resource UID within a list, or null.</summary>
    public async Task<Item?> FindAsync(Guid listId, string uid, CancellationToken ct)
    {
        var items = await _session.Query<Item>()
            .Where(i => i.ListId == listId && i.Uid == uid && !i.Deleted)
            .ToListAsync(ct);
        return items.FirstOrDefault();
    }

    public async Task<OpResult<DavWriteResult>> PutVtodoAsync(
        Caller caller, Guid listId, string uid, string rawVtodo, string? ifMatch, bool ifNoneMatchStar, CancellationToken ct)
    {
        var acc = await _access.AuthorizeAsync(caller, listId, ListRole.Editor, ct);
        if (!acc.Allowed) return OpResult<DavWriteResult>.NotFound();

        var existing = await FindAsync(listId, uid, ct);
        if (ifNoneMatchStar && existing is not null)
            return OpResult<DavWriteResult>.Conflict("Resource already exists.");
        if (ifMatch is not null && (existing is null || Etag(existing) != ifMatch))
            return OpResult<DavWriteResult>.Conflict("ETag mismatch.");

        VtodoFields f;
        try { f = VtodoMapper.Parse(rawVtodo); }
        catch (FormatException ex) { return OpResult<DavWriteResult>.Invalid(ex.Message); }

        var tagIds = MapCategoriesToTags(acc.List!, f.Categories);
        var occurredAt = DateTimeOffset.UtcNow;   // DAV carries no client wall-clock; the server clock orders LWW.
        var commandId = Guid.CreateVersion7();

        EventActor.Stamp(_session, caller.Actor, caller.ActorEmail, commandId);

        if (existing is null)
        {
            var newId = Guid.CreateVersion7();
            var sortOrder = NewSortOrder(commandId);
            var created = new ItemVtodoPut(
                newId, listId, uid, f.Title, f.Notes, f.DueAt, f.Status, f.CompletedAt,
                tagIds, sortOrder, rawVtodo, occurredAt, commandId, f.Priority);
            _session.Events.StartStream<Item>(newId, created);
            await _session.SaveChangesAsync(ct);

            var item = await _session.LoadAsync<Item>(newId, ct);
            return OpResult<DavWriteResult>.Ok(new DavWriteResult(Created: true, Etag(item!)));
        }

        var stream = await _session.Events.FetchForWriting<Item>(existing.Id, ct);
        stream.AppendOne(new ItemVtodoPut(
            existing.Id, listId, uid, f.Title, f.Notes, f.DueAt, f.Status, f.CompletedAt,
            tagIds, existing.SortOrder, rawVtodo, occurredAt, commandId, f.Priority));
        await _session.SaveChangesAsync(ct);

        var updated = await _session.LoadAsync<Item>(existing.Id, ct);
        return OpResult<DavWriteResult>.Ok(new DavWriteResult(Created: false, Etag(updated!)));
    }

    public async Task<OpResult> DeleteByUidAsync(Caller caller, Guid listId, string uid, string? ifMatch, CancellationToken ct)
    {
        var acc = await _access.AuthorizeAsync(caller, listId, ListRole.Editor, ct);
        if (!acc.Allowed) return OpResult.NotFound();

        var existing = await FindAsync(listId, uid, ct);
        if (existing is null) return OpResult.NotFound();
        if (ifMatch is not null && Etag(existing) != ifMatch) return OpResult.Conflict("ETag mismatch.");

        var commandId = Guid.CreateVersion7();
        EventActor.Stamp(_session, caller.Actor, caller.ActorEmail, commandId);
        var stream = await _session.Events.FetchForWriting<Item>(existing.Id, ct);
        stream.AppendOne(new ItemDeleted(existing.Id, DateTimeOffset.UtcNow, commandId));
        // Cascade: the item's cross-API relations are orphaned by the tombstone — drop them in the
        // same commit so dangling edges don't accrete (see RelationService / architecture.md).
        _session.DeleteWhere<Relation>(r => r.FromId == existing.Id);
        await _session.SaveChangesAsync(ct);
        return OpResult.Ok();
    }

    /// <summary>Viewer-level read gate for the DAV surface (deny reads as "not found" upstream).</summary>
    public async Task<bool> CanReadAsync(Caller caller, Guid listId, CancellationToken ct) =>
        (await _access.AuthorizeAsync(caller, listId, ListRole.Viewer, ct)).Allowed;

    /// <summary>The current sync token = the store's latest global event sequence.</summary>
    public async Task<long> CurrentTokenAsync(CancellationToken ct)
    {
        var last = await _session.Events.QueryAllRawEvents().OrderByDescending(e => e.Sequence).Take(1).ToListAsync(ct);
        return last.Count > 0 ? last[0].Sequence : 0L;
    }

    /// <summary>Changes in a list since <paramref name="since"/>; a null token yields the full live listing
    /// (self-healing resync). Deletions surface as tombstones only on incremental diffs; an item that was
    /// never in this list is skipped. 404-on-deny like the rest of the DAV surface.</summary>
    public async Task<OpResult<DavChangesResult>> ChangesSinceAsync(Caller caller, Guid listId, long? since, CancellationToken ct)
    {
        var acc = await _access.AuthorizeAsync(caller, listId, ListRole.Viewer, ct);
        if (!acc.Allowed) return OpResult<DavChangesResult>.NotFound();

        var newToken = await CurrentTokenAsync(ct);

        if (since is null)
        {
            var live = await ItemsAsync(listId, ct);
            return OpResult<DavChangesResult>.Ok(new DavChangesResult(
                newToken, [.. live.Select(i => new DavChange(i.Uid, Etag(i), Deleted: false))]));
        }

        var changedIds = (await _session.Events.QueryAllRawEvents().Where(e => e.Sequence > since).ToListAsync(ct))
            .Select(e => e.StreamId).Distinct().ToList();
        var items = await _session.Query<Item>().Where(i => changedIds.Contains(i.Id)).ToListAsync(ct);

        var changes = new List<DavChange>();
        foreach (var i in items)
        {
            if (i.ListId != listId) continue;   // never been in this list
            changes.Add(i.Deleted
                ? new DavChange(i.Uid, null, Deleted: true)
                : new DavChange(i.Uid, Etag(i), Deleted: false));
        }
        return OpResult<DavChangesResult>.Ok(new DavChangesResult(newToken, changes));
    }

    /// <summary>VTODO CATEGORIES → existing list tag ids (case-insensitive label match). Unknown labels
    /// are ignored in v1 to avoid uncontrolled tag creation from a phone.</summary>
    private static List<Guid> MapCategoriesToTags(TodoList list, IReadOnlyList<string> categories) =>
        categories
            .Select(label => list.Tags.Find(t => string.Equals(t.Label, label, StringComparison.OrdinalIgnoreCase)))
            .Where(t => t is not null)
            .Select(t => t!.Id)
            .Distinct()
            .ToList();

    /// <summary>A fractional-index sort key that places DAV-created items at the end, in creation order
    /// (the '~' prefix sorts after the client's lowercase keys; the v7 command id keeps it unique).</summary>
    private static string NewSortOrder(Guid commandId) => "~" + commandId.ToString("N");
}
