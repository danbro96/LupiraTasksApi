using LupiraTasksApi.Application;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Dtos.Shares;
using LupiraTasksApi.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LupiraTasksApi.Handlers;

/// <summary>
/// REST adapter for owner-side share-link management: resolves the caller from the JWT and
/// delegates to <see cref="ShareService"/> (owner-only). Public consumption of a link lives on the
/// separate <c>/shared/{token}</c> surface.
/// </summary>
public sealed class SharesHandler
{
    private readonly CurrentUser _user;
    private readonly ShareService _shares;

    public SharesHandler(CurrentUser user, ShareService shares)
    {
        _user = user;
        _shares = shares;
    }

    public async Task<Results<Ok<ShareResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> CreateAsync(
        HttpContext ctx, Guid listId, CreateShareRequest request, CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFoundProblem(await _shares.CreateAsync(caller, IdempotencyKey.From(ctx), listId, request, ct));
    }

    public async Task<Results<Ok<ShareCollectionResponse>, NotFound, UnauthorizedHttpResult>> ListAsync(
        Guid listId, CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFound(await _shares.ListAsync(caller, listId, ct));
    }

    public async Task<Results<NoContent, NotFound, UnauthorizedHttpResult>> RevokeAsync(
        HttpContext ctx, Guid listId, Guid shareId, CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.NoContentNotFound(await _shares.RevokeAsync(caller, IdempotencyKey.From(ctx), listId, shareId, ct));
    }

    public async Task<Results<Ok<RedeemShareResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> RedeemAsync(
        HttpContext ctx, RedeemShareRequest request, CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFoundProblem(await _shares.RedeemAsync(caller, IdempotencyKey.From(ctx), request.Token, ct));
    }
}
