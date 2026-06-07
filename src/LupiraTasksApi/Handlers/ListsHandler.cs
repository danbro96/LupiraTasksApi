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

    public async Task<Results<Ok<ListResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> AddMemberAsync(
        HttpContext ctx,
        Guid listId,
        AddMemberRequest request,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null)
        {
            return TypedResults.Unauthorized();
        }

        var target = NormalizeEmail(request.Email);
        if (target is null)
        {
            return Problems.BadRequest("A member email is required.");
        }

        // Any member may add another user (direct-add, no invite/accept).
        var access = await _access.RequireMembershipAsync(listId, email, ListRole.Viewer, ct);
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

        // Reuse an existing member's stored casing so a re-add (different case) updates the
        // role rather than spawning a duplicate (the snapshot Apply matches case-sensitively).
        var existing = access.List!.Members
            .Find(m => string.Equals(m.Email, target, StringComparison.OrdinalIgnoreCase));
        var memberEmail = existing?.Email ?? target;
        var role = request.Role ?? ListRole.Editor;

        _session.SetHeader(EventActor.HeaderKey, email);
        await _idempotency.AppendDedupAsync(
            commandId, listId, new object[] { new MemberAdded(listId, memberEmail, role) }, ct);

        var updated = await _session.LoadAsync<TodoList>(listId, ct);
        return TypedResults.Ok(updated!.ToResponse());
    }

    public async Task<Results<Ok<ListResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> ChangeMemberRoleAsync(
        HttpContext ctx,
        Guid listId,
        string memberEmail,
        UpdateMemberRoleRequest request,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null)
        {
            return TypedResults.Unauthorized();
        }

        var target = NormalizeEmail(memberEmail);
        if (target is null)
        {
            return Problems.BadRequest("A member email is required.");
        }

        // Member-but-not-owner → 403; non-member → 404 (don't leak the list).
        var membership = await _access.RequireMembershipAsync(listId, email, ListRole.Viewer, ct);
        if (!membership.Allowed)
        {
            return TypedResults.NotFound();
        }
        if (!AccessResolver.Satisfies(membership.Role, ListRole.Owner))
        {
            return Forbidden("Only an owner can change member roles.");
        }

        var members = membership.List!.Members;
        var targetMember = members.Find(m => string.Equals(m.Email, target, StringComparison.OrdinalIgnoreCase));
        if (targetMember is null)
        {
            return TypedResults.NotFound();
        }

        // Never strand the list without an owner.
        if (request.Role != ListRole.Owner && targetMember.Role == ListRole.Owner && !OtherOwnerExists(members, target))
        {
            return Problems.BadRequest("The list must keep at least one owner.");
        }

        var commandId = ResolveCommandId(ctx);
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null)
        {
            return TypedResults.Ok(membership.List!.ToResponse());
        }

        _session.SetHeader(EventActor.HeaderKey, email);
        await _idempotency.AppendDedupAsync(
            commandId, listId, new object[] { new MemberRoleChanged(listId, targetMember.Email, request.Role) }, ct);

        var updated = await _session.LoadAsync<TodoList>(listId, ct);
        return TypedResults.Ok(updated!.ToResponse());
    }

    public async Task<Results<NoContent, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> RemoveMemberAsync(
        HttpContext ctx,
        Guid listId,
        string memberEmail,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null)
        {
            return TypedResults.Unauthorized();
        }

        var target = NormalizeEmail(memberEmail);
        if (target is null)
        {
            return Problems.BadRequest("A member email is required.");
        }

        var isSelf = string.Equals(target, email, StringComparison.OrdinalIgnoreCase);

        // Must be a member (404 otherwise). Removing OTHERS needs Owner (403); leaving is always allowed.
        var membership = await _access.RequireMembershipAsync(listId, email, ListRole.Viewer, ct);
        if (!membership.Allowed)
        {
            return TypedResults.NotFound();
        }
        if (!isSelf && !AccessResolver.Satisfies(membership.Role, ListRole.Owner))
        {
            return Forbidden("Only an owner can remove other members.");
        }

        var members = membership.List!.Members;
        var targetMember = members.Find(m => string.Equals(m.Email, target, StringComparison.OrdinalIgnoreCase));
        if (targetMember is null)
        {
            return TypedResults.NoContent(); // already not a member — idempotent no-op
        }

        var commandId = ResolveCommandId(ctx);
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null)
        {
            return TypedResults.NoContent();
        }

        var events = new List<object> { new MemberRemoved(listId, targetMember.Email) };
        // The last owner leaving/being removed auto-deletes the list (tombstone) for everyone.
        if (targetMember.Role == ListRole.Owner && !OtherOwnerExists(members, target))
        {
            events.Add(new ListDeleted(listId, "last owner left"));
        }

        _session.SetHeader(EventActor.HeaderKey, email);
        await _idempotency.AppendDedupAsync(commandId, listId, events, ct);

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

    private const int MaxEmailLength = 320;

    /// <summary>Trim + length-check a member email; <c>null</c> if empty/too long. Casing is preserved
    /// (the snapshot Apply matches case-sensitively, and the owner email keeps its token casing).</summary>
    private static string? NormalizeEmail(string? raw)
    {
        var e = raw?.Trim();
        return string.IsNullOrEmpty(e) || e.Length > MaxEmailLength ? null : e;
    }

    /// <summary>True if some member other than <paramref name="exceptEmail"/> is still an Owner.</summary>
    private static bool OtherOwnerExists(IEnumerable<Member> members, string exceptEmail) =>
        members.Any(m => m.Role == ListRole.Owner
            && !string.Equals(m.Email, exceptEmail, StringComparison.OrdinalIgnoreCase));

    /// <summary>A plain 403 (used when a member lacks the Owner role — distinct from 404 for non-members).</summary>
    private static ProblemHttpResult Forbidden(string detail) =>
        TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status403Forbidden);

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
