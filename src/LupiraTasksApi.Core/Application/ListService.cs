using JasperFx;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Data;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Identity;
using LupiraTasksApi.Domain.Lists;
using LupiraTasksApi.Dtos.Lists;
using LupiraTasksApi.Domain.Shares;
using LupiraTasksApi.Mappers;
using Marten;
using Marten.Exceptions;

namespace LupiraTasksApi.Application;

/// <summary>
/// Transport-neutral list operations over the <c>TodoList</c> event stream — the single
/// source of truth shared by the REST <c>ListsHandler</c> and the MCP tools. Membership and
/// ownership are keyed by the internal <c>PrincipalId</c>; an invite takes an email, resolved to a
/// principal (provisioning a placeholder if unseen). Every mutation stamps the <c>actor</c> header
/// (the caller's principal id) before the single <c>SaveChangesAsync</c>. Reads resolve principal ids
/// back to <see cref="PersonRef"/>. Non-members get <see cref="OpStatus.NotFound"/> (not 403); a member
/// lacking the required role gets <see cref="OpStatus.Forbidden"/>.
/// </summary>
public sealed class ListService
{
    private const int MaxNameLength = 128;
    private const int MaxEmailLength = 320;

    private readonly IDocumentSession _session;
    private readonly AccessResolver _access;
    private readonly Idempotency _idempotency;
    private readonly PrincipalDirectory _principals;

    public ListService(IDocumentSession session, AccessResolver access, Idempotency idempotency, PrincipalDirectory principals)
    {
        _session = session;
        _access = access;
        _idempotency = idempotency;
        _principals = principals;
    }

    public async Task<OpResult<ListCollectionResponse>> ListAsync(Caller caller, bool archived, CancellationToken ct)
    {
        var principalId = caller.PrincipalId!.Value; // member-only surface

        // "My lists" = not deleted, archived-flag matches the filter, caller is a member.
        var docs = await _session.Query<TodoList>()
            .Where(l => !l.IsDeleted && l.IsArchived == archived && l.Members.Any(m => m.PrincipalId == principalId))
            .ToListAsync(ct);

        var ordered = docs.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var lookup = await _principals.LookupAsync(ordered.SelectMany(PrincipalIdsOf), ct);
        var lists = ordered.Select(l => l.ToResponse(lookup, principalId)).ToList();

        return OpResult<ListCollectionResponse>.Ok(new ListCollectionResponse { Lists = lists });
    }

    public async Task<OpResult<ListResponse>> CreateAsync(Caller caller, Guid? cmdId, CreateListRequest request, CancellationToken ct)
    {
        var ownerPrincipalId = caller.PrincipalId!.Value; // member-only surface

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            return OpResult<ListResponse>.Invalid($"Name must be 1..{MaxNameLength} characters.");
        if (request.Id == Guid.Empty)
            return OpResult<ListResponse>.Invalid("A client-generated `id` (GUIDv7) is required.");

        var commandId = cmdId ?? Guid.CreateVersion7();
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null)
        {
            // Replay of a recorded create: return the ORIGINAL aggregate (by the stored
            // AggregateId), ignoring the body id so a retried create with the same key but a
            // different body id can't spawn a second stream.
            var prior = await _session.LoadAsync<TodoList>(seen.AggregateId, ct);
            if (prior is not null) return OpResult<ListResponse>.Ok(await ToResponseAsync(prior, caller.PrincipalId!.Value, ct));
        }

        EventActor.Stamp(_session, caller.Actor, caller.ActorEmail, commandId);
        try
        {
            // StartStream + the dedup-ledger Insert commit together: a stream-id collision OR
            // a duplicate command id rolls back the whole transaction, so a concurrent
            // double-create yields neither two streams nor two ledger rows.
            _session.Events.StartStream<TodoList>(
                request.Id,
                new ListCreated(request.Id, name, request.Kind, request.Color, ownerPrincipalId));
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
            if (prior is not null) return OpResult<ListResponse>.Ok(await ToResponseAsync(prior, caller.PrincipalId!.Value, ct));
        }

        var list = await _session.LoadAsync<TodoList>(request.Id, ct);
        return list is null
            ? OpResult<ListResponse>.Invalid("List could not be created.")
            : OpResult<ListResponse>.Ok(await ToResponseAsync(list, caller.PrincipalId!.Value, ct));
    }

    public async Task<OpResult<ListResponse>> GetAsync(Caller caller, Guid listId, CancellationToken ct)
    {
        var access = await _access.RequireMembershipAsync(listId, caller.PrincipalId!.Value, ListRole.Viewer, ct);
        return access.Allowed
            ? OpResult<ListResponse>.Ok(await ToResponseAsync(access.List!, caller.PrincipalId!.Value, ct))
            : OpResult<ListResponse>.NotFound();
    }

    public async Task<OpResult<ListResponse>> UpdateAsync(Caller caller, Guid? cmdId, Guid listId, UpdateListRequest request, CancellationToken ct)
    {
        var access = await _access.RequireMembershipAsync(listId, caller.PrincipalId!.Value, ListRole.Editor, ct);
        if (!access.Allowed) return OpResult<ListResponse>.NotFound();

        var commandId = cmdId ?? Guid.CreateVersion7();
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null) return OpResult<ListResponse>.Ok(await ToResponseAsync(access.List!, caller.PrincipalId!.Value, ct));

        var events = new List<object>();
        var name = request.Name?.Trim();
        if (name is not null)
        {
            if (name.Length is 0 or > MaxNameLength)
                return OpResult<ListResponse>.Invalid($"Name must be 1..{MaxNameLength} characters.");
            events.Add(new ListRenamed(listId, name));
        }
        if (request.ColorProvided)
            events.Add(new ListRecolored(listId, request.Color));
        if (request.SimplePriority is { } simplePriority)
            events.Add(new ListSimplePrioritySet(listId, simplePriority));

        if (events.Count == 0) return OpResult<ListResponse>.Ok(await ToResponseAsync(access.List!, caller.PrincipalId!.Value, ct));

        EventActor.Stamp(_session, caller.Actor, caller.ActorEmail, commandId);
        await _idempotency.AppendDedupAsync(commandId, listId, events, ct);

        var updated = await _session.LoadAsync<TodoList>(listId, ct);
        return OpResult<ListResponse>.Ok(await ToResponseAsync(updated!, caller.PrincipalId!.Value, ct));
    }

    public Task<OpResult<ListResponse>> ArchiveAsync(Caller caller, Guid? cmdId, Guid listId, CancellationToken ct) =>
        OwnerLifecycleAsync(caller, cmdId, listId, l => new ListArchived(l.Id), ct);

    public Task<OpResult<ListResponse>> RestoreAsync(Caller caller, Guid? cmdId, Guid listId, CancellationToken ct) =>
        OwnerLifecycleAsync(caller, cmdId, listId, l => new ListRestored(l.Id), ct);

    public async Task<OpResult> DeleteAsync(Caller caller, Guid? cmdId, Guid listId, CancellationToken ct)
    {
        var access = await _access.RequireMembershipAsync(listId, caller.PrincipalId!.Value, ListRole.Owner, ct);
        if (!access.Allowed) return OpResult.NotFound();

        var commandId = cmdId ?? Guid.CreateVersion7();
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null) return OpResult.Ok();

        EventActor.Stamp(_session, caller.Actor, caller.ActorEmail, commandId);
        await StageShareRevokesAsync(listId, ct);
        await _idempotency.AppendDedupAsync(
            commandId, listId, new object[] { new ListDeleted(listId, "Deleted by owner") }, ct);

        return OpResult.Ok();
    }

    public async Task<OpResult<ListResponse>> AddMemberAsync(Caller caller, Guid? cmdId, Guid listId, AddMemberRequest request, CancellationToken ct)
    {
        var target = NormalizeEmail(request.Email);
        if (target is null) return OpResult<ListResponse>.Invalid("A member email is required.");

        // Any member may add another user (direct-add, no invite/accept).
        var access = await _access.RequireMembershipAsync(listId, caller.PrincipalId!.Value, ListRole.Viewer, ct);
        if (!access.Allowed) return OpResult<ListResponse>.NotFound();

        var commandId = cmdId ?? Guid.CreateVersion7();
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null) return OpResult<ListResponse>.Ok(await ToResponseAsync(access.List!, caller.PrincipalId!.Value, ct));

        // Resolve the invite email to a principal (provisioning a placeholder if the person hasn't
        // been seen yet); membership is keyed by that id, so a case variant re-adds the same member.
        var principal = await _principals.ResolveOrProvisionAsync(sub: null, target, name: null, ct);
        var role = request.Role ?? ListRole.Editor;

        EventActor.Stamp(_session, caller.Actor, caller.ActorEmail, commandId);
        await _idempotency.AppendDedupAsync(
            commandId, listId, new object[] { new MemberAdded(listId, principal.Id, role) }, ct);

        var updated = await _session.LoadAsync<TodoList>(listId, ct);
        return OpResult<ListResponse>.Ok(await ToResponseAsync(updated!, caller.PrincipalId!.Value, ct));
    }

    public async Task<OpResult<ListResponse>> ChangeMemberRoleAsync(Caller caller, Guid? cmdId, Guid listId, Guid targetPrincipalId, UpdateMemberRoleRequest request, CancellationToken ct)
    {
        // Member-but-not-owner → 403; non-member → 404 (don't leak the list).
        var membership = await _access.RequireMembershipAsync(listId, caller.PrincipalId!.Value, ListRole.Viewer, ct);
        if (!membership.Allowed) return OpResult<ListResponse>.NotFound();
        if (!AccessResolver.Satisfies(membership.Role, ListRole.Owner))
            return OpResult<ListResponse>.Forbidden("Only an owner can change member roles.");

        var members = membership.List!.Members;
        var targetMember = members.Find(m => m.PrincipalId == targetPrincipalId);
        if (targetMember is null) return OpResult<ListResponse>.NotFound();

        // Never strand the list without an owner.
        if (request.Role != ListRole.Owner && targetMember.Role == ListRole.Owner && !OtherOwnerExists(members, targetPrincipalId))
            return OpResult<ListResponse>.Invalid("The list must keep at least one owner.");

        var commandId = cmdId ?? Guid.CreateVersion7();
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null) return OpResult<ListResponse>.Ok(await ToResponseAsync(membership.List!, caller.PrincipalId!.Value, ct));

        EventActor.Stamp(_session, caller.Actor, caller.ActorEmail, commandId);
        await _idempotency.AppendDedupAsync(
            commandId, listId, new object[] { new MemberRoleChanged(listId, targetPrincipalId, request.Role) }, ct);

        var updated = await _session.LoadAsync<TodoList>(listId, ct);
        return OpResult<ListResponse>.Ok(await ToResponseAsync(updated!, caller.PrincipalId!.Value, ct));
    }

    public async Task<OpResult> RemoveMemberAsync(Caller caller, Guid? cmdId, Guid listId, Guid targetPrincipalId, CancellationToken ct)
    {
        var isSelf = targetPrincipalId == caller.PrincipalId!.Value;

        // Must be a member (404 otherwise). Removing OTHERS needs Owner (403); leaving is always allowed.
        var membership = await _access.RequireMembershipAsync(listId, caller.PrincipalId!.Value, ListRole.Viewer, ct);
        if (!membership.Allowed) return OpResult.NotFound();
        if (!isSelf && !AccessResolver.Satisfies(membership.Role, ListRole.Owner))
            return OpResult.Forbidden("Only an owner can remove other members.");

        var members = membership.List!.Members;
        var targetMember = members.Find(m => m.PrincipalId == targetPrincipalId);
        if (targetMember is null) return OpResult.Ok(); // already not a member — idempotent no-op

        var commandId = cmdId ?? Guid.CreateVersion7();
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null) return OpResult.Ok();

        var events = new List<object> { new MemberRemoved(listId, targetPrincipalId) };
        // The last owner leaving/being removed auto-deletes the list (tombstone) for everyone.
        var listDeleted = targetMember.Role == ListRole.Owner && !OtherOwnerExists(members, targetPrincipalId);
        if (listDeleted)
            events.Add(new ListDeleted(listId, "last owner left"));

        EventActor.Stamp(_session, caller.Actor, caller.ActorEmail, commandId);
        if (listDeleted) await StageShareRevokesAsync(listId, ct);
        await _idempotency.AppendDedupAsync(commandId, listId, events, ct);

        return OpResult.Ok();
    }

    private async Task<OpResult<ListResponse>> OwnerLifecycleAsync(
        Caller caller, Guid? cmdId, Guid listId, Func<TodoList, object> makeEvent, CancellationToken ct)
    {
        var access = await _access.RequireMembershipAsync(listId, caller.PrincipalId!.Value, ListRole.Owner, ct);
        if (!access.Allowed) return OpResult<ListResponse>.NotFound();

        var commandId = cmdId ?? Guid.CreateVersion7();
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null) return OpResult<ListResponse>.Ok(await ToResponseAsync(access.List!, caller.PrincipalId!.Value, ct));

        EventActor.Stamp(_session, caller.Actor, caller.ActorEmail, commandId);
        await _idempotency.AppendDedupAsync(commandId, listId, new[] { makeEvent(access.List!) }, ct);

        var updated = await _session.LoadAsync<TodoList>(listId, ct);
        return OpResult<ListResponse>.Ok(await ToResponseAsync(updated!, caller.PrincipalId!.Value, ct));
    }

    /// <summary>Map a list to its response, resolving owner + member principal ids to <see cref="PersonRef"/>.</summary>
    private async Task<ListResponse> ToResponseAsync(TodoList list, Guid callerPrincipalId, CancellationToken ct)
    {
        var lookup = await _principals.LookupAsync(PrincipalIdsOf(list), ct);
        return list.ToResponse(lookup, callerPrincipalId);
    }

    /// <summary>Every principal id referenced by a list snapshot: owner, members, and Guid-shaped AddedBy actors.</summary>
    private static IEnumerable<Guid> PrincipalIdsOf(TodoList list)
    {
        yield return list.OwnerPrincipalId;
        foreach (var m in list.Members)
        {
            yield return m.PrincipalId;
            if (Guid.TryParse(m.AddedBy, out var addedBy)) yield return addedBy;
        }
    }

    /// <summary>
    /// Stage a <see cref="ShareLinkRevoked"/> on every still-active link of a list about to be tombstoned,
    /// so a deleted list leaves no usable public token. Appended to the session (not committed here) so it
    /// rides the SAME transaction as the <see cref="ListDeleted"/> — if the dedup race is lost the whole
    /// batch, revokes included, rolls back. Already-revoked links are skipped, so a re-run is a no-op.
    /// </summary>
    private async Task StageShareRevokesAsync(Guid listId, CancellationToken ct)
    {
        var links = await _session.Query<ShareLink>()
            .Where(s => s.ListId == listId && !s.Revoked)
            .ToListAsync(ct);
        foreach (var link in links)
            _session.Events.Append(link.Id, new ShareLinkRevoked(link.Id, "list deleted"));
    }

    /// <summary>Trim + length-check an invite email; <c>null</c> if empty/too long.</summary>
    private static string? NormalizeEmail(string? raw)
    {
        var e = raw?.Trim();
        return string.IsNullOrEmpty(e) || e.Length > MaxEmailLength ? null : e;
    }

    /// <summary>True if some member other than <paramref name="exceptPrincipalId"/> is still an Owner.</summary>
    private static bool OtherOwnerExists(IEnumerable<Member> members, Guid exceptPrincipalId) =>
        members.Any(m => m.Role == ListRole.Owner && m.PrincipalId != exceptPrincipalId);

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
