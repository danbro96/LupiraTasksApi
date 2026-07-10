using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>
/// Hosts the real app against an ephemeral Postgres (Testcontainers). Runs in <c>Development</c> so the dev auth
/// handler is wired (<c>X-Dev-User</c>, which also satisfies the /dav-backend policy) — no Authentik needed.
/// Marten data is reset per test via <see cref="ResetAsync"/> so listings and the global event sequence
/// (the sync-token / cursor) are deterministic. Mirrors LupiraCalApi's <c>CalApiTestFactory</c>.
/// </summary>
public sealed class TasksApiTestFactory : WebApplicationFactory<Program>
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private bool _schemaApplied;

    public TasksApiTestFactory() => _postgres.StartAsync().GetAwaiter().GetResult();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(cfg =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:tasks"] = _postgres.GetConnectionString(),
                // Never contacted (tests auth via X-Dev-User) — feeds the RFC 9728 metadata + JWT challenge.
                ["Auth:Oidc:Authority"] = "https://auth.test/application/o/lupira-tasks/",
                // Lift the per-email limiter so a busy serial test run can't trip 429.
                ["RateLimit:RequestsPerMinute"] = "100000",
            }));
    }

    public IDocumentStore Store => Services.GetRequiredService<IDocumentStore>();

    /// <summary>Ensure the schema exists (once), then wipe all documents + events (and reset the event sequence).</summary>
    public async Task ResetAsync()
    {
        if (!_schemaApplied)
        {
            await Store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            _schemaApplied = true;
        }
        await Store.Advanced.ResetAllData();
    }

    /// <summary>A client authenticated as a member via the Development <c>X-Dev-User</c> header (+ optional groups).</summary>
    public HttpClient ApiClient(string email, params string[] groups)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-User", email);
        if (groups.Length > 0) client.DefaultRequestHeaders.Add("X-Dev-Groups", string.Join(',', groups));
        return client;
    }

    /// <summary>A client with no auth header — for asserting unauthenticated requests are rejected.</summary>
    public HttpClient AnonymousClient() => CreateClient();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _postgres.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
