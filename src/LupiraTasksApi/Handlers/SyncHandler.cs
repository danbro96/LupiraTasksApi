using Lupira.Hosting.Problems;
using Lupira.Sync;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Core.Application.Dav;
using LupiraTasksApi.Core.Application.Sync;
using LupiraTasksApi.Core.Dtos.Lists;
using LupiraTasksApi.Core.Dtos.Sync;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LupiraTasksApi.Handlers;

/// <summary>
/// REST adapter for the offline mirrors: resolves the caller from the JWT and delegates
/// to the transport-neutral <see cref="SyncFeed"/> and <see cref="SyncService"/>.
/// </summary>
public sealed class SyncHandler
{
    private readonly CallerFactory _callers;
    private readonly SyncService _sync;
    private readonly SyncFeed _feed;

    public SyncHandler(CallerFactory callers, SyncService sync, SyncFeed feed)
    {
        _callers = callers;
        _sync = sync;
        _feed = feed;
    }

    public async Task<Results<Ok<SyncResponse>, NotFound, UnauthorizedHttpResult>> GetAsync(
        Guid listId,
        long? since,
        CancellationToken ct)
    {
        var caller = await _callers.MemberAsync(ct);
        if (caller is null) return TypedResults.Unauthorized();
        return OpResultMap.OkNotFound(await _sync.GetAsync(caller, listId, since, ct));
    }

    public async Task<Results<Ok<SyncPage<ListDto>>, ProblemHttpResult, UnauthorizedHttpResult>> ListsAsync(
        string? since,
        int? limit,
        CancellationToken ct)
    {
        var caller = await _callers.MemberAsync(ct);
        if (caller is null) return TypedResults.Unauthorized();
        return OpResultMap.OkProblem(await _feed.ListsAsync(caller, since, limit, ct));
    }

    public async Task<Results<Ok<SyncPage<ItemSyncChange>>, ProblemHttpResult, UnauthorizedHttpResult>> ItemsAsync(
        string? since,
        int? limit,
        CancellationToken ct)
    {
        var caller = await _callers.MemberAsync(ct);
        if (caller is null) return TypedResults.Unauthorized();
        return OpResultMap.OkProblem(await _feed.ItemsAsync(caller, since, limit, ct));
    }
}
