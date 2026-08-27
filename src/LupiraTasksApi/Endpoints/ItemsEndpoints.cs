using LupiraTasksApi.Core.Domain;
using LupiraTasksApi.Core.Dtos.Items;
using LupiraTasksApi.Handlers;

namespace LupiraTasksApi.Endpoints;

public static class ItemsEndpoints
{
    public static IEndpointRouteBuilder MapItems(this IEndpointRouteBuilder app)
    {
        // Cross-list surface (address a task by id alone; the caller need not know its list).
        var top = app.MapGroup("/items").RequireAuthorization().WithTags("Items");

        top.MapGet("/", (string? query, bool? completed, ItemStatus? status, DateTimeOffset? dueFrom, DateTimeOffset? dueTo, ItemsHandler h, CancellationToken ct) =>
                h.SearchAsync(query, completed, status, dueFrom, dueTo, ct))
            .WithSummary("Search items across the caller's lists (Viewer+).")
            .WithDescription("Case-insensitive `query` title substring, optional `completed`/`status`. `dueFrom`/`dueTo` bound `dueAt` half-open `[from, to)`; either bound implies `dueAt` is set. Spans every list the caller is a member of (archived included).")
            .Produces<IReadOnlyList<ItemDto>>(StatusCodes.Status200OK)
            .WithName("ListItems");

        top.MapPatch("/{itemId:guid}", (HttpContext ctx, Guid itemId, UpdateItemRequest body, ItemsHandler h, CancellationToken ct) =>
                h.UpdateByIdAsync(ctx, itemId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Edit item fields addressed by id (Editor+); the list is resolved server-side.")
            .WithDescription("Same body as the list-scoped PATCH (`*Provided` flags). 404 if no such item or the caller can't edit its list.")
            .Produces<ItemDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("UpdateItem");

        top.MapPost("/{itemId:guid}/metadata", (HttpContext ctx, Guid itemId, SetMetadataRequest body, ItemsHandler h, CancellationToken ct) =>
                h.SetMetadataByIdAsync(ctx, itemId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Set an item's metadata addressed by id (Editor+); the list is resolved server-side.")
            .WithDescription("Body `{ metadata (JSON object or null), occurredAt? }`. Whole-field LWW. 404 if no such item or the caller can't edit its list.")
            .Produces<ItemDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("SetItemMetadata");

        var group = app.MapGroup("/lists/{listId:guid}/items")
            .RequireAuthorization()
            .WithTags("Items");

        group.MapGet("/", (
                Guid listId,
                bool? completed,
                Guid? tagId,
                Guid? parentItemId,
                string? assignedTo,
                ItemStatus? status,
                ItemsHandler h,
                CancellationToken ct) =>
            h.ListAsync(listId, completed, tagId, parentItemId, assignedTo, status, ct))
            .WithSummary("List a list's items (Viewer+).")
            .WithDescription("Excludes deleted items; ordered by `sortOrder`. Filters: `completed`, `tagId`, `parentItemId`, `assignedTo`, `status`.")
            .Produces<IReadOnlyList<ItemDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ListListItems");

        group.MapPost("/", (HttpContext ctx, Guid listId, CreateItemRequest body, ItemsHandler h, CancellationToken ct) =>
                h.CreateAsync(ctx, listId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Add an item (Editor+).")
            .WithDescription("Body `{ id (GUIDv7), title, parentItemId?, dueAt?, assigneeEmail?, quantity?, unit?, priority? (0..9), tagIds?, sortOrder, occurredAt? }`. Re-sending an existing id is idempotent.")
            .Produces<ItemDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("CreateListItem");

        group.MapGet("/{itemId:guid}", (Guid listId, Guid itemId, ItemsHandler h, CancellationToken ct) =>
                h.GetAsync(listId, itemId, ct))
            .WithSummary("Get a single item (Viewer+).")
            .Produces<ItemDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetItem");

        group.MapPatch("/{itemId:guid}", (HttpContext ctx, Guid listId, Guid itemId, UpdateItemRequest body, ItemsHandler h, CancellationToken ct) =>
                h.UpdateAsync(ctx, listId, itemId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Edit item fields (Editor+).")
            .WithDescription("One event per changed field. Use the `*Provided` flags so a null can mean 'clear' rather than 'unchanged' (e.g. `priority` 0..9 with `priorityProvided`). Tags via `addTagIds`/`removeTagIds`.")
            .Produces<ItemDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("UpdateListItem");

        group.MapPost("/{itemId:guid}/complete", (HttpContext ctx, Guid listId, Guid itemId, ItemTimestampRequest? body, ItemsHandler h, CancellationToken ct) =>
                h.CompleteAsync(ctx, listId, itemId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Mark an item complete (Editor+).")
            .Produces<ItemDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("CompleteItem");

        group.MapPost("/{itemId:guid}/reopen", (HttpContext ctx, Guid listId, Guid itemId, ItemTimestampRequest? body, ItemsHandler h, CancellationToken ct) =>
                h.ReopenAsync(ctx, listId, itemId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Reopen a completed item (Editor+).")
            .Produces<ItemDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ReopenItem");

        group.MapPost("/{itemId:guid}/status", (HttpContext ctx, Guid listId, Guid itemId, SetStatusRequest body, ItemsHandler h, CancellationToken ct) =>
                h.SetStatusAsync(ctx, listId, itemId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Set an item's lifecycle status (Editor+).")
            .WithDescription("Body `{ status (Open|InProgress|Blocked|Waiting|Done|Cancelled), reason?, occurredAt? }`. " +
                "`Done` is equivalent to completing the item; `completed` is the derived `status == Done`.")
            .Produces<ItemDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("SetItemStatus");

        group.MapPost("/{itemId:guid}/metadata", (HttpContext ctx, Guid listId, Guid itemId, SetMetadataRequest body, ItemsHandler h, CancellationToken ct) =>
                h.SetMetadataAsync(ctx, listId, itemId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Set an item's free-form JSON metadata (Editor+).")
            .WithDescription("Body `{ metadata (JSON object or null), occurredAt? }`. Server-side bookkeeping; never in VTODO or share links. Whole-field LWW.")
            .Produces<ItemDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("SetListItemMetadata");

        group.MapPost("/{itemId:guid}/move", (HttpContext ctx, Guid listId, Guid itemId, MoveItemRequest body, ItemsHandler h, CancellationToken ct) =>
                h.MoveAsync(ctx, listId, itemId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Reparent / reorder an item (Editor+).")
            .WithDescription("Carries the fractional-index `sortOrder` string and optional `parentItemId`.")
            .Produces<ItemDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("MoveItem");

        group.MapDelete("/{itemId:guid}", (HttpContext ctx, Guid listId, Guid itemId, DateTimeOffset? occurredAt, ItemsHandler h, CancellationToken ct) =>
                h.DeleteAsync(ctx, listId, itemId, occurredAt, ct))
            .WithIdempotencyKey()
            .WithSummary("Delete an item (Editor+). Tombstone.")
            .WithDescription("Optional `?occurredAt=` (ISO-8601) carries the client timestamp for LWW.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("DeleteListItem");

        return app;
    }
}
