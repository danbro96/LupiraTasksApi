using System.Threading.RateLimiting;
using JasperFx;
using Lupira.Auth.Jwt;
using Lupira.Hosting.Defaults;
using Lupira.Hosting.Health;
using Lupira.Hosting.LanEdge;
using Lupira.Hosting.Observability;
using Lupira.Hosting.OpenApi;
using Lupira.Hosting.Problems;
using Lupira.Mcp;
using Lupira.Postgres.Health;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Core.Application.Shares;
using LupiraTasksApi.Core.Data;
using LupiraTasksApi.Core.Domain.Items;
using LupiraTasksApi.Core.Domain.Lists;
using LupiraTasksApi.Core.Domain.Shares;
using LupiraTasksApi.Dav;
using LupiraTasksApi.Endpoints;
using LupiraTasksApi.Handlers;
using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.OpenApi;
using Weasel.Core;

var builder = WebApplication.CreateBuilder(args);

// Config (connection string) + environment are read LAZILY from the service provider at build
// time — not eagerly off `builder` — so a test host (WebApplicationFactory) can override the
// connection via ConfigureAppConfiguration before Marten resolves it.
builder.Services
    .AddMarten(sp =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var env = sp.GetRequiredService<IHostEnvironment>();
        var connectionString = config.GetConnectionString("tasks")
            ?? throw new InvalidOperationException("ConnectionStrings:tasks is required.");

        var opts = new StoreOptions();
        opts.Connection(connectionString);
        opts.DatabaseSchemaName = "tasks";
        // Store enums as strings in the event/document JSON (not integers), so reordering or inserting
        // an enum value never reinterprets history — the append-only-safe encoding (matches cal-api).
        opts.UseSystemTextJsonForSerialization(EnumStorage.AsString);
        opts.AutoCreateSchemaObjects = env.IsDevelopment()
            ? AutoCreate.CreateOrUpdate
            : AutoCreate.None;
        opts.UseLupiraTasks();
        return opts;
    })
    .UseLightweightSessions();

// Liveness (/livez) + readiness (/readyz, pings Postgres) probes.
builder.Services.AddLupiraHealth().AddReadyCheck<DatabaseReadyCheck>("postgres");

builder.Services.Configure<ShareLinkOptions>(builder.Configuration.GetSection(ShareLinkOptions.SectionName));

// Caller identity + authorization + per-request handlers. CurrentUser reads the
// validated JWT via IHttpContextAccessor and never writes to the DB.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<CallerFactory>();

// The bounded context (Application + Data services from LupiraTasksApi.Core) in one call —
// the single source of truth shared by REST handlers and MCP tools, reused as-is by any future
// host (worker/CLI). Host-specific composition (Marten store, CurrentUser, handlers) stays here.
builder.Services.AddTasksCore();

builder.Services.AddScoped<MeHandler>();
builder.Services.AddScoped<UsersHandler>();
builder.Services.AddScoped<ListsHandler>();
builder.Services.AddScoped<ItemsHandler>();
builder.Services.AddScoped<RelationsHandler>();
builder.Services.AddScoped<SyncHandler>();
builder.Services.AddScoped<SharesHandler>();
builder.Services.AddScoped<SharedHandler>();
builder.Services.AddScoped<DavBackendHandler>();

// MCP agent surface. The [McpServerToolType] tools in this assembly call the same
// Application services as the REST handlers (no second source of truth). Mounted at /mcp
// over Streamable HTTP, secured by the same OIDC JWT bearer (see MapMcp below), and kept
// LAN/WireGuard-only — never published through the Cloudflare Tunnel.
builder.Services.AddLupiraMcp().WithToolsFromAssembly();

builder.Services.AddLupiraProblems();

builder.Services.AddOpenApi("v1", options => options.AddLupiraConventions(o =>
{
    o.Title = "Lupira Tasks API";
    o.Description =
        "Task and command processing backend for Lupira. " +
        "Authenticate with a Bearer token issued by the OIDC provider (Authentik).";
    o.IdempotencyHeader<IdempotentMutation>(
        "Client-generated GUIDv7 command id. A redelivery with the same key is a " +
        "no-op that returns the prior result (offline-safe retry).");
    // The "/shared/{token}" group carries the token in the path template, but the handlers read
    // it from the authenticated principal (ShareToken scheme) rather than binding a route arg —
    // so the generator omits the parameter and emits an invalid path. Declare it explicitly.
    o.OperationTransformers.Add((operation, context, _) =>
    {
        if ((context.Description.RelativePath ?? string.Empty).Contains("{token}", StringComparison.OrdinalIgnoreCase)
            && !(operation.Parameters?.Any(p => p.Name == "token" && p.In == ParameterLocation.Path) ?? false))
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "token",
                In = ParameterLocation.Path,
                Required = true,
                Description = "Opaque share-link token (validated by the ShareToken auth scheme).",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            });
        }

        return Task.CompletedTask;
    });
}));

var auth = builder.AddLupiraJwt(o =>
{
    o.Validation = JwtValidationProfile.Strict;
    o.RelaxHttpsMetadataInDevelopment = false;
    o.RequireConfig = OidcConfigRequirement.Always;
    o.DevOrJwtDefault = true;
});

// Account-less share-link recipients on /shared/{token}. Always registered (share links work in
// prod); used only by the "ShareToken" policy below, so it never affects the default scheme.
auth.AddScheme<AuthenticationSchemeOptions, ShareTokenAuthHandler>(ShareTokenAuthHandler.SchemeName, _ => { });

builder.Services.AddAuthorizationBuilder()
    // The /shared/{token} group authenticates specifically with the ShareToken scheme, regardless of
    // the default (member) scheme.
    .AddPolicy(ShareTokenAuthHandler.SchemeName, p => p
        .AddAuthenticationSchemes(ShareTokenAuthHandler.SchemeName)
        .RequireAuthenticatedUser())
    .AddLupiraGatewayAzpPolicy([], builder.Configuration["DavGateway:ClientId"]);

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    var permitsPerMinute = builder.Configuration.GetValue("RateLimit:RequestsPerMinute", 120);
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        // Partition per authenticated caller (email = the OIDC subject), falling back to
        // the remote IP for anonymous requests.
        var key = ctx.User.FindFirst("email")?.Value
               ?? ctx.Connection.RemoteIpAddress?.ToString()
               ?? "anon";
        return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = permitsPerMinute,
            TokensPerPeriod = permitsPerMinute,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
});

var allowedOrigins = builder.Configuration.GetSection("Auth:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
if (allowedOrigins.Length > 0)
{
    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        p.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));
}

builder.AddLupiraDefaults(o =>
{
    o.CaseInsensitiveProperties = true;
    o.ThrowOnBadRequest = true;
    o.ForwardedHeaders = ForwardedHeaders.None;
});

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1_000_000);

builder.AddLupiraTelemetry("lupira-tasks-api");

var app = builder.Build();

// One-shot schema migration. Prod runs with AutoCreate.None, so schema changes are a
// deliberate `--apply-schema` invocation (e.g. `docker exec ... --apply-schema`), never
// a side-effect of boot. Short-circuits before the host starts.
if (args.Contains("--apply-schema"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
    await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
    Console.WriteLine("Schema applied.");
    return;
}

// One-shot projection rebuild. Snapshots are inline, so a projection/LWW formula fix heals only on a
// deliberate replay from event zero (e.g. `docker exec ... --rebuild-projections`), never at boot.
// Runs the async daemon once to rebuild each aggregate, then exits.
if (args.Contains("--rebuild-projections"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
    using var daemon = await store.BuildProjectionDaemonAsync();
    await daemon.RebuildProjectionAsync<TodoList>(CancellationToken.None);
    await daemon.RebuildProjectionAsync<Item>(CancellationToken.None);
    await daemon.RebuildProjectionAsync<ShareLink>(CancellationToken.None);
    Console.WriteLine("Projections rebuilt.");
    return;
}

// Keep the LAN-only surfaces (/mcp + its discovery metadata, /dav-backend) LAN/WireGuard-only: reject
// anything that arrived via the Cloudflare Tunnel (backstop behind the ingress not routing them at all).
// Before auth so a tunnelled probe never even receives a challenge.
app.UseLanOnlySurfaces("/mcp", "/dav-backend", "/.well-known/oauth-protected-resource");

if (allowedOrigins.Length > 0) app.UseCors();
app.UseLupiraDefaults();
app.UseExceptionHandler();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapLupiraOpenApi(o => o.Title = "Lupira Tasks API");

app.MapLupiraHealth();

app.MapLupiraPing();
app.MapMe();
app.MapUsers();
app.MapLists();
app.MapItems();
app.MapRelations();
app.MapSync();
app.MapShares();
app.MapShared();

// The internal DAV-backend (VTODO) seam the LupiraDavApi gateway consumes (LAN-only, gateway-authed).
app.MapDavBackend();

// Agent MCP surface (Streamable HTTP). Mapped AFTER UseAuthentication/UseAuthorization so
// the same JWT bearer validates it; RequireAuthorization rejects anonymous calls with 401.
// Exposure is LAN/WireGuard-only — the Cloudflare Tunnel must not route /mcp (see deploy docs).
// RFC 9728 metadata lets MCP clients discover the Authentik issuer from the 401 challenge. Read from
// app.Configuration so a test host's config override is honoured.
app.MapMcpResourceMetadata(app.Configuration[$"{OidcAuthOptions.SectionName}:Authority"]);
app.MapMcp("/mcp")
   .RequireAuthorization();

app.Run();

// Exposed so the integration test project can host the app in-memory via
// WebApplicationFactory<Program> (top-level statements otherwise emit an internal Program).
public partial class Program;
