using Lupira.Identity.Marten;
using Lupira.Marten.Idempotency;
using LupiraTasksApi.Core.Application.Dav;
using LupiraTasksApi.Core.Application.Items;
using LupiraTasksApi.Core.Application.Lists;
using LupiraTasksApi.Core.Application.Shares;
using LupiraTasksApi.Core.Application.Sync;
using LupiraTasksApi.Core.Auth;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the LupiraTasksApi bounded context (the <c>Application</c> + <c>Data</c> services) so any host
/// composes the same service graph in one call — the web API today, a worker/CLI tomorrow. Lives in Core and
/// depends only on the DI abstractions, not ASP.NET, so the "no ASP.NET in the core" rule holds. The host still
/// owns environment-specific composition: <c>AddMarten</c> (connection string + <c>AutoCreate</c> gating, which
/// calls <c>MartenRegistrations.Configure</c>), the <c>HttpContext</c>-based <c>CurrentUser</c>, the transport
/// handlers / MCP tools, and options binding (<c>ShareLinkOptions</c>, <c>SyncFeedOptions</c>).
/// </summary>
public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddTasksCore(this IServiceCollection services) =>
        services
            .AddScoped<AccessResolver>()
            .AddLupiraPrincipalDirectory()
            .AddScoped<Idempotency>()
            .AddScoped<ListService>()
            .AddScoped<ItemService>()
            .AddScoped<RelationService>()
            .AddScoped<SyncService>()
            .AddScoped<SyncFeed>()
            .AddScoped<ShareService>()
            .AddScoped<TaskDavService>();
}
