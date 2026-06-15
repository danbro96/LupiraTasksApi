using LupiraTasksApi.Application;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Http;
using LupiraTasksApi.Dtos.Lists;
using LupiraTasksApi.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LupiraTasksApi.Handlers;

/// <summary>
/// REST adapter for list operations: resolves the caller from the JWT (<see cref="CurrentUser"/>),
/// reads the optional <c>Idempotency-Key</c> header, delegates to the transport-neutral
/// <see cref="ListService"/> (shared with the MCP tools), and maps its <see cref="OpResult{T}"/>
/// to the typed HTTP result. Membership/role enforcement, validation, and the atomic
/// idempotent commit all live in the service.
/// </summary>
public sealed class ListsHandler
{
    private readonly CurrentUser _user;
    private readonly ListService _lists;

    public ListsHandler(CurrentUser user, ListService lists)
    {
        _user = user;
        _lists = lists;
    }

    public async Task<Results<Ok<ListCollectionResponse>, UnauthorizedHttpResult>> ListAsync(
        bool archived,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkOnly(await _lists.ListAsync(caller, archived, ct));
    }

    public async Task<Results<Ok<ListResponse>, ProblemHttpResult, UnauthorizedHttpResult>> CreateAsync(
        HttpContext ctx,
        CreateListRequest request,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkProblem(await _lists.CreateAsync(caller, Idempotency.KeyFrom(ctx), request, ct));
    }

    public async Task<Results<Ok<ListResponse>, NotFound, UnauthorizedHttpResult>> GetAsync(
        Guid listId,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFound(await _lists.GetAsync(caller, listId, ct));
    }

    public async Task<Results<Ok<ListResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> UpdateAsync(
        HttpContext ctx,
        Guid listId,
        UpdateListRequest request,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFoundProblem(await _lists.UpdateAsync(caller, Idempotency.KeyFrom(ctx), listId, request, ct));
    }

    public async Task<Results<Ok<ListResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> ArchiveAsync(
        HttpContext ctx, Guid listId, CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFoundProblem(await _lists.ArchiveAsync(caller, Idempotency.KeyFrom(ctx), listId, ct));
    }

    public async Task<Results<Ok<ListResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> RestoreAsync(
        HttpContext ctx, Guid listId, CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFoundProblem(await _lists.RestoreAsync(caller, Idempotency.KeyFrom(ctx), listId, ct));
    }

    public async Task<Results<NoContent, NotFound, UnauthorizedHttpResult>> DeleteAsync(
        HttpContext ctx, Guid listId, CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.NoContentNotFound(await _lists.DeleteAsync(caller, Idempotency.KeyFrom(ctx), listId, ct));
    }

    public async Task<Results<Ok<ListResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> AddMemberAsync(
        HttpContext ctx,
        Guid listId,
        AddMemberRequest request,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFoundProblem(await _lists.AddMemberAsync(caller, Idempotency.KeyFrom(ctx), listId, request, ct));
    }

    public async Task<Results<Ok<ListResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> ChangeMemberRoleAsync(
        HttpContext ctx,
        Guid listId,
        string memberEmail,
        UpdateMemberRoleRequest request,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.OkNotFoundProblem(
            await _lists.ChangeMemberRoleAsync(caller, Idempotency.KeyFrom(ctx), listId, memberEmail, request, ct));
    }

    public async Task<Results<NoContent, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> RemoveMemberAsync(
        HttpContext ctx,
        Guid listId,
        string memberEmail,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();
        var caller = Caller.Member(email, _user.Groups);
        return OpResultMap.NoContentNotFoundProblem(
            await _lists.RemoveMemberAsync(caller, Idempotency.KeyFrom(ctx), listId, memberEmail, ct));
    }
}
