using LupiraTasksApi.Auth;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Identity;
using LupiraTasksApi.Domain.Items;
using LupiraTasksApi.Domain.Lists;
using LupiraTasksApi.Dtos.Sync;
using LupiraTasksApi.Mappers;
using Marten;

namespace LupiraTasksApi.Application;

/// <summary>
/// Offline delta-pull. v1 is the simplest-correct shape: return the current list plus all
/// its live items (full re-derive) regardless of the <c>since</c> cursor, and report a
/// <c>nextCursor</c> the client plumbs back on the next pull. The client rebases its local
/// mirror onto this base, then re-applies its non-acked outbox rows. Read-only; not exposed
/// over MCP (offline-sync is a client concern).
/// </summary>
public sealed class SyncService
{
    private readonly IDocumentSession _session;
    private readonly AccessResolver _access;
    private readonly PrincipalDirectory _principals;

    public SyncService(IDocumentSession session, AccessResolver access, PrincipalDirectory principals)
    {
        _session = session;
        _access = access;
        _principals = principals;
    }

    public async Task<OpResult<SyncResponse>> GetAsync(Caller caller, Guid listId, long? since, CancellationToken ct)
    {
        // Member-only surface: offline delta-pull is never reached by a share-link caller.
        var access = await _access.RequireMembershipAsync(listId, caller.PrincipalId!.Value, ListRole.Viewer, ct);
        if (!access.Allowed) return OpResult<SyncResponse>.NotFound();

        // `since` is accepted and plumbed through, but v1 always re-derives the whole list.
        _ = since;

        var items = (await _session.Query<Item>()
                .Where(i => i.ListId == listId && !i.Deleted)
                .ToListAsync(ct))
            .OrderBy(i => i.SortOrder, StringComparer.Ordinal)
            .ToList();

        // One lookup resolves every principal id referenced by the list and its items.
        var lookup = await _principals.LookupAsync(PrincipalIds(access.List!, items), ct);
        var ordered = items.Select(i => i.ToResponse(lookup)).ToList();
        var nextCursor = ordered.Count == 0 ? 0L : ordered.Max(i => (long)i.Version);

        return OpResult<SyncResponse>.Ok(new SyncResponse
        {
            List = access.List!.ToResponse(lookup, caller.PrincipalId!.Value),
            Items = ordered,
            NextCursor = nextCursor,
        });
    }

    private static IEnumerable<Guid> PrincipalIds(TodoList list, IEnumerable<Item> items)
    {
        yield return list.OwnerPrincipalId;
        foreach (var m in list.Members)
        {
            yield return m.PrincipalId;
            if (Guid.TryParse(m.AddedBy, out var addedBy)) yield return addedBy;
        }
        foreach (var i in items)
        {
            if (i.AssignedToPrincipalId is { } a) yield return a;
            if (Guid.TryParse(i.CreatedBy, out var c)) yield return c;
            if (Guid.TryParse(i.CompletedBy, out var d)) yield return d;
        }
    }
}
