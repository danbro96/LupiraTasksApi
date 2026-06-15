using LupiraTasksApi.Dtos.Users;
using LupiraTasksApi.Handlers;

namespace LupiraTasksApi.Endpoints;

public static class UsersEndpoints
{
    public static IEndpointRouteBuilder MapUsers(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/users")
            .RequireAuthorization()
            .WithTags("Users");

        group.MapGet("/directory", (string? q, UsersHandler h, CancellationToken ct) =>
                h.DirectoryAsync(q, ct))
            .WithSummary("People seen across the caller's shared lists (for adding members).")
            .WithDescription("`?q=` filters the distinct member emails (case-insensitive substring).")
            .Produces<DirectoryResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }
}
