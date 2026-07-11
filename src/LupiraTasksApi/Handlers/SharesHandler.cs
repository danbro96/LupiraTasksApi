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
    private readonly CallerFactory _callers;
    private readonly ShareService _shares;

    public SharesHandler(CallerFactory callers, ShareService shares)
    {
        _callers = callers;
        _shares = shares;
    }

    public async Task<Results<Ok<ShareResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> CreateAsync(
        HttpContext ctx, Guid listId, CreateShareRequest request, CancellationToken ct)
    {
        var caller = await _callers.MemberAsync(ct);
        if (caller is null) return TypedResults.Unauthorized();
        return OpResultMap.OkNotFoundProblem(await _shares.CreateAsync(caller, IdempotencyKey.From(ctx), listId, request, ct));
    }

    public async Task<Results<Ok<ShareCollectionResponse>, NotFound, UnauthorizedHttpResult>> ListAsync(
        Guid listId, CancellationToken ct)
    {
        var caller = await _callers.MemberAsync(ct);
        if (caller is null) return TypedResults.Unauthorized();
        return OpResultMap.OkNotFound(await _shares.ListAsync(caller, listId, ct));
    }

    public async Task<Results<NoContent, NotFound, UnauthorizedHttpResult>> RevokeAsync(
        HttpContext ctx, Guid listId, Guid shareId, CancellationToken ct)
    {
        var caller = await _callers.MemberAsync(ct);
        if (caller is null) return TypedResults.Unauthorized();
        return OpResultMap.NoContentNotFound(await _shares.RevokeAsync(caller, IdempotencyKey.From(ctx), listId, shareId, ct));
    }

    public async Task<Results<Ok<RedeemShareResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> RedeemAsync(
        HttpContext ctx, RedeemShareRequest request, CancellationToken ct)
    {
        var caller = await _callers.MemberAsync(ct);
        if (caller is null) return TypedResults.Unauthorized();
        return OpResultMap.OkNotFoundProblem(await _shares.RedeemAsync(caller, IdempotencyKey.From(ctx), request.Token, ct));
    }
}
