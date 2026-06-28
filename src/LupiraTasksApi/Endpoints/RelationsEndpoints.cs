using LupiraTasksApi.Dtos.Relations;
using LupiraTasksApi.Handlers;

namespace LupiraTasksApi.Endpoints;

public static class RelationsEndpoints
{
    public static IEndpointRouteBuilder MapRelations(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/lists/{listId:guid}/items/{itemId:guid}/relations")
            .RequireAuthorization()
            .WithTags("Relations");

        group.MapPost("/", (Guid listId, Guid itemId, CreateRelationRequest body, RelationsHandler h, CancellationToken ct) =>
                h.LinkAsync(listId, itemId, body, ct))
            .WithSummary("Link a task to a cal-api Prompt heartbeat or an external ref (Editor+).")
            .WithDescription("Body `{ toKind, toRef, relationType, metadata? }`. `toKind` e.g. `cal-item` (the checking " +
                "heartbeat Prompt) or `url` (issue/PR/incident/release). `relationType` e.g. `monitors`, `spawned-by`, " +
                "`produced`, `blocked-by`, `relates-to`. Idempotent: re-linking the same edge is a no-op.")
            .Produces<RelationDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/", (Guid listId, Guid itemId, RelationsHandler h, CancellationToken ct) =>
                h.ListAsync(listId, itemId, ct))
            .WithSummary("List a task's relations (Viewer+).")
            .Produces<List<RelationDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/", (Guid listId, Guid itemId, string toKind, string toRef, string relationType, RelationsHandler h, CancellationToken ct) =>
                h.UnlinkAsync(listId, itemId, toKind, toRef, relationType, ct))
            .WithSummary("Remove a task relation by its edge tuple (Editor+).")
            .WithDescription("Identify the edge with `?toKind=&toRef=&relationType=`. Idempotent: removing a link that " +
                "isn't there is a no-op (204).")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
