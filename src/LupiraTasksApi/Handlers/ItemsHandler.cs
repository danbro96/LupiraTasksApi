using JasperFx;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Domain;
using LupiraTasksApi.Domain.Items;
using LupiraTasksApi.Http;
using LupiraTasksApi.Mappers;
using LupiraTasksApi.Models.Items;
using LupiraTasksApi.Services;
using Marten;
using Marten.Exceptions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LupiraTasksApi.Handlers;

/// <summary>
/// Item CRUD over the <c>Item</c> event stream, scoped to a list. Every operation first
/// resolves list membership (404 to non-members) and confirms the item belongs to the
/// route's list before mutating. Mutations stamp the <c>actor</c> header and carry an
/// <c>OccurredAt</c> for per-field LWW; <c>PATCH</c> emits exactly one event per changed
/// field.
/// </summary>
public sealed class ItemsHandler
{
    private const int MaxTitleLength = 256;

    private readonly IDocumentSession _session;
    private readonly CurrentUser _user;
    private readonly AccessResolver _access;
    private readonly Idempotency _idempotency;

    public ItemsHandler(
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

    public async Task<Results<Ok<ItemCollectionResponse>, NotFound, UnauthorizedHttpResult>> ListAsync(
        Guid listId,
        bool? completed,
        Guid? tagId,
        Guid? parentItemId,
        string? assignedTo,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();

        var access = await _access.RequireMembershipAsync(listId, email, ListRole.Viewer, ct);
        if (!access.Allowed) return TypedResults.NotFound();

        // Marten can't translate the per-field-LWW snapshot's computed members across all
        // filters, so fetch the list's live items and filter/sort in memory (family scale).
        var items = await _session.Query<Item>()
            .Where(i => i.ListId == listId && !i.Deleted)
            .ToListAsync(ct);

        IEnumerable<Item> filtered = items;
        if (completed is { } c) filtered = filtered.Where(i => i.Completed == c);
        if (tagId is { } t) filtered = filtered.Where(i => i.Tags.Contains(t));
        if (parentItemId is { } p) filtered = filtered.Where(i => i.ParentItemId == p);
        if (!string.IsNullOrWhiteSpace(assignedTo))
            filtered = filtered.Where(i => string.Equals(i.AssignedTo, assignedTo, StringComparison.OrdinalIgnoreCase));

        var ordered = filtered
            .OrderBy(i => i.SortOrder, StringComparer.Ordinal)
            .Select(i => i.ToResponse())
            .ToList();

        return TypedResults.Ok(new ItemCollectionResponse { Items = ordered });
    }

    public async Task<Results<Ok<ItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> CreateAsync(
        HttpContext ctx,
        Guid listId,
        CreateItemRequest request,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();

        var access = await _access.RequireMembershipAsync(listId, email, ListRole.Editor, ct);
        if (!access.Allowed) return TypedResults.NotFound();

        var title = request.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > MaxTitleLength)
        {
            return Problems.BadRequest($"Title must be 1..{MaxTitleLength} characters.");
        }
        if (request.Id == Guid.Empty)
        {
            return Problems.BadRequest("A client-generated `id` (GUIDv7) is required.");
        }
        if (string.IsNullOrEmpty(request.SortOrder))
        {
            return Problems.BadRequest("`sortOrder` is required.");
        }
        if (request.Quantity is < 0)
        {
            return Problems.BadRequest("`quantity` must be non-negative.");
        }

        var commandId = ResolveCommandId(ctx);
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null)
        {
            // Replay of a recorded create: return the ORIGINAL aggregate (by the stored
            // AggregateId), ignoring the body id — a retried create with the same key but a
            // different body id must NOT spawn a second stream.
            var prior = await _session.LoadAsync<Item>(seen.AggregateId, ct);
            if (prior is not null) return TypedResults.Ok(prior.ToResponse());
        }

        var occurredAt = request.OccurredAt ?? DateTimeOffset.UtcNow;

        _session.SetHeader(EventActor.HeaderKey, email);
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
            if (prior is not null) return TypedResults.Ok(prior.ToResponse());
        }

        var item = await _session.LoadAsync<Item>(request.Id, ct);
        return item is null
            ? Problems.BadRequest("Item could not be created.")
            : TypedResults.Ok(item.ToResponse());
    }

    public async Task<Results<Ok<ItemResponse>, NotFound, UnauthorizedHttpResult>> GetAsync(
        Guid listId,
        Guid itemId,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();

        var access = await _access.RequireMembershipAsync(listId, email, ListRole.Viewer, ct);
        if (!access.Allowed) return TypedResults.NotFound();

        var item = await LoadInListAsync(itemId, listId, ct);
        return item is null ? TypedResults.NotFound() : TypedResults.Ok(item.ToResponse());
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

        var access = await _access.RequireMembershipAsync(listId, email, ListRole.Editor, ct);
        if (!access.Allowed) return TypedResults.NotFound();

        var item = await LoadInListAsync(itemId, listId, ct);
        if (item is null) return TypedResults.NotFound();

        var commandId = ResolveCommandId(ctx);
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null)
        {
            return TypedResults.Ok(item.ToResponse());
        }

        var occurredAt = request.OccurredAt ?? DateTimeOffset.UtcNow;
        var events = new List<object>();

        if (request.TitleProvided)
        {
            var title = request.Title?.Trim();
            if (string.IsNullOrEmpty(title) || title.Length > MaxTitleLength)
            {
                return Problems.BadRequest($"Title must be 1..{MaxTitleLength} characters.");
            }
            events.Add(new ItemRenamed(itemId, title, occurredAt, commandId));
        }
        if (request.NotesProvided)
        {
            events.Add(new ItemNotesEdited(itemId, request.Notes, occurredAt, commandId));
        }
        if (request.DueAtProvided)
        {
            events.Add(new ItemDueDateSet(itemId, request.DueAt, occurredAt, commandId));
        }
        if (request.AssigneeEmailProvided)
        {
            var assignee = string.IsNullOrWhiteSpace(request.AssigneeEmail) ? null : request.AssigneeEmail.Trim();
            events.Add(new ItemAssigned(itemId, assignee, occurredAt, commandId));
        }
        if (request.QuantityProvided)
        {
            if (request.Quantity is < 0)
            {
                return Problems.BadRequest("`quantity` must be non-negative.");
            }
            events.Add(new ItemQuantitySet(itemId, request.Quantity, request.Unit, occurredAt, commandId));
        }
        if (request.AddTagIds is { Count: > 0 })
        {
            foreach (var tagId in request.AddTagIds.Distinct())
                events.Add(new ItemTagAdded(itemId, tagId, occurredAt, commandId));
        }
        if (request.RemoveTagIds is { Count: > 0 })
        {
            foreach (var tagId in request.RemoveTagIds.Distinct())
                events.Add(new ItemTagRemoved(itemId, tagId, occurredAt, commandId));
        }

        if (events.Count == 0)
        {
            return TypedResults.Ok(item.ToResponse());
        }

        _session.SetHeader(EventActor.HeaderKey, email);
        await _idempotency.AppendDedupAsync(commandId, itemId, events, ct);

        var updated = await _session.LoadAsync<Item>(itemId, ct);
        return TypedResults.Ok(updated!.ToResponse());
    }

    public Task<Results<Ok<ItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> CompleteAsync(
        HttpContext ctx, Guid listId, Guid itemId, ItemTimestampRequest? body, CancellationToken ct) =>
        SingleEventAsync(ctx, listId, itemId, body?.OccurredAt,
            (id, at, cmd) => new ItemCompleted(id, at, cmd), ct);

    public Task<Results<Ok<ItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> ReopenAsync(
        HttpContext ctx, Guid listId, Guid itemId, ItemTimestampRequest? body, CancellationToken ct) =>
        SingleEventAsync(ctx, listId, itemId, body?.OccurredAt,
            (id, at, cmd) => new ItemReopened(id, at, cmd), ct);

    public async Task<Results<Ok<ItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> MoveAsync(
        HttpContext ctx, Guid listId, Guid itemId, MoveItemRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.SortOrder))
        {
            return Problems.BadRequest("`sortOrder` is required.");
        }
        return await SingleEventAsync(ctx, listId, itemId, request.OccurredAt,
            (id, at, cmd) => new ItemMoved(id, request.ParentItemId, request.SortOrder, at, cmd), ct);
    }

    public async Task<Results<NoContent, NotFound, UnauthorizedHttpResult>> DeleteAsync(
        HttpContext ctx, Guid listId, Guid itemId, DateTimeOffset? occurredAt, CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();

        var access = await _access.RequireMembershipAsync(listId, email, ListRole.Editor, ct);
        if (!access.Allowed) return TypedResults.NotFound();

        var item = await LoadInListAsync(itemId, listId, ct);
        if (item is null) return TypedResults.NotFound();

        var commandId = ResolveCommandId(ctx);
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null)
        {
            return TypedResults.NoContent();
        }

        var occurredAtValue = occurredAt ?? DateTimeOffset.UtcNow;
        _session.SetHeader(EventActor.HeaderKey, email);
        await _idempotency.AppendDedupAsync(
            commandId, itemId, new object[] { new ItemDeleted(itemId, occurredAtValue, commandId) }, ct);

        return TypedResults.NoContent();
    }

    private async Task<Results<Ok<ItemResponse>, NotFound, ProblemHttpResult, UnauthorizedHttpResult>> SingleEventAsync(
        HttpContext ctx,
        Guid listId,
        Guid itemId,
        DateTimeOffset? occurredAtRaw,
        Func<Guid, DateTimeOffset, Guid, object> makeEvent,
        CancellationToken ct)
    {
        var email = _user.Email;
        if (email is null) return TypedResults.Unauthorized();

        var access = await _access.RequireMembershipAsync(listId, email, ListRole.Editor, ct);
        if (!access.Allowed) return TypedResults.NotFound();

        var item = await LoadInListAsync(itemId, listId, ct);
        if (item is null) return TypedResults.NotFound();

        var commandId = ResolveCommandId(ctx);
        var seen = await _idempotency.SeenAsync(commandId, ct);
        if (seen is not null)
        {
            return TypedResults.Ok(item.ToResponse());
        }

        var occurredAt = occurredAtRaw ?? DateTimeOffset.UtcNow;
        _session.SetHeader(EventActor.HeaderKey, email);
        await _idempotency.AppendDedupAsync(
            commandId, itemId, new[] { makeEvent(itemId, occurredAt, commandId) }, ct);

        var updated = await _session.LoadAsync<Item>(itemId, ct);
        return TypedResults.Ok(updated!.ToResponse());
    }

    /// <summary>
    /// The per-request command id used to stamp every event the command emits and to key
    /// the idempotency ledger: the <c>Idempotency-Key</c> header when present, else a
    /// server-minted GUIDv7. A single command shares one id across all its events (a command
    /// never edits the same field twice, so the LWW tiebreaker stays well-defined).
    /// </summary>
    private static Guid ResolveCommandId(HttpContext ctx) =>
        Idempotency.KeyFrom(ctx) ?? Guid.CreateVersion7();

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

    /// <summary>Loads an item only if it lives in <paramref name="listId"/> and isn't tombstoned.</summary>
    private async Task<Item?> LoadInListAsync(Guid itemId, Guid listId, CancellationToken ct)
    {
        var item = await _session.LoadAsync<Item>(itemId, ct);
        return item is null || item.ListId != listId || item.Deleted ? null : item;
    }
}
