using LupiraTasksApi.Application;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Http;
using LupiraTasksApi.Dtos.Items;
using LupiraTasksApi.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LupiraTasksApi.Handlers;

/// <summary>
/// REST adapter for item operations: resolves the caller from the JWT (<see cref="CurrentUser"/>),
/// reads the optional <c>Idempotency-Key</c> header, delegates to the transport-neutral
/// <see cref="ItemService"/> (shared with the MCP tools), and maps its <see cref="OpResult{T}"/>
/// to the typed HTTP result. All business logic — membership, validation, LWW, the atomic
/// idempotent commit — lives in the service.
/// </summary>
public sealed class ItemsHandler
{
    private readonly CurrentUser _user;
    private readonly ItemService _items;

    public ItemsHandler(CurrentUser user, ItemService items)
    {
        _user = user;
        _items = items;
    }

    public async Task<Results<Ok<ItemCollectionResponse>, NotFound, UnauthorizedHttpResult>> ListAsync(
        Guid listId,
        bool? completed,
        Guid? tagId,
        Guid? parentItemId,
        string? assignedTo,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFound(
            await _items.ListAsync(caller, listId, new ItemFilter(completed, tagId, parentItemId, assignedTo), ct));
    }

    public async Task<Results<Ok<ItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> CreateAsync(
        HttpContext ctx,
        Guid listId,
        CreateItemRequest request,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFoundProblem(
            await _items.CreateAsync(caller, Idempotency.KeyFrom(ctx), listId, request, ct));
    }

    public async Task<Results<Ok<ItemResponse>, NotFound, UnauthorizedHttpResult>> GetAsync(
        Guid listId,
        Guid itemId,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFound(await _items.GetAsync(caller, listId, itemId, ct));
    }

    public async Task<Results<Ok<ItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> UpdateAsync(
        HttpContext ctx,
        Guid listId,
        Guid itemId,
        UpdateItemRequest request,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFoundProblem(
            await _items.UpdateAsync(caller, Idempotency.KeyFrom(ctx), listId, itemId, request, ct));
    }

    public async Task<Results<Ok<ItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> CompleteAsync(
        HttpContext ctx, Guid listId, Guid itemId, ItemTimestampRequest? body, CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFoundProblem(
            await _items.CompleteAsync(caller, Idempotency.KeyFrom(ctx), listId, itemId, body?.OccurredAt, ct));
    }

    public async Task<Results<Ok<ItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> ReopenAsync(
        HttpContext ctx, Guid listId, Guid itemId, ItemTimestampRequest? body, CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFoundProblem(
            await _items.ReopenAsync(caller, Idempotency.KeyFrom(ctx), listId, itemId, body?.OccurredAt, ct));
    }

    public async Task<Results<Ok<ItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> MoveAsync(
        HttpContext ctx, Guid listId, Guid itemId, MoveItemRequest request, CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFoundProblem(
            await _items.MoveAsync(caller, Idempotency.KeyFrom(ctx), listId, itemId, request, ct));
    }

    public async Task<Results<NoContent, NotFound, UnauthorizedHttpResult>> DeleteAsync(
        HttpContext ctx, Guid listId, Guid itemId, DateTimeOffset? occurredAt, CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.NoContentNotFound(
            await _items.DeleteAsync(caller, Idempotency.KeyFrom(ctx), listId, itemId, occurredAt, ct));
    }
}
