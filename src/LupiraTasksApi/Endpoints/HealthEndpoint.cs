using LupiraTasksApi.Models;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LupiraTasksApi.Endpoints;

public static class HealthEndpoint
{
    public static IEndpointConventionBuilder MapHealthEndpoint(this IEndpointRouteBuilder app) =>
        app.MapGet("/healthz", Ok<HealthResponse> () => TypedResults.Ok(new HealthResponse { Status = "ok" }))
            .AllowAnonymous()
            .DisableHttpMetrics()
            .WithTags("Meta")
            .WithSummary("Liveness probe.")
            .WithDescription(
                """
                Returns 200 with `{ "status": "ok" }` as soon as the process is up. Used by the
                container healthcheck. Anonymous — no token required.
                """)
            .Produces<HealthResponse>(StatusCodes.Status200OK);
}
