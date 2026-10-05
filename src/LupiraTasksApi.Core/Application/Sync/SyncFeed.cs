using Lupira.Identity.Marten;
using Lupira.Results;
using Lupira.Sync;
using Lupira.Sync.Marten;
using LupiraTasksApi.Core.Auth;
using LupiraTasksApi.Core.Domain.Items;
using LupiraTasksApi.Core.Domain.Lists;
using LupiraTasksApi.Core.Dtos.Lists;
using LupiraTasksApi.Core.Dtos.Sync;
using LupiraTasksApi.Core.Mappers;
using Marten;
using Microsoft.Extensions.Options;

namespace LupiraTasksApi.Core.Application.Sync;

/// <summary>
/// The offline-mirror feeds over the lists a member can read and their items. The cursor's scope hashes the
/// readable list ids, so gaining or losing a list (a share, a removal, a deleted list) restarts both feeds;
/// deltas therefore only ever report lists and items inside an unchanged scope.
/// </summary>
public sealed class SyncFeed
{
    private readonly IDocumentSession _session;
    private readonly AccessResolver _access;
    private readonly PrincipalDirectory _principals;
    private readonly TimeSpan _settleLag;

    public SyncFeed(IDocumentSession session, AccessResolver access, PrincipalDirectory principals, IOptions<SyncFeedOptions> options)
    {
        _session = session;
        _access = access;
        _principals = principals;
        _settleLag = options.Value.SettleLag;
    }

    public async Task<OpResult<SyncPage<ListDto>>> ListsAsync(Caller caller, string? since, int? limit, CancellationToken ct)
    {
        if (!SyncFeedQuery.TryParse(since, limit, out var query))
            return OpResult<SyncPage<ListDto>>.Invalid(SyncFeedQuery.InvalidSince);

        var principalId = caller.PrincipalId!.Value;
        var readable = await _access.ReadableListIdsAsync(principalId, ct);
        var scope = SyncCursor.ScopeOf(readable);

        if (query.IsFullSync(scope))
        {
            var head = await FullSyncHeadAsync(query, scope, ct);
            var lists = _session.Query<TodoList>().Where(l => readable.Contains(l.Id));
            if (FullSyncAfter(query, scope) is { } after) lists = lists.Where(l => l.Id > after);
            var rows = await lists.OrderBy(l => l.Id).Take(query.Limit + 1).ToListAsync(ct);
            var page = rows.Take(query.Limit).ToList();
            Guid? nextAfter = rows.Count > query.Limit ? page[^1].Id : null;
            return OpResult<SyncPage<ListDto>>.Ok(
                FullSyncPage(query, scope, head, nextAfter, await ToListDtosAsync(page, principalId, ct)));
        }

        var changes = await _session.ChangedStreamsAsync<TodoList>(query.Since!.Value.Sequence, query.Limit, _settleLag, ct);
        var changed = (await _session.LoadManyAsync<TodoList>(ct, changes.Ids)).Where(l => readable.Contains(l.Id)).ToList();
        return OpResult<SyncPage<ListDto>>.Ok(
            DeltaPage(scope, changes, await ToListDtosAsync(changed, principalId, ct), deleted: []));
    }

    public async Task<OpResult<SyncPage<ItemSyncChange>>> ItemsAsync(Caller caller, string? since, int? limit, CancellationToken ct)
    {
        if (!SyncFeedQuery.TryParse(since, limit, out var query))
            return OpResult<SyncPage<ItemSyncChange>>.Invalid(SyncFeedQuery.InvalidSince);

        var readable = await _access.ReadableListIdsAsync(caller.PrincipalId!.Value, ct);
        var scope = SyncCursor.ScopeOf(readable);

        if (query.IsFullSync(scope))
        {
            var head = await FullSyncHeadAsync(query, scope, ct);
            var items = _session.Query<Item>().Where(i => readable.Contains(i.ListId) && !i.Deleted);
            if (FullSyncAfter(query, scope) is { } after) items = items.Where(i => i.Id > after);
            var rows = await items.OrderBy(i => i.Id).Take(query.Limit + 1).ToListAsync(ct);
            var page = rows.Take(query.Limit).ToList();
            Guid? nextAfter = rows.Count > query.Limit ? page[^1].Id : null;
            return OpResult<SyncPage<ItemSyncChange>>.Ok(
                FullSyncPage(query, scope, head, nextAfter, await ToItemChangesAsync(page, ct)));
        }

        // Items never change list, so one outside the scope was never in this mirror.
        var changes = await _session.ChangedStreamsAsync<Item>(query.Since!.Value.Sequence, query.Limit, _settleLag, ct);
        var inScope = (await _session.LoadManyAsync<Item>(ct, changes.Ids)).Where(i => readable.Contains(i.ListId)).ToList();
        var live = inScope.Where(i => !i.Deleted).ToList();
        var deleted = inScope.Where(i => i.Deleted).Select(i => i.Id).ToList();
        return OpResult<SyncPage<ItemSyncChange>>.Ok(DeltaPage(scope, changes, await ToItemChangesAsync(live, ct), deleted));
    }

    private static Guid? FullSyncAfter(SyncFeedQuery query, string scope) =>
        query.IsReset(scope) ? null : query.Since!.Value.After;

    private static SyncPage<T> FullSyncPage<T>(SyncFeedQuery query, string scope, long head, Guid? nextAfter, List<T> changed) => new()
    {
        Cursor = new SyncCursor(head, scope) { After = nextAfter }.ToString(),
        HasMore = nextAfter is not null,
        Reset = query.IsReset(scope),
        Changed = changed,
        Deleted = [],
    };

    private static SyncPage<T> DeltaPage<T>(string scope, ChangedStreams changes, List<T> changed, List<Guid> deleted) => new()
    {
        Cursor = new SyncCursor(changes.NextSequence, scope).ToString(),
        HasMore = changes.HasMore,
        Reset = false,
        Changed = changed,
        Deleted = deleted,
    };

    /// <summary>Read before the first page, so anything written during the full sync arrives in the next delta.</summary>
    private async Task<long> FullSyncHeadAsync(SyncFeedQuery query, string scope, CancellationToken ct) =>
        query.IsReset(scope) ? await _session.HeadSequenceAsync(_settleLag, ct) : query.Since!.Value.Sequence;

    private async Task<List<ListDto>> ToListDtosAsync(IReadOnlyList<TodoList> lists, Guid principalId, CancellationToken ct)
    {
        var lookup = await _principals.LookupAsync(lists.SelectMany(ListMapper.PrincipalIdsOf), ct);
        return [.. lists.Select(l => l.ToResponse(lookup, principalId))];
    }

    private async Task<List<ItemSyncChange>> ToItemChangesAsync(IReadOnlyList<Item> items, CancellationToken ct)
    {
        var lookup = await _principals.LookupAsync(items.SelectMany(ItemMapper.PrincipalIdsOf), ct);
        return [.. items.Select(i => new ItemSyncChange { Item = i.ToResponse(lookup), Guards = i.State.ToGuards() })];
    }
}
