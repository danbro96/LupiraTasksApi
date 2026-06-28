using LupiraTasksApi.Auth;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Items;
using LupiraTasksApi.Domain.Lists;
using LupiraTasksApi.Dtos.Relations;
using LupiraTasksApi.Mappers;
using Marten;

namespace LupiraTasksApi.Application;

/// <summary>
/// Cross-API relations: a by-reference link from a task to a cal-api Prompt heartbeat or an external ref
/// (GitHub issue/PR, health incident, …). References are by string, not FK — integrity is by convention.
/// The link is a plain <see cref="Relation"/> document (not an LWW field on the item); add/remove are
/// idempotent via the tuple-derived <see cref="Relation.DeterministicId"/>. The single source of truth
/// shared by the REST <c>RelationsHandler</c> and the MCP tools; every operation resolves list membership
/// first (denied → <see cref="OpStatus.NotFound"/>, never 403) and confirms the item belongs to the list.
/// </summary>
public sealed class RelationService
{
    private readonly IDocumentSession _session;
    private readonly AccessResolver _access;

    public RelationService(IDocumentSession session, AccessResolver access)
    {
        _session = session;
        _access = access;
    }

    public async Task<OpResult<RelationDto>> LinkAsync(
        Caller caller, Guid listId, Guid itemId, CreateRelationRequest request, CancellationToken ct)
    {
        var toKind = request.ToKind?.Trim();
        var toRef = request.ToRef?.Trim();
        var relationType = request.RelationType?.Trim();
        if (string.IsNullOrEmpty(toKind) || string.IsNullOrEmpty(toRef) || string.IsNullOrEmpty(relationType))
            return OpResult<RelationDto>.Invalid("`toKind`, `toRef`, and `relationType` are required.");

        var access = await _access.AuthorizeAsync(caller, listId, ListRole.Editor, ct);
        if (!access.Allowed) return OpResult<RelationDto>.NotFound();
        if (!await ItemInListAsync(itemId, listId, ct)) return OpResult<RelationDto>.NotFound();

        var rel = new Relation
        {
            Id = Relation.DeterministicId(itemId, toKind, toRef, relationType),
            FromKind = Relation.TaskKind,
            FromId = itemId,
            ToKind = toKind,
            ToRef = toRef,
            RelationType = relationType,
            Metadata = request.Metadata?.ToJsonString(),
        };
        // Store is an upsert keyed by the deterministic id: re-adding the same edge is a no-op (and refreshes
        // metadata), with no duplicate row and no race.
        _session.Store(rel);
        await _session.SaveChangesAsync(ct);
        return OpResult<RelationDto>.Ok(rel.ToResponse());
    }

    public async Task<OpResult<List<RelationDto>>> ListAsync(Caller caller, Guid listId, Guid itemId, CancellationToken ct)
    {
        var access = await _access.AuthorizeAsync(caller, listId, ListRole.Viewer, ct);
        if (!access.Allowed) return OpResult<List<RelationDto>>.NotFound();
        if (!await ItemInListAsync(itemId, listId, ct)) return OpResult<List<RelationDto>>.NotFound();

        var rels = await _session.Query<Relation>()
            .Where(x => x.FromKind == Relation.TaskKind && x.FromId == itemId)
            .ToListAsync(ct);
        return OpResult<List<RelationDto>>.Ok(rels.Select(RelationMapper.ToResponse).ToList());
    }

    public async Task<OpResult> UnlinkAsync(
        Caller caller, Guid listId, Guid itemId, string? toKind, string? toRef, string? relationType, CancellationToken ct)
    {
        toKind = toKind?.Trim();
        toRef = toRef?.Trim();
        relationType = relationType?.Trim();
        if (string.IsNullOrEmpty(toKind) || string.IsNullOrEmpty(toRef) || string.IsNullOrEmpty(relationType))
            return OpResult.Invalid("`toKind`, `toRef`, and `relationType` are required.");

        var access = await _access.AuthorizeAsync(caller, listId, ListRole.Editor, ct);
        if (!access.Allowed) return OpResult.NotFound();
        if (!await ItemInListAsync(itemId, listId, ct)) return OpResult.NotFound();

        // Delete by the tuple-derived id: removing a link that isn't there is a no-op (idempotent).
        _session.Delete<Relation>(Relation.DeterministicId(itemId, toKind, toRef, relationType));
        await _session.SaveChangesAsync(ct);
        return OpResult.Ok();
    }

    /// <summary>True when a live item with this id belongs to <paramref name="listId"/>.</summary>
    private async Task<bool> ItemInListAsync(Guid itemId, Guid listId, CancellationToken ct)
    {
        var item = await _session.LoadAsync<Item>(itemId, ct);
        return item is not null && item.ListId == listId && !item.Deleted;
    }
}
