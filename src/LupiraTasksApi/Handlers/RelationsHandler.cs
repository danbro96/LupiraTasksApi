using LupiraTasksApi.Application;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Dtos.Relations;
using LupiraTasksApi.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LupiraTasksApi.Handlers;

/// <summary>
/// REST adapter for an item's cross-API relations: resolves the caller from the JWT, delegates to the
/// transport-neutral <see cref="RelationService"/> (shared with the MCP tools), and maps its
/// <see cref="OpResult{T}"/> to the typed HTTP result. Idempotency is structural (the deterministic
/// relation id), so there is no <c>Idempotency-Key</c> here.
/// </summary>
public sealed class RelationsHandler
{
    private readonly CallerFactory _callers;
    private readonly RelationService _relations;

    public RelationsHandler(CallerFactory callers, RelationService relations)
    {
        _callers = callers;
        _relations = relations;
    }

    public async Task<Results<Ok<RelationDto>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> LinkAsync(
        Guid listId, Guid itemId, CreateRelationRequest body, CancellationToken ct)
    {
        var caller = await _callers.MemberAsync(ct);
        if (caller is null) return TypedResults.Unauthorized();
        return OpResultMap.OkNotFoundProblem(await _relations.LinkAsync(caller, listId, itemId, body, ct));
    }

    public async Task<Results<Ok<List<RelationDto>>, NotFound, UnauthorizedHttpResult>> ListAsync(
        Guid listId, Guid itemId, CancellationToken ct)
    {
        var caller = await _callers.MemberAsync(ct);
        if (caller is null) return TypedResults.Unauthorized();
        return OpResultMap.OkNotFound(await _relations.ListAsync(caller, listId, itemId, ct));
    }

    public async Task<Results<NoContent, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> UnlinkAsync(
        Guid listId, Guid itemId, string? toKind, string? toRef, string? relationType, CancellationToken ct)
    {
        var caller = await _callers.MemberAsync(ct);
        if (caller is null) return TypedResults.Unauthorized();
        return OpResultMap.NoContentNotFoundProblem(
            await _relations.UnlinkAsync(caller, listId, itemId, toKind, toRef, relationType, ct));
    }
}
