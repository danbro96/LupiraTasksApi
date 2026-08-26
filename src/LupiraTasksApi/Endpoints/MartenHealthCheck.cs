using Marten;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LupiraTasksApi.Endpoints;

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
