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
    private readonly CurrentUser _user;
    private readonly SyncService _sync;

    public SyncHandler(CurrentUser user, SyncService sync)
    {
        _user = user;
        _sync = sync;
    }

    public async Task<Results<Ok<SyncResponse>, NotFound, UnauthorizedHttpResult>> GetAsync(
        Guid listId,
        long? since,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFound(await _sync.GetAsync(caller, listId, since, ct));
    }
}
