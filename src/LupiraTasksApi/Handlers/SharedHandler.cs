using LupiraTasksApi.Application;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Http;
using LupiraTasksApi.Mappers;
using LupiraTasksApi.Dtos.Items;
using LupiraTasksApi.Dtos.Shared;
using LupiraTasksApi.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LupiraTasksApi.Handlers;

/// <summary>
/// Adapter for the public <c>/shared/{token}</c> surface. The request was authenticated by
/// <see cref="ShareTokenAuthHandler"/>, so the share grant is on the principal's claims; this turns
/// it into a <c>Caller.ForShare(...)</c> and delegates to the SAME services as the app. Reads map to
/// the trimmed <see cref="SharedListResponse"/>/<see cref="SharedItemResponse"/> (no emails); writes
/// reuse <see cref="ItemService"/> verbatim (idempotency + LWW) and are gated to read/write links.
/// </summary>
public sealed class SharedHandler
{
    private readonly IHttpContextAccessor _http;
    private readonly ShareService _shares;
    private readonly ItemService _items;

    public SharedHandler(IHttpContextAccessor http, ShareService shares, ItemService items)
    {
        _http = http;
        _shares = shares;
        _items = items;
    }

    public async Task<Results<Ok<SharedListResponse>, NotFound, UnauthorizedHttpResult>> GetListAsync(CancellationToken ct)
    {
        var caller = ShareCaller();
        if (caller is null) return TypedResults.Unauthorized();
        return OpResultMap.OkNotFound(await _shares.GetSharedListAsync(caller, ct));
    }

    public Task<Results<Ok<SharedItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> AddItemAsync(
        HttpContext ctx, CreateItemRequest body, CancellationToken ct) =>
        WriteAsync(ctx, (caller, listId, cmdId) => _items.CreateAsync(caller, cmdId, listId, body, ct));

    public Task<Results<Ok<SharedItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> UpdateItemAsync(
        HttpContext ctx, Guid itemId, UpdateItemRequest body, CancellationToken ct) =>
        WriteAsync(ctx, (caller, listId, cmdId) => _items.UpdateAsync(caller, cmdId, listId, itemId, body, ct));

    public Task<Results<Ok<SharedItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> CompleteAsync(
        HttpContext ctx, Guid itemId, ItemTimestampRequest? body, CancellationToken ct) =>
        WriteAsync(ctx, (caller, listId, cmdId) => _items.CompleteAsync(caller, cmdId, listId, itemId, body?.OccurredAt, ct));

    public Task<Results<Ok<SharedItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> ReopenAsync(
        HttpContext ctx, Guid itemId, ItemTimestampRequest? body, CancellationToken ct) =>
        WriteAsync(ctx, (caller, listId, cmdId) => _items.ReopenAsync(caller, cmdId, listId, itemId, body?.OccurredAt, ct));

    public Task<Results<Ok<SharedItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> MoveAsync(
        HttpContext ctx, Guid itemId, MoveItemRequest body, CancellationToken ct) =>
        WriteAsync(ctx, (caller, listId, cmdId) => _items.MoveAsync(caller, cmdId, listId, itemId, body, ct));

    public async Task<Results<NoContent, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> DeleteItemAsync(
        HttpContext ctx, Guid itemId, DateTimeOffset? occurredAt, CancellationToken ct)
    {
        var caller = ShareCaller();
        if (caller is null) return TypedResults.Unauthorized();
        if (caller.Share!.Access != ShareAccess.ReadWrite) return Problems.Forbidden("This share link is read-only.");
        return OpResultMap.NoContentNotFoundProblem(
            await _items.DeleteAsync(caller, Idempotency.KeyFrom(ctx), caller.Share.ListId, itemId, occurredAt, ct));
    }

    /// <summary>Shared item write: require an authenticated read/write share, run the op, trim the result.</summary>
    private async Task<Results<Ok<SharedItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> WriteAsync(
        HttpContext ctx, Func<Caller, Guid, Guid?, Task<OpResult<ItemResponse>>> op)
    {
        var caller = ShareCaller();
        if (caller is null) return TypedResults.Unauthorized();
        if (caller.Share!.Access != ShareAccess.ReadWrite)
            return Problems.Forbidden("This share link is read-only.");

        var result = await op(caller, caller.Share.ListId, Idempotency.KeyFrom(ctx));
        return OpResultMap.OkNotFoundProblem(result, it => it.ToShared());
    }

    private Caller? ShareCaller()
    {
        var principal = _http.HttpContext?.User;
        var grant = principal is null ? null : ShareTokenAuthHandler.GrantFrom(principal);
        return grant is null ? null : Caller.ForShare(grant);
    }
}
