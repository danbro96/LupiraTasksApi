using JasperFx;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Lists;
using LupiraTasksApi.Mappers;
using LupiraTasksApi.Models.Lists;
using LupiraTasksApi.Services;
using Marten;
using Marten.Exceptions;

namespace LupiraTasksApi.Application;

/// <summary>
/// Transport-neutral list operations over the <c>TodoList</c> event stream — the single
/// source of truth shared by the REST <c>ListsHandler</c> and the MCP tools. Every mutation
/// stamps the <c>actor</c> event-metadata header (the caller's email) before the single
/// <c>SaveChangesAsync</c>, so attribution (e.g. <c>Member.AddedBy</c>) is recorded.
/// Membership is enforced via <see cref="AccessResolver"/>: non-members get
/// <see cref="OpStatus.NotFound"/> (not 403), a member lacking the required role gets
/// <see cref="OpStatus.Forbidden"/>.
/// </summary>
public sealed class ListService
{
    private const int MaxNameLength = 128;
    private const int MaxEmailLength = 320;

    private readonly IDocumentSession _session;
    private readonly AccessResolver _access;
    private readonly Idempotency _idempotency;

    public ListService(IDocumentSession session, AccessResolver access, Idempotency idempotency)
    {
        _session = session;
        _access = access;
        _idempotency = idempotency;
    }

    public async Task<OpResult<ListCollectionResponse>> ListAsync(Caller caller, bool archived, CancellationToken ct)
    {
        var email = caller.Email!; // member-only surface: list management is never reached by a share-link caller

        // "My lists" = not deleted, archived-flag matches the filter, caller is a member.
        var docs = await _session.Query<TodoList>()
            .Where(l => !l.IsDeleted && l.IsArchived == archived && l.Members.Any(m => m.Email == email))
            .ToListAsync(ct);

        var lists = docs
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .Select(l => l.ToResponse())
            .ToList();

        return OpResult<ListCollectionResponse>.Ok(new ListCollectionResponse { Lists = lists });
    }

    public async Task<OpResult<ListResponse>> CreateAsync(Caller caller, Guid? cmdId, CreateListRequest request, CancellationToken ct)
    {
        var email = caller.Email!; // member-only surface: list management is never reached by a share-link caller

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
            if (prior is not null) return OpResult<ListResponse>.Ok(prior.ToResponse());
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
            if (prior is not null) return OpResult<ListResponse>.Ok(prior.ToResponse());
        }

        var list = await _session.LoadAsync<TodoList>(request.Id, ct);
        return list is null
            ? OpResult<ListResponse>.Invalid("List could not be created.")
            : OpResult<ListResponse>.Ok(list.ToResponse());
    }

    public async Task<OpResult<ListResponse>> GetAsync(Caller caller, Guid listId, CancellationToken ct)
    {
        var access = await _access.RequireMembershipAsync(listId, caller.Email!, ListRole.Viewer, ct);
        return access.Allowed
            ? OpResult<ListResponse>.Ok(access.List!.ToResponse())
            : OpResult<ListResponse>.NotFound();
    }

    public async Task<OpResult<ListResponse>> UpdateAsync(Caller caller, Guid? cmdId, Guid listId, UpdateListRequest request, CancellationToken ct)
    {
        var email = caller.Email!; // member-only surface: list management is never reached by a share-link caller
        var access = await _access.RequireMembershipAsync(listId, email, ListRole.Editor, ct);
        if (!access.Allowed) return OpResult<ListResponse>.NotFound();

        var commandId = cmdId ?? Guid.CreateVersion7();
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null) return OpResult<ListResponse>.Ok(access.List!.ToResponse());

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

        if (events.Count == 0) return OpResult<ListResponse>.Ok(access.List!.ToResponse());

        _session.SetHeader(EventActor.HeaderKey, email);
        await _idempotency.AppendDedupAsync(commandId, listId, events, ct);

        var updated = await _session.LoadAsync<TodoList>(listId, ct);
        return OpResult<ListResponse>.Ok(updated!.ToResponse());
    }

    public Task<OpResult<ListResponse>> ArchiveAsync(Caller caller, Guid? cmdId, Guid listId, CancellationToken ct) =>
        OwnerLifecycleAsync(caller, cmdId, listId, l => new ListArchived(l.Id), ct);

    public Task<OpResult<ListResponse>> RestoreAsync(Caller caller, Guid? cmdId, Guid listId, CancellationToken ct) =>
        OwnerLifecycleAsync(caller, cmdId, listId, l => new ListRestored(l.Id), ct);

    public async Task<OpResult> DeleteAsync(Caller caller, Guid? cmdId, Guid listId, CancellationToken ct)
    {
        var email = caller.Email!; // member-only surface: list management is never reached by a share-link caller
        var access = await _access.RequireMembershipAsync(listId, email, ListRole.Owner, ct);
        if (!access.Allowed) return OpResult.NotFound();

        var commandId = cmdId ?? Guid.CreateVersion7();
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null) return OpResult.Ok();

        _session.SetHeader(EventActor.HeaderKey, email);
        await _idempotency.AppendDedupAsync(
            commandId, listId, new object[] { new ListDeleted(listId, "Deleted by owner") }, ct);

        return OpResult.Ok();
    }

    public async Task<OpResult<ListResponse>> AddMemberAsync(Caller caller, Guid? cmdId, Guid listId, AddMemberRequest request, CancellationToken ct)
    {
        var email = caller.Email!; // member-only surface: list management is never reached by a share-link caller
        var target = NormalizeEmail(request.Email);
        if (target is null) return OpResult<ListResponse>.Invalid("A member email is required.");

        // Any member may add another user (direct-add, no invite/accept).
        var access = await _access.RequireMembershipAsync(listId, email, ListRole.Viewer, ct);
        if (!access.Allowed) return OpResult<ListResponse>.NotFound();

        var commandId = cmdId ?? Guid.CreateVersion7();
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null) return OpResult<ListResponse>.Ok(access.List!.ToResponse());

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
        return OpResult<ListResponse>.Ok(updated!.ToResponse());
    }

    public async Task<OpResult<ListResponse>> ChangeMemberRoleAsync(Caller caller, Guid? cmdId, Guid listId, string memberEmail, UpdateMemberRoleRequest request, CancellationToken ct)
    {
        var email = caller.Email!; // member-only surface: list management is never reached by a share-link caller
        var target = NormalizeEmail(memberEmail);
        if (target is null) return OpResult<ListResponse>.Invalid("A member email is required.");

        // Member-but-not-owner → 403; non-member → 404 (don't leak the list).
        var membership = await _access.RequireMembershipAsync(listId, email, ListRole.Viewer, ct);
        if (!membership.Allowed) return OpResult<ListResponse>.NotFound();
        if (!AccessResolver.Satisfies(membership.Role, ListRole.Owner))
            return OpResult<ListResponse>.Forbidden("Only an owner can change member roles.");

        var members = membership.List!.Members;
        var targetMember = members.Find(m => string.Equals(m.Email, target, StringComparison.OrdinalIgnoreCase));
        if (targetMember is null) return OpResult<ListResponse>.NotFound();

        // Never strand the list without an owner.
        if (request.Role != ListRole.Owner && targetMember.Role == ListRole.Owner && !OtherOwnerExists(members, target))
            return OpResult<ListResponse>.Invalid("The list must keep at least one owner.");

        var commandId = cmdId ?? Guid.CreateVersion7();
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null) return OpResult<ListResponse>.Ok(membership.List!.ToResponse());

        _session.SetHeader(EventActor.HeaderKey, email);
        await _idempotency.AppendDedupAsync(
            commandId, listId, new object[] { new MemberRoleChanged(listId, targetMember.Email, request.Role) }, ct);

        var updated = await _session.LoadAsync<TodoList>(listId, ct);
        return OpResult<ListResponse>.Ok(updated!.ToResponse());
    }

    public async Task<OpResult> RemoveMemberAsync(Caller caller, Guid? cmdId, Guid listId, string memberEmail, CancellationToken ct)
    {
        var email = caller.Email!; // member-only surface: list management is never reached by a share-link caller
        var target = NormalizeEmail(memberEmail);
        if (target is null) return OpResult.Invalid("A member email is required.");

        var isSelf = string.Equals(target, email, StringComparison.OrdinalIgnoreCase);

        // Must be a member (404 otherwise). Removing OTHERS needs Owner (403); leaving is always allowed.
        var membership = await _access.RequireMembershipAsync(listId, email, ListRole.Viewer, ct);
        if (!membership.Allowed) return OpResult.NotFound();
        if (!isSelf && !AccessResolver.Satisfies(membership.Role, ListRole.Owner))
            return OpResult.Forbidden("Only an owner can remove other members.");

        var members = membership.List!.Members;
        var targetMember = members.Find(m => string.Equals(m.Email, target, StringComparison.OrdinalIgnoreCase));
        if (targetMember is null) return OpResult.Ok(); // already not a member — idempotent no-op

        var commandId = cmdId ?? Guid.CreateVersion7();
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null) return OpResult.Ok();

        var events = new List<object> { new MemberRemoved(listId, targetMember.Email) };
        // The last owner leaving/being removed auto-deletes the list (tombstone) for everyone.
        if (targetMember.Role == ListRole.Owner && !OtherOwnerExists(members, target))
            events.Add(new ListDeleted(listId, "last owner left"));

        _session.SetHeader(EventActor.HeaderKey, email);
        await _idempotency.AppendDedupAsync(commandId, listId, events, ct);

        return OpResult.Ok();
    }

    private async Task<OpResult<ListResponse>> OwnerLifecycleAsync(
        Caller caller, Guid? cmdId, Guid listId, Func<TodoList, object> makeEvent, CancellationToken ct)
    {
        var email = caller.Email!; // member-only surface: list management is never reached by a share-link caller
        var access = await _access.RequireMembershipAsync(listId, email, ListRole.Owner, ct);
        if (!access.Allowed) return OpResult<ListResponse>.NotFound();

        var commandId = cmdId ?? Guid.CreateVersion7();
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null) return OpResult<ListResponse>.Ok(access.List!.ToResponse());

        _session.SetHeader(EventActor.HeaderKey, email);
        await _idempotency.AppendDedupAsync(commandId, listId, new[] { makeEvent(access.List!) }, ct);

        var updated = await _session.LoadAsync<TodoList>(listId, ct);
        return OpResult<ListResponse>.Ok(updated!.ToResponse());
    }

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
