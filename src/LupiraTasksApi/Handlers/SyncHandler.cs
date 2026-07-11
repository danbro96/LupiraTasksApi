using LupiraTasksApi.Application;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Dtos.Sync;
using LupiraTasksApi.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LupiraTasksApi.Handlers;

/// <summary>
/// REST adapter for the offline delta-pull: resolves the caller from the JWT and delegates
/// to the transport-neutral <see cref="SyncService"/>.
/// </summary>
public sealed class SyncHandler
{
    private readonly CallerFactory _callers;
    private readonly SyncService _sync;

    public SyncHandler(CallerFactory callers, SyncService sync)
    {
        _callers = callers;
        _sync = sync;
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
}
