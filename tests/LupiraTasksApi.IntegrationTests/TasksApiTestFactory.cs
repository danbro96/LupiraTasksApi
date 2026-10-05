using Lupira.Testing.Postgres;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace LupiraTasksApi.IntegrationTests;

public sealed class TasksApiTestFactory : LupiraApiFactory<Program>
{
    public IDocumentStore Store => Services.GetRequiredService<IDocumentStore>();

    protected override string ConnectionStringName => "tasks";

    protected override string AuthentikSlug => "lupira-tasks";

    protected override Task ApplySchemaAsync() => Store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

    protected override Task ResetDataAsync() => Store.Advanced.ResetAllData();

    // Lift the per-email limiter so a busy serial test run can't trip 429; tests commit serially, so the
    // sync feeds need no settle lag.
    protected override void AddSettings(IDictionary<string, string?> settings)
    {
        settings["RateLimit:RequestsPerMinute"] = "100000";
        settings["Sync:SettleLag"] = "00:00:00";
    }
}
