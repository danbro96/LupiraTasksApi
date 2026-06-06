using LupiraTasksApi.Handlers;
using LupiraTasksApi.Models.Items;

namespace LupiraTasksApi.Endpoints;

public static class ItemsEndpoints
{
    public static IEndpointRouteBuilder MapItems(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/lists/{listId:guid}/items")
            .RequireAuthorization()
            .WithTags("Items");

        group.MapGet("/", (
                Guid listId,
                bool? completed,
                Guid? tagId,
                Guid? parentItemId,
                string? assignedTo,
                ItemsHandler h,
                CancellationToken ct) =>
            h.ListAsync(listId, completed, tagId, parentItemId, assignedTo, ct))
            .WithSummary("List a list's items (Viewer+).")
            .WithDescription("Excludes deleted items; ordered by `sortOrder`. Filters: `completed`, `tagId`, `parentItemId`, `assignedTo`.")
            .Produces<ItemCollectionResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/", (HttpContext ctx, Guid listId, CreateItemRequest body, ItemsHandler h, CancellationToken ct) =>
                h.CreateAsync(ctx, listId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Add an item (Editor+).")
            .WithDescription("Body `{ id (GUIDv7), title, parentItemId?, dueAt?, assigneeEmail?, quantity?, unit?, tagIds?, sortOrder, occurredAt? }`. Re-sending an existing id is idempotent.")
            .Produces<ItemResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{itemId:guid}", (Guid listId, Guid itemId, ItemsHandler h, CancellationToken ct) =>
                h.GetAsync(listId, itemId, ct))
            .WithSummary("Get a single item (Viewer+).")
            .Produces<ItemResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPatch("/{itemId:guid}", (HttpContext ctx, Guid listId, Guid itemId, UpdateItemRequest body, ItemsHandler h, CancellationToken ct) =>
                h.UpdateAsync(ctx, listId, itemId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Edit item fields (Editor+).")
            .WithDescription("One event per changed field. Use the `*Provided` flags so a null can mean 'clear' rather than 'unchanged'. Tags via `addTagIds`/`removeTagIds`.")
            .Produces<ItemResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{itemId:guid}/complete", (HttpContext ctx, Guid listId, Guid itemId, ItemTimestampRequest? body, ItemsHandler h, CancellationToken ct) =>
                h.CompleteAsync(ctx, listId, itemId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Mark an item complete (Editor+).")
            .Produces<ItemResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{itemId:guid}/reopen", (HttpContext ctx, Guid listId, Guid itemId, ItemTimestampRequest? body, ItemsHandler h, CancellationToken ct) =>
                h.ReopenAsync(ctx, listId, itemId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Reopen a completed item (Editor+).")
            .Produces<ItemResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{itemId:guid}/move", (HttpContext ctx, Guid listId, Guid itemId, MoveItemRequest body, ItemsHandler h, CancellationToken ct) =>
                h.MoveAsync(ctx, listId, itemId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Reparent / reorder an item (Editor+).")
            .WithDescription("Carries the fractional-index `sortOrder` string and optional `parentItemId`.")
            .Produces<ItemResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{itemId:guid}", (HttpContext ctx, Guid listId, Guid itemId, DateTimeOffset? occurredAt, ItemsHandler h, CancellationToken ct) =>
                h.DeleteAsync(ctx, listId, itemId, occurredAt, ct))
            .WithIdempotencyKey()
            .WithSummary("Delete an item (Editor+). Tombstone.")
            .WithDescription("Optional `?occurredAt=` (ISO-8601) carries the client timestamp for LWW.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
