using LupiraTasksApi.Auth;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Items;
using LupiraTasksApi.Mappers;
using LupiraTasksApi.Models.Sync;
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

    public SyncService(IDocumentSession session, AccessResolver access)
    {
        _session = session;
        _access = access;
    }

    public async Task<OpResult<SyncResponse>> GetAsync(Caller caller, Guid listId, long? since, CancellationToken ct)
    {
        var access = await _access.RequireMembershipAsync(listId, caller.Email, ListRole.Viewer, ct);
        if (!access.Allowed) return OpResult<SyncResponse>.NotFound();

        // `since` is accepted and plumbed through, but v1 always re-derives the whole list.
        _ = since;

        var items = await _session.Query<Item>()
            .Where(i => i.ListId == listId && !i.Deleted)
            .ToListAsync(ct);

        var ordered = items
            .OrderBy(i => i.SortOrder, StringComparer.Ordinal)
            .Select(i => i.ToResponse())
            .ToList();

        var nextCursor = ordered.Count == 0 ? 0L : ordered.Max(i => (long)i.Version);

        return OpResult<SyncResponse>.Ok(new SyncResponse
        {
            List = access.List!.ToResponse(),
            Items = ordered,
            NextCursor = nextCursor,
        });
    }
}
