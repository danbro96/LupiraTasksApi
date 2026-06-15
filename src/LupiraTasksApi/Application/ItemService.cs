using JasperFx;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Items;
using LupiraTasksApi.Mappers;
using LupiraTasksApi.Dtos.Items;
using LupiraTasksApi.Services;
using Marten;
using Marten.Exceptions;

namespace LupiraTasksApi.Application;

/// <summary>
/// Transport-neutral item operations over the <c>Item</c> event stream, scoped to a list.
/// The single source of truth shared by the REST <c>ItemsHandler</c> and the MCP tools.
/// Every operation resolves list membership first (denied → <see cref="OpStatus.NotFound"/>,
/// never 403, so a non-member can't probe existence) and confirms the item belongs to the
/// route's list before mutating. Mutations stamp the <c>actor</c> header and carry an
/// <c>OccurredAt</c> for per-field LWW; the atomic event-append + idempotency-ledger commit
/// (single <c>SaveChangesAsync</c>) lives in <see cref="Idempotency"/> and is unchanged.
/// </summary>
public sealed class ItemService
{
    private const int MaxTitleLength = 256;

    private readonly IDocumentSession _session;
    private readonly AccessResolver _access;
    private readonly Idempotency _idempotency;

    public ItemService(IDocumentSession session, AccessResolver access, Idempotency idempotency)
    {
        _session = session;
        _access = access;
        _idempotency = idempotency;
    }

    public async Task<OpResult<ItemCollectionResponse>> ListAsync(
        Caller caller, Guid listId, ItemFilter filter, CancellationToken ct)
    {
        var access = await _access.AuthorizeAsync(caller, listId, ListRole.Viewer, ct);
        if (!access.Allowed) return OpResult<ItemCollectionResponse>.NotFound();

        // Marten can't translate the per-field-LWW snapshot's computed members across all
        // filters, so fetch the list's live items and filter/sort in memory (family scale).
        var items = await _session.Query<Item>()
            .Where(i => i.ListId == listId && !i.Deleted)
            .ToListAsync(ct);

        IEnumerable<Item> filtered = items;
        if (filter.Completed is { } c) filtered = filtered.Where(i => i.Completed == c);
        if (filter.TagId is { } t) filtered = filtered.Where(i => i.Tags.Contains(t));
        if (filter.ParentItemId is { } p) filtered = filtered.Where(i => i.ParentItemId == p);
        if (!string.IsNullOrWhiteSpace(filter.AssignedTo))
            filtered = filtered.Where(i => string.Equals(i.AssignedTo, filter.AssignedTo, StringComparison.OrdinalIgnoreCase));

        var ordered = filtered
            .OrderBy(i => i.SortOrder, StringComparer.Ordinal)
            .Select(i => i.ToResponse())
            .ToList();

        return OpResult<ItemCollectionResponse>.Ok(new ItemCollectionResponse { Items = ordered });
    }

    public async Task<OpResult<ItemResponse>> CreateAsync(
        Caller caller, Guid? cmdId, Guid listId, CreateItemRequest request, CancellationToken ct)
    {
        var access = await _access.AuthorizeAsync(caller, listId, ListRole.Editor, ct);
        if (!access.Allowed) return OpResult<ItemResponse>.NotFound();

        var title = request.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > MaxTitleLength)
            return OpResult<ItemResponse>.Invalid($"Title must be 1..{MaxTitleLength} characters.");
        if (request.Id == Guid.Empty)
            return OpResult<ItemResponse>.Invalid("A client-generated `id` (GUIDv7) is required.");
        if (string.IsNullOrEmpty(request.SortOrder))
            return OpResult<ItemResponse>.Invalid("`sortOrder` is required.");
        if (request.Quantity is < 0)
            return OpResult<ItemResponse>.Invalid("`quantity` must be non-negative.");

        var commandId = cmdId ?? Guid.CreateVersion7();
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null)
        {
            // Replay of a recorded create: return the ORIGINAL aggregate (by the stored
            // AggregateId), ignoring the body id — a retried create with the same key but a
            // different body id must NOT spawn a second stream.
            var prior = await _session.LoadAsync<Item>(seen.AggregateId, ct);
            if (prior is not null) return OpResult<ItemResponse>.Ok(prior.ToResponse());
        }

        var occurredAt = request.OccurredAt ?? DateTimeOffset.UtcNow;

        _session.SetHeader(EventActor.HeaderKey, caller.Actor);
        try
        {
            var seed = new List<object>
            {
                new ItemAdded(request.Id, listId, request.ParentItemId, title, request.SortOrder, occurredAt, commandId),
            };
            if (request.DueAt is not null)
                seed.Add(new ItemDueDateSet(request.Id, request.DueAt, occurredAt, commandId));
            if (!string.IsNullOrWhiteSpace(request.AssigneeEmail))
                seed.Add(new ItemAssigned(request.Id, request.AssigneeEmail.Trim(), occurredAt, commandId));
            if (request.Quantity is not null || !string.IsNullOrWhiteSpace(request.Unit))
                seed.Add(new ItemQuantitySet(request.Id, request.Quantity, request.Unit, occurredAt, commandId));
            if (request.TagIds is { Count: > 0 })
                foreach (var tagId in request.TagIds.Distinct())
                    seed.Add(new ItemTagAdded(request.Id, tagId, occurredAt, commandId));

            // StartStream + the dedup-ledger Insert commit together: the stream-id collision
            // OR the duplicate command-id both roll back the whole transaction, so a concurrent
            // double-create can't produce two streams or two ledger rows.
            _session.Events.StartStream<Item>(request.Id, seed.ToArray());
            _idempotency.Record(commandId, request.Id, seed.Count);
            await _session.SaveChangesAsync(ct);
        }
        catch (ExistingStreamIdCollisionException)
        {
            // Idempotent create — stream already exists (replay or concurrent create).
        }
        catch (DocumentAlreadyExistsException)
        {
            // Lost the dedup race on the command id — another create with this key committed.
            var prior = await ReResolveCreatedAsync(commandId, request.Id, ct);
            if (prior is not null) return OpResult<ItemResponse>.Ok(prior.ToResponse());
        }

        var item = await _session.LoadAsync<Item>(request.Id, ct);
        return item is null
            ? OpResult<ItemResponse>.Invalid("Item could not be created.")
            : OpResult<ItemResponse>.Ok(item.ToResponse());
    }

    public async Task<OpResult<ItemResponse>> GetAsync(Caller caller, Guid listId, Guid itemId, CancellationToken ct)
    {
        var access = await _access.AuthorizeAsync(caller, listId, ListRole.Viewer, ct);
        if (!access.Allowed) return OpResult<ItemResponse>.NotFound();

        var item = await LoadInListAsync(itemId, listId, ct);
        return item is null ? OpResult<ItemResponse>.NotFound() : OpResult<ItemResponse>.Ok(item.ToResponse());
    }

    public async Task<OpResult<ItemResponse>> UpdateAsync(
        Caller caller, Guid? cmdId, Guid listId, Guid itemId, UpdateItemRequest request, CancellationToken ct)
    {
        var access = await _access.AuthorizeAsync(caller, listId, ListRole.Editor, ct);
        if (!access.Allowed) return OpResult<ItemResponse>.NotFound();

        var item = await LoadInListAsync(itemId, listId, ct);
        if (item is null) return OpResult<ItemResponse>.NotFound();

        var commandId = cmdId ?? Guid.CreateVersion7();
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null) return OpResult<ItemResponse>.Ok(item.ToResponse());

        var occurredAt = request.OccurredAt ?? DateTimeOffset.UtcNow;
        var events = new List<object>();

        if (request.TitleProvided)
        {
            var title = request.Title?.Trim();
            if (string.IsNullOrEmpty(title) || title.Length > MaxTitleLength)
                return OpResult<ItemResponse>.Invalid($"Title must be 1..{MaxTitleLength} characters.");
            events.Add(new ItemRenamed(itemId, title, occurredAt, commandId));
        }
        if (request.NotesProvided)
            events.Add(new ItemNotesEdited(itemId, request.Notes, occurredAt, commandId));
        if (request.DueAtProvided)
            events.Add(new ItemDueDateSet(itemId, request.DueAt, occurredAt, commandId));
        if (request.AssigneeEmailProvided)
        {
            var assignee = string.IsNullOrWhiteSpace(request.AssigneeEmail) ? null : request.AssigneeEmail.Trim();
            events.Add(new ItemAssigned(itemId, assignee, occurredAt, commandId));
        }
        if (request.QuantityProvided)
        {
            if (request.Quantity is < 0)
                return OpResult<ItemResponse>.Invalid("`quantity` must be non-negative.");
            events.Add(new ItemQuantitySet(itemId, request.Quantity, request.Unit, occurredAt, commandId));
        }
        if (request.AddTagIds is { Count: > 0 })
            foreach (var tagId in request.AddTagIds.Distinct())
                events.Add(new ItemTagAdded(itemId, tagId, occurredAt, commandId));
        if (request.RemoveTagIds is { Count: > 0 })
            foreach (var tagId in request.RemoveTagIds.Distinct())
                events.Add(new ItemTagRemoved(itemId, tagId, occurredAt, commandId));

        if (events.Count == 0) return OpResult<ItemResponse>.Ok(item.ToResponse());

        _session.SetHeader(EventActor.HeaderKey, caller.Actor);
        await _idempotency.AppendDedupAsync(commandId, itemId, events, ct);

        var updated = await _session.LoadAsync<Item>(itemId, ct);
        return OpResult<ItemResponse>.Ok(updated!.ToResponse());
    }

    public Task<OpResult<ItemResponse>> CompleteAsync(
        Caller caller, Guid? cmdId, Guid listId, Guid itemId, DateTimeOffset? occurredAt, CancellationToken ct) =>
        SingleEventAsync(caller, cmdId, listId, itemId, occurredAt, (id, at, cmd) => new ItemCompleted(id, at, cmd), ct);

    public Task<OpResult<ItemResponse>> ReopenAsync(
        Caller caller, Guid? cmdId, Guid listId, Guid itemId, DateTimeOffset? occurredAt, CancellationToken ct) =>
        SingleEventAsync(caller, cmdId, listId, itemId, occurredAt, (id, at, cmd) => new ItemReopened(id, at, cmd), ct);

    public async Task<OpResult<ItemResponse>> MoveAsync(
        Caller caller, Guid? cmdId, Guid listId, Guid itemId, MoveItemRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.SortOrder))
            return OpResult<ItemResponse>.Invalid("`sortOrder` is required.");
        return await SingleEventAsync(caller, cmdId, listId, itemId, request.OccurredAt,
            (id, at, cmd) => new ItemMoved(id, request.ParentItemId, request.SortOrder, at, cmd), ct);
    }

    public async Task<OpResult> DeleteAsync(
        Caller caller, Guid? cmdId, Guid listId, Guid itemId, DateTimeOffset? occurredAt, CancellationToken ct)
    {
        var access = await _access.AuthorizeAsync(caller, listId, ListRole.Editor, ct);
        if (!access.Allowed) return OpResult.NotFound();

        var item = await LoadInListAsync(itemId, listId, ct);
        if (item is null) return OpResult.NotFound();

        var commandId = cmdId ?? Guid.CreateVersion7();
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null) return OpResult.Ok();

        var occurredAtValue = occurredAt ?? DateTimeOffset.UtcNow;
        _session.SetHeader(EventActor.HeaderKey, caller.Actor);
        await _idempotency.AppendDedupAsync(
            commandId, itemId, new object[] { new ItemDeleted(itemId, occurredAtValue, commandId) }, ct);

        return OpResult.Ok();
    }

    private async Task<OpResult<ItemResponse>> SingleEventAsync(
        Caller caller,
        Guid? cmdId,
        Guid listId,
        Guid itemId,
        DateTimeOffset? occurredAtRaw,
        Func<Guid, DateTimeOffset, Guid, object> makeEvent,
        CancellationToken ct)
    {
        var access = await _access.AuthorizeAsync(caller, listId, ListRole.Editor, ct);
        if (!access.Allowed) return OpResult<ItemResponse>.NotFound();

        var item = await LoadInListAsync(itemId, listId, ct);
        if (item is null) return OpResult<ItemResponse>.NotFound();

        var commandId = cmdId ?? Guid.CreateVersion7();
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null) return OpResult<ItemResponse>.Ok(item.ToResponse());

        var occurredAt = occurredAtRaw ?? DateTimeOffset.UtcNow;
        _session.SetHeader(EventActor.HeaderKey, caller.Actor);
        await _idempotency.AppendDedupAsync(
            commandId, itemId, new[] { makeEvent(itemId, occurredAt, commandId) }, ct);

        var updated = await _session.LoadAsync<Item>(itemId, ct);
        return OpResult<ItemResponse>.Ok(updated!.ToResponse());
    }

    /// <summary>
    /// Re-resolve the aggregate after losing the create dedup race: prefer the stored
    /// <see cref="ProcessedCommand.AggregateId"/> (the original create's id), falling back to
    /// the body id.
    /// </summary>
    private async Task<Item?> ReResolveCreatedAsync(Guid commandId, Guid bodyId, CancellationToken ct)
    {
        var seen = await _idempotency.SeenAsync(commandId, ct);
        return await _session.LoadAsync<Item>(seen?.AggregateId ?? bodyId, ct);
    }

    /// <summary>
    /// The list a (live) item belongs to, or <c>null</c> if no such live item exists. A bare
    /// lookup with no membership check — callers (e.g. MCP tools that only know a task id) pass
    /// the result straight into a service method that DOES enforce membership, so a non-member
    /// still gets a uniform "not found" and existence is never leaked.
    /// </summary>
    public async Task<Guid?> FindListIdAsync(Guid itemId, CancellationToken ct)
    {
        var item = await _session.LoadAsync<Item>(itemId, ct);
        return item is null || item.Deleted ? null : item.ListId;
    }

    /// <summary>Loads an item only if it lives in <paramref name="listId"/> and isn't tombstoned.</summary>
    private async Task<Item?> LoadInListAsync(Guid itemId, Guid listId, CancellationToken ct)
    {
        var item = await _session.LoadAsync<Item>(itemId, ct);
        return item is null || item.ListId != listId || item.Deleted ? null : item;
    }
}
