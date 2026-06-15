using LupiraTasksApi.Dtos.Sync;
using LupiraTasksApi.Handlers;

namespace LupiraTasksApi.Endpoints;

public static class SyncEndpoints
{
    public static IEndpointRouteBuilder MapSync(this IEndpointRouteBuilder app)
    {
        app.MapGet("/lists/{listId:guid}/sync", (Guid listId, long? since, SyncHandler h, CancellationToken ct) =>
                h.GetAsync(listId, since, ct))
            .RequireAuthorization()
            .WithTags("Sync")
            .WithSummary("Offline delta-pull for a list (Viewer+).")
            .WithDescription(
                """
                Returns the current list plus all its live items (v1 full re-derive,
                regardless of `?since=`) and a `nextCursor` to pass on the next pull. The
                client rebases its local mirror onto this base, then re-applies non-acked
                outbox rows.
                """)
            .Produces<SyncResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}
