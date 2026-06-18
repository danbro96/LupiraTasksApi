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

        _session.SetHeader(EventActor.HeaderKey, caller.Actor);

        if (existing is null)
        {
            var newId = Guid.CreateVersion7();
            var sortOrder = NewSortOrder(commandId);
            var created = new ItemVtodoPut(
                newId, listId, uid, f.Title, f.Notes, f.DueAt, f.Completed, f.CompletedAt,
                tagIds, sortOrder, rawVtodo, occurredAt, commandId, f.Priority);
            _session.Events.StartStream<Item>(newId, created);
            await _session.SaveChangesAsync(ct);

            var item = await _session.LoadAsync<Item>(newId, ct);
            return OpResult<DavWriteResult>.Ok(new DavWriteResult(Created: true, Etag(item!)));
        }

        var stream = await _session.Events.FetchForWriting<Item>(existing.Id, ct);
        stream.AppendOne(new ItemVtodoPut(
            existing.Id, listId, uid, f.Title, f.Notes, f.DueAt, f.Completed, f.CompletedAt,
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

        _session.SetHeader(EventActor.HeaderKey, caller.Actor);
        var stream = await _session.Events.FetchForWriting<Item>(existing.Id, ct);
        stream.AppendOne(new ItemDeleted(existing.Id, DateTimeOffset.UtcNow, Guid.CreateVersion7()));
        await _session.SaveChangesAsync(ct);
        return OpResult.Ok();
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
