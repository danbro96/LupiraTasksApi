using Marten;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LupiraTasksApi.Endpoints;

/// <summary>
/// Liveness (<c>/livez</c>) and readiness (<c>/readyz</c>) probes built on the ASP.NET Core
/// health-check framework. Liveness reports process-up only; readiness pings Postgres. Names
/// follow the k8s "z-pages" convention; <c>/healthz</c> is deliberately avoided as ambiguous.
/// </summary>
public static class HealthChecks
{
    private const string LiveTag = "live";
    private const string ReadyTag = "ready";

    public static IServiceCollection AddAppHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            // Liveness: the process is up and serving. Touches no dependencies, so a failure
            // here means "restart me", never "a downstream is down".
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [LiveTag])
            // Readiness: Postgres is reachable. Hard timeout so a hung/half-open connection
            // fails fast instead of blocking the probe until Kestrel's request timeout.
            .AddCheck<MartenHealthCheck>(
                "postgres",
                failureStatus: HealthStatus.Unhealthy,
                tags: [ReadyTag],
                timeout: TimeSpan.FromSeconds(3));
        return services;
    }

    public static void MapAppHealthChecks(this IEndpointRouteBuilder app, IHostEnvironment env)
    {
        // Detailed per-dependency JSON only outside Production — the body reveals dependency
        // topology (that we run Postgres, connection state, timings) and these probes are
        // anonymous. Production falls back to the framework's minimal plaintext status.
        var detailed = !env.IsProduction();

        app.MapHealthChecks("/livez", Options(LiveTag, detailed))
            .AllowAnonymous()
            .DisableHttpMetrics();

        app.MapHealthChecks("/readyz", Options(ReadyTag, detailed))
            .AllowAnonymous()
            .DisableHttpMetrics();
    }

    private static HealthCheckOptions Options(string tag, bool detailed)
    {
        var options = new HealthCheckOptions { Predicate = check => check.Tags.Contains(tag) };
        // Leaving ResponseWriter unset keeps the framework default (minimal plaintext status).
        if (detailed) options.ResponseWriter = WriteJsonReport;
        return options;
    }

    private static Task WriteJsonReport(HttpContext context, HealthReport report) =>
        context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                durationMs = e.Value.Duration.TotalMilliseconds,
                description = e.Value.Description,
                error = e.Value.Exception?.Message,
            }),
        });
}

/// <summary>
/// Readiness check: a cheap <c>select 1</c> round-trip proving Postgres is reachable and the
/// connection/credentials work. Preserves the exception on the result so failures are
/// diagnosable (the framework logs it) rather than being swallowed.
/// </summary>
internal sealed class MartenHealthCheck(IDocumentStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = store.QuerySession();
            await session.QueryAsync<int>("select 1", cancellationToken);
            return HealthCheckResult.Healthy("Postgres reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Postgres unreachable.", ex);
        }
    }
}
