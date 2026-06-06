using LupiraTasksApi.Handlers;
using LupiraTasksApi.Models.Lists;

namespace LupiraTasksApi.Endpoints;

public static class ListsEndpoints
{
    public static IEndpointRouteBuilder MapLists(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/lists")
            .RequireAuthorization()
            .WithTags("Lists");

        group.MapGet("/", (bool? archived, ListsHandler h, CancellationToken ct) =>
                h.ListAsync(archived ?? false, ct))
            .WithSummary("List the lists the caller is a member of.")
            .WithDescription("`?archived=true` returns the caller's archived lists instead of the active ones.")
            .Produces<ListCollectionResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/", (HttpContext ctx, CreateListRequest body, ListsHandler h, CancellationToken ct) =>
                h.CreateAsync(ctx, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Create a list; the caller becomes Owner.")
            .WithDescription("Body `{ id (GUIDv7), name, kind, color? }`. Re-sending an existing id is an idempotent success.")
            .Produces<ListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapGet("/{listId:guid}", (Guid listId, ListsHandler h, CancellationToken ct) =>
                h.GetAsync(listId, ct))
            .WithSummary("Get a list with its members and tags (Viewer+).")
            .Produces<ListResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPatch("/{listId:guid}", (HttpContext ctx, Guid listId, UpdateListRequest body, ListsHandler h, CancellationToken ct) =>
                h.UpdateAsync(ctx, listId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Rename / recolor a list (Editor+).")
            .WithDescription("Each provided field emits its own event. Set `colorProvided` to apply `color` (incl. clearing it).")
            .Produces<ListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{listId:guid}/archive", (HttpContext ctx, Guid listId, ListsHandler h, CancellationToken ct) =>
                h.ArchiveAsync(ctx, listId, ct))
            .WithIdempotencyKey()
            .WithSummary("Archive a list (Owner). Soft — items retained.")
            .Produces<ListResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{listId:guid}/restore", (HttpContext ctx, Guid listId, ListsHandler h, CancellationToken ct) =>
                h.RestoreAsync(ctx, listId, ct))
            .WithIdempotencyKey()
            .WithSummary("Restore an archived list (Owner).")
            .Produces<ListResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{listId:guid}", (HttpContext ctx, Guid listId, ListsHandler h, CancellationToken ct) =>
                h.DeleteAsync(ctx, listId, ct))
            .WithIdempotencyKey()
            .WithSummary("Delete a list (Owner). Tombstone — stream retained.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
