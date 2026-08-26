using LupiraTasksApi.Core.Dtos.Lists;
using LupiraTasksApi.Handlers;

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
            .WithName("ListLists");

        group.MapPost("/", (HttpContext ctx, CreateListRequest body, ListsHandler h, CancellationToken ct) =>
                h.CreateAsync(ctx, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Create a list; the caller becomes Owner.")
            .WithDescription("Body `{ id (GUIDv7), name, kind, color? }`. `kind` is `Todo`, `Shopping`, or `Agent` " +
                "(agent/system-owned lists). Re-sending an existing id is an idempotent success.")
            .Produces<ListDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("CreateList");

        group.MapGet("/{listId:guid}", (Guid listId, ListsHandler h, CancellationToken ct) =>
                h.GetAsync(listId, ct))
            .WithSummary("Get a list with its members and tags (Viewer+).")
            .Produces<ListDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetList");

        group.MapPatch("/{listId:guid}", (HttpContext ctx, Guid listId, UpdateListRequest body, ListsHandler h, CancellationToken ct) =>
                h.UpdateAsync(ctx, listId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Rename / recolor a list, or set its priority mode (Editor+).")
            .WithDescription("Each provided field emits its own event. Set `colorProvided` to apply `color` (incl. clearing it). Send `simplePriority` (bool) to switch between simple on/off and the full 0..9 scale.")
            .Produces<ListDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("UpdateList");

        group.MapPost("/{listId:guid}/archive", (HttpContext ctx, Guid listId, ListsHandler h, CancellationToken ct) =>
                h.ArchiveAsync(ctx, listId, ct))
            .WithIdempotencyKey()
            .WithSummary("Archive a list (Owner). Soft — items retained.")
            .Produces<ListDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ArchiveList");

        group.MapPost("/{listId:guid}/restore", (HttpContext ctx, Guid listId, ListsHandler h, CancellationToken ct) =>
                h.RestoreAsync(ctx, listId, ct))
            .WithIdempotencyKey()
            .WithSummary("Restore an archived list (Owner).")
            .Produces<ListDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("RestoreList");

        group.MapPost("/{listId:guid}/order", (HttpContext ctx, Guid listId, SetListOrderRequest body, ListsHandler h, CancellationToken ct) =>
                h.SetOrderAsync(ctx, listId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Set the caller's own position for this list (Viewer+).")
            .WithDescription("Body `{ sortOrder }` — a fractional-index key generated between the neighbours " +
                "the list was dropped between. Per-user: other members' ordering is untouched, and this does " +
                "not count as a change to the list. Returned as `sortOrder` on the caller's `ListDto`; " +
                "lists the caller has never ordered come back null and sort by name after the ordered ones.")
            .Produces<ListDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("ReorderListItems");

        group.MapDelete("/{listId:guid}", (HttpContext ctx, Guid listId, ListsHandler h, CancellationToken ct) =>
                h.DeleteAsync(ctx, listId, ct))
            .WithIdempotencyKey()
            .WithSummary("Delete a list (Owner). Tombstone — stream retained.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("DeleteList");

        group.MapPost("/{listId:guid}/members", (HttpContext ctx, Guid listId, AddMemberRequest body, ListsHandler h, CancellationToken ct) =>
                h.AddMemberAsync(ctx, listId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Add a member by email (any member; defaults to Editor).")
            .WithDescription("Body `{ email, role? }`. Direct-add, no invite/accept. An unseen email provisions a " +
                "placeholder principal (its `sub` is upgraded when the person first logs in). Members are returned " +
                "with their `principalId`; use that for role change / removal.")
            .Produces<ListDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("AddListMember");

        group.MapPatch("/{listId:guid}/members/{principalId:guid}", (HttpContext ctx, Guid listId, Guid principalId, UpdateMemberRoleRequest body, ListsHandler h, CancellationToken ct) =>
                h.ChangeMemberRoleAsync(ctx, listId, principalId, body, ct))
            .WithIdempotencyKey()
            .WithSummary("Change a member's role by principal id (Owner only).")
            .Produces<ListDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("UpdateListMember");

        group.MapDelete("/{listId:guid}/members/{principalId:guid}", (HttpContext ctx, Guid listId, Guid principalId, ListsHandler h, CancellationToken ct) =>
                h.RemoveMemberAsync(ctx, listId, principalId, ct))
            .WithIdempotencyKey()
            .WithSummary("Remove a member by principal id, or leave (self). Last owner leaving deletes the list for everyone.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("RemoveListMember");

        return app;
    }
}
