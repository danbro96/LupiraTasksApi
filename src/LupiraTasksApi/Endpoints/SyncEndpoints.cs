using Lupira.Sync;
using LupiraTasksApi.Core.Dtos.Lists;
using LupiraTasksApi.Core.Dtos.Sync;
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
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("SyncList");

        var feeds = app.MapGroup("/sync")
            .RequireAuthorization()
            .WithTags("Sync");

        feeds.MapGet("/lists", (string? since, int? limit, SyncHandler h, CancellationToken ct) =>
                h.ListsAsync(since, limit, ct))
            .WithSummary("Lists the caller can read that changed since a cursor, for an offline mirror.")
            .WithDescription(FeedDescription("Lists leave the mirror through `reset`, so `deleted` is always empty."))
            .Produces<SyncPage<ListDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("SyncLists");

        feeds.MapGet("/items", (string? since, int? limit, SyncHandler h, CancellationToken ct) =>
                h.ItemsAsync(since, limit, ct))
            .WithSummary("Items of the caller's readable lists that changed since a cursor, with per-field guards.")
            .WithDescription(FeedDescription("`deleted` holds the ids of deleted items."))
            .Produces<SyncPage<ItemSyncChange>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("SyncItems");

        return app;
    }

    private static string FeedDescription(string deleted) =>
        "Omit `since` for a full sync; then pass back each returned `cursor`, looping while `hasMore`. " +
        "`reset: true` = drop the mirror before applying the page (first page of a full sync, or the caller " +
        "gained or lost a list). `limit` defaults to 200, max 500. " + deleted;
}
