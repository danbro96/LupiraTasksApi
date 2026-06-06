using JasperFx;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Lists;
using LupiraTasksApi.Http;
using LupiraTasksApi.Mappers;
using LupiraTasksApi.Models.Lists;
using LupiraTasksApi.Services;
using Marten;
using Marten.Exceptions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LupiraTasksApi.Handlers;

/// <summary>
/// List CRUD over the <c>TodoList</c> event stream. Every mutation stamps the
/// <c>actor</c> event-metadata header (the caller's email) before the single
/// <c>SaveChangesAsync</c>, so attribution (e.g. <c>Member.AddedBy</c>) is recorded.
/// Membership is enforced via <see cref="AccessResolver"/>, which returns 404 (not 403)
/// to non-members.
/// </summary>
public sealed class ListsHandler
{
    private const int MaxNameLength = 128;

    private readonly IDocumentSession _session;
    private readonly CurrentUser _user;
    private readonly AccessResolver _access;
    private readonly Idempotency _idempotency;

    public ListsHandler(
        IDocumentSession session,
        CurrentUser user,
        AccessResolver access,
        Idempotency idempotency)
    {
        _session = session;
        _user = user;
        _access = access;
        _idempotency = idempotency;
    }

    public async Task<Results<Ok<ListCollectionResponse>, UnauthorizedHttpResult>> ListAsync(
        bool archived,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null)
        {
            return TypedResults.Unauthorized();
        }

        // "My lists" = not deleted, archived-flag matches the filter, caller is a member.
        var docs = await _session.Query<TodoList>()
            .Where(l => !l.IsDeleted && l.IsArchived == archived && l.Members.Any(m => m.Email == email))
            .ToListAsync(ct);

        var lists = docs
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .Select(l => l.ToResponse())
            .ToList();

        return TypedResults.Ok(new ListCollectionResponse { Lists = lists });
    }

    public async Task<Results<Ok<ListResponse>, ProblemHttpResult, UnauthorizedHttpResult>> CreateAsync(
        HttpContext ctx,
        CreateListRequest request,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null)
        {
            return TypedResults.Unauthorized();
        }

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
        {
            return Problems.BadRequest($"Name must be 1..{MaxNameLength} characters.");
        }

        if (request.Id == Guid.Empty)
        {
            return Problems.BadRequest("A client-generated `id` (GUIDv7) is required.");
        }

        var commandId = ResolveCommandId(ctx);
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null)
        {
            // Replay of a recorded create: return the ORIGINAL aggregate (by the stored
            // AggregateId), ignoring the body id so a retried create with the same key but a
            // different body id can't spawn a second stream.
            var prior = await _session.LoadAsync<TodoList>(seen.AggregateId, ct);
            if (prior is not null) return TypedResults.Ok(prior.ToResponse());
        }

        _session.SetHeader(EventActor.HeaderKey, email);
        try
        {
            // StartStream + the dedup-ledger Insert commit together: a stream-id collision OR
            // a duplicate command id rolls back the whole transaction, so a concurrent
            // double-create yields neither two streams nor two ledger rows.
            _session.Events.StartStream<TodoList>(
                request.Id,
                new ListCreated(request.Id, name, request.Kind, request.Color, email));
            _idempotency.Record(commandId, request.Id, version: 1);
            await _session.SaveChangesAsync(ct);
        }
        catch (ExistingStreamIdCollisionException)
        {
            // Idempotent create: the stream already exists (a replayed create). Return it.
        }
        catch (DocumentAlreadyExistsException)
        {
            // Lost the dedup race on the command id — return the original aggregate.
            var prior = await ReResolveCreatedAsync(commandId, request.Id, ct);
            if (prior is not null) return TypedResults.Ok(prior.ToResponse());
        }

        var list = await _session.LoadAsync<TodoList>(request.Id, ct);
        return list is null
            ? Problems.BadRequest("List could not be created.")
            : TypedResults.Ok(list.ToResponse());
    }

    public async Task<Results<Ok<ListResponse>, NotFound, UnauthorizedHttpResult>> GetAsync(
        Guid listId,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null)
        {
            return TypedResults.Unauthorized();
        }

        var access = await _access.RequireMembershipAsync(listId, email, ListRole.Viewer, ct);
        return access.Allowed
            ? TypedResults.Ok(access.List!.ToResponse())
            : TypedResults.NotFound();
    }

    public async Task<Results<Ok<ListResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> UpdateAsync(
        HttpContext ctx,
        Guid listId,
        UpdateListRequest request,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null)
        {
            return TypedResults.Unauthorized();
        }

        var access = await _access.RequireMembershipAsync(listId, email, ListRole.Editor, ct);
        if (!access.Allowed)
        {
            return TypedResults.NotFound();
        }

        var commandId = ResolveCommandId(ctx);
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null)
        {
            return TypedResults.Ok(access.List!.ToResponse());
        }

        var events = new List<object>();
        var name = request.Name?.Trim();
        if (name is not null)
        {
            if (name.Length is 0 or > MaxNameLength)
            {
                return Problems.BadRequest($"Name must be 1..{MaxNameLength} characters.");
            }
            events.Add(new ListRenamed(listId, name));
        }
        if (request.ColorProvided)
        {
            events.Add(new ListRecolored(listId, request.Color));
        }

        if (events.Count == 0)
        {
            return TypedResults.Ok(access.List!.ToResponse());
        }

        _session.SetHeader(EventActor.HeaderKey, email);
        await _idempotency.AppendDedupAsync(commandId, listId, events, ct);

        var updated = await _session.LoadAsync<TodoList>(listId, ct);
        return TypedResults.Ok(updated!.ToResponse());
    }

    public Task<Results<Ok<ListResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> ArchiveAsync(
        HttpContext ctx, Guid listId, CancellationToken ct) =>
        OwnerLifecycleAsync(ctx, listId, l => new ListArchived(l.Id), ct);

    public Task<Results<Ok<ListResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> RestoreAsync(
        HttpContext ctx, Guid listId, CancellationToken ct) =>
        OwnerLifecycleAsync(ctx, listId, l => new ListRestored(l.Id), ct);

    public async Task<Results<NoContent, NotFound, UnauthorizedHttpResult>> DeleteAsync(
        HttpContext ctx, Guid listId, CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null)
        {
            return TypedResults.Unauthorized();
        }

        var access = await _access.RequireMembershipAsync(listId, email, ListRole.Owner, ct);
        if (!access.Allowed)
        {
            return TypedResults.NotFound();
        }

        var commandId = ResolveCommandId(ctx);
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null)
        {
            return TypedResults.NoContent();
        }

        _session.SetHeader(EventActor.HeaderKey, email);
        await _idempotency.AppendDedupAsync(
            commandId, listId, new object[] { new ListDeleted(listId, "Deleted by owner") }, ct);

        return TypedResults.NoContent();
    }

    private async Task<Results<Ok<ListResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> OwnerLifecycleAsync(
        HttpContext ctx,
        Guid listId,
        Func<TodoList, object> makeEvent,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null)
        {
            return TypedResults.Unauthorized();
        }

        var access = await _access.RequireMembershipAsync(listId, email, ListRole.Owner, ct);
        if (!access.Allowed)
        {
            return TypedResults.NotFound();
        }

        var commandId = ResolveCommandId(ctx);
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null)
        {
            return TypedResults.Ok(access.List!.ToResponse());
        }

        _session.SetHeader(EventActor.HeaderKey, email);
        await _idempotency.AppendDedupAsync(
            commandId, listId, new[] { makeEvent(access.List!) }, ct);

        var updated = await _session.LoadAsync<TodoList>(listId, ct);
        return TypedResults.Ok(updated!.ToResponse());
    }

    /// <summary>
    /// The per-request command id used to key the idempotency ledger: the
    /// <c>Idempotency-Key</c> header when present, else a server-minted GUIDv7.
    /// </summary>
    private static Guid ResolveCommandId(HttpContext ctx) =>
        Idempotency.KeyFrom(ctx) ?? Guid.CreateVersion7();

    /// <summary>
    /// Re-resolve the aggregate after losing the create dedup race: prefer the stored
    /// <see cref="ProcessedCommand.AggregateId"/> (the original create's id), else the body id.
    /// </summary>
    private async Task<TodoList?> ReResolveCreatedAsync(Guid commandId, Guid bodyId, CancellationToken ct)
    {
        var seen = await _idempotency.SeenAsync(commandId, ct);
        return await _session.LoadAsync<TodoList>(seen?.AggregateId ?? bodyId, ct);
    }
}
