using LupiraTasksApi.Dtos.Me;
using LupiraTasksApi.Handlers;

namespace LupiraTasksApi.Endpoints;

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMe(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/me")
            .RequireAuthorization()
            .WithTags("Me");

        group.MapGet("/", (MeHandler h, CancellationToken ct) => h.GetAsync(ct))
            .WithSummary("Provision and return the caller's profile.")
            .WithDescription(
                """
                Upserts the caller's `UserProfile` from the bearer token (create on first
                login, else bump `LastSeenAt` and refresh display name / admin flag) and
                returns `{ email, displayName, isAdmin }`. Call on app cold start.
                """)
            .Produces<MeResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }
}
