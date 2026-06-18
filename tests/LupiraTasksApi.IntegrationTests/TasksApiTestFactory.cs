using System.Net.Http.Headers;
using System.Text;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace LupiraTasksApi.IntegrationTests;

/// <summary>
/// Hosts the real app against an ephemeral Postgres (Testcontainers). Runs in <c>Development</c> so the dev auth
/// handlers are wired: <c>X-Dev-User</c> for the JSON API and any-password HTTP Basic for <c>/dav</c> — no Authentik
/// needed. Marten data is reset per test via <see cref="ResetAsync"/> so listings and the global event sequence
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

    /// <summary>A client authenticated for the <c>/dav</c> surface (HTTP Basic; dev accepts any password).</summary>
    public HttpClient DavClient(string email)
    {
        var client = CreateClient();
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:x"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _postgres.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
