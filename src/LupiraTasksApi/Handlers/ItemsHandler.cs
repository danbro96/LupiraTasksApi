using LupiraTasksApi.Application;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Dtos.Items;
using LupiraTasksApi.Http;
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
        ItemStatus? status,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFound(
            await _items.ListAsync(caller, listId, new ItemFilter(completed, tagId, parentItemId, assignedTo, status), ct));
    }

    public async Task<Results<Ok<ItemCollectionResponse>, UnauthorizedHttpResult>> SearchAsync(
        string? query,
        bool? completed,
        ItemStatus? status,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkOnly(await _items.SearchAsync(caller, query, completed, status, ct));
    }

    /// <summary>Edit an item addressed by id alone — the list is resolved server-side (the caller may not
    /// know it). Membership is still enforced by the subsequent update (Editor+), so a non-member gets 404.</summary>
    public async Task<Results<Ok<ItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> UpdateByIdAsync(
        HttpContext ctx,
        Guid itemId,
        UpdateItemRequest request,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        if (await _items.FindListIdAsync(itemId, ct) is not { } listId) return TypedResults.NotFound();
        return OpResultMap.OkNotFoundProblem(
            await _items.UpdateAsync(caller, IdempotencyKey.From(ctx), listId, itemId, request, ct));
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
            await _items.CreateAsync(caller, IdempotencyKey.From(ctx), listId, request, ct));
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
            await _items.UpdateAsync(caller, IdempotencyKey.From(ctx), listId, itemId, request, ct));
    }

    public async Task<Results<Ok<ItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> CompleteAsync(
        HttpContext ctx, Guid listId, Guid itemId, ItemTimestampRequest? body, CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFoundProblem(
            await _items.CompleteAsync(caller, IdempotencyKey.From(ctx), listId, itemId, body?.OccurredAt, ct));
    }

    public async Task<Results<Ok<ItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> ReopenAsync(
        HttpContext ctx, Guid listId, Guid itemId, ItemTimestampRequest? body, CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFoundProblem(
            await _items.ReopenAsync(caller, IdempotencyKey.From(ctx), listId, itemId, body?.OccurredAt, ct));
    }

    public async Task<Results<Ok<ItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> SetStatusAsync(
        HttpContext ctx, Guid listId, Guid itemId, SetStatusRequest body, CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFoundProblem(
            await _items.SetStatusAsync(caller, IdempotencyKey.From(ctx), listId, itemId, body.Status, body.Reason, body.OccurredAt, ct));
    }

    public async Task<Results<Ok<ItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> SetMetadataAsync(
        HttpContext ctx, Guid listId, Guid itemId, SetMetadataRequest body, CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFoundProblem(
            await _items.SetMetadataAsync(caller, IdempotencyKey.From(ctx), listId, itemId, body.Metadata?.ToJsonString(), body.OccurredAt, ct));
    }

    public async Task<Results<Ok<ItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> MoveAsync(
        HttpContext ctx, Guid listId, Guid itemId, MoveItemRequest request, CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFoundProblem(
            await _items.MoveAsync(caller, IdempotencyKey.From(ctx), listId, itemId, request, ct));
    }

    public async Task<Results<NoContent, NotFound, UnauthorizedHttpResult>> DeleteAsync(
        HttpContext ctx, Guid listId, Guid itemId, DateTimeOffset? occurredAt, CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.NoContentNotFound(
            await _items.DeleteAsync(caller, IdempotencyKey.From(ctx), listId, itemId, occurredAt, ct));
    }
}
