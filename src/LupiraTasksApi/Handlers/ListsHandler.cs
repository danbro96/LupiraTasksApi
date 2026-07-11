using LupiraTasksApi.Application;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Dtos.Lists;
using LupiraTasksApi.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LupiraTasksApi.Handlers;

/// <summary>
/// REST adapter for list operations: resolves the caller from the JWT (<see cref="CallerFactory"/>),
/// reads the optional <c>Idempotency-Key</c> header, delegates to the transport-neutral
/// <see cref="ListService"/> (shared with the MCP tools), and maps its <see cref="OpResult{T}"/>
/// to the typed HTTP result. Membership/role enforcement, validation, and the atomic
/// idempotent commit all live in the service.
/// </summary>
public sealed class ListsHandler
{
    private readonly CallerFactory _callers;
    private readonly ListService _lists;

    public ListsHandler(CallerFactory callers, ListService lists)
    {
        _callers = callers;
        _lists = lists;
    }

    public async Task<Results<Ok<ListCollectionResponse>, UnauthorizedHttpResult>> ListAsync(
        bool archived,
        CancellationToken ct)
    {
        var caller = await _callers.MemberAsync(ct);
        if (caller is null) return TypedResults.Unauthorized();
        return OpResultMap.OkOnly(await _lists.ListAsync(caller, archived, ct));
    }

    public async Task<Results<Ok<ListResponse>, ProblemHttpResult, UnauthorizedHttpResult>> CreateAsync(
        HttpContext ctx,
        CreateListRequest request,
        CancellationToken ct)
    {
        var caller = await _callers.MemberAsync(ct);
        if (caller is null) return TypedResults.Unauthorized();
        return OpResultMap.OkProblem(await _lists.CreateAsync(caller, IdempotencyKey.From(ctx), request, ct));
    }

    public async Task<Results<Ok<ListResponse>, NotFound, UnauthorizedHttpResult>> GetAsync(
        Guid listId,
        CancellationToken ct)
    {
        var caller = await _callers.MemberAsync(ct);
        if (caller is null) return TypedResults.Unauthorized();
        return OpResultMap.OkNotFound(await _lists.GetAsync(caller, listId, ct));
    }

    public async Task<Results<Ok<ListResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> UpdateAsync(
        HttpContext ctx,
        Guid listId,
        UpdateListRequest request,
        CancellationToken ct)
    {
        var caller = await _callers.MemberAsync(ct);
        if (caller is null) return TypedResults.Unauthorized();
        return OpResultMap.OkNotFoundProblem(await _lists.UpdateAsync(caller, IdempotencyKey.From(ctx), listId, request, ct));
    }

    public async Task<Results<Ok<ListResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> ArchiveAsync(
        HttpContext ctx, Guid listId, CancellationToken ct)
    {
        var caller = await _callers.MemberAsync(ct);
        if (caller is null) return TypedResults.Unauthorized();
        return OpResultMap.OkNotFoundProblem(await _lists.ArchiveAsync(caller, IdempotencyKey.From(ctx), listId, ct));
    }

    public async Task<Results<Ok<ListResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> RestoreAsync(
        HttpContext ctx, Guid listId, CancellationToken ct)
    {
        var caller = await _callers.MemberAsync(ct);
        if (caller is null) return TypedResults.Unauthorized();
        return OpResultMap.OkNotFoundProblem(await _lists.RestoreAsync(caller, IdempotencyKey.From(ctx), listId, ct));
    }

    public async Task<Results<NoContent, NotFound, UnauthorizedHttpResult>> DeleteAsync(
        HttpContext ctx, Guid listId, CancellationToken ct)
    {
        var caller = await _callers.MemberAsync(ct);
        if (caller is null) return TypedResults.Unauthorized();
        return OpResultMap.NoContentNotFound(await _lists.DeleteAsync(caller, IdempotencyKey.From(ctx), listId, ct));
    }

    public async Task<Results<Ok<ListResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> AddMemberAsync(
        HttpContext ctx,
        Guid listId,
        AddMemberRequest request,
        CancellationToken ct)
    {
        var caller = await _callers.MemberAsync(ct);
        if (caller is null) return TypedResults.Unauthorized();
        return OpResultMap.OkNotFoundProblem(await _lists.AddMemberAsync(caller, IdempotencyKey.From(ctx), listId, request, ct));
    }

    public async Task<Results<Ok<ListResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> ChangeMemberRoleAsync(
        HttpContext ctx,
        Guid listId,
        Guid principalId,
        UpdateMemberRoleRequest request,
        CancellationToken ct)
    {
        var caller = await _callers.MemberAsync(ct);
        if (caller is null) return TypedResults.Unauthorized();
        return OpResultMap.OkNotFoundProblem(
            await _lists.ChangeMemberRoleAsync(caller, IdempotencyKey.From(ctx), listId, principalId, request, ct));
    }

    public async Task<Results<NoContent, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> RemoveMemberAsync(
        HttpContext ctx,
        Guid listId,
        Guid principalId,
        CancellationToken ct)
    {
        var caller = await _callers.MemberAsync(ct);
        if (caller is null) return TypedResults.Unauthorized();
        return OpResultMap.NoContentNotFoundProblem(
            await _lists.RemoveMemberAsync(caller, IdempotencyKey.From(ctx), listId, principalId, ct));
    }
}
