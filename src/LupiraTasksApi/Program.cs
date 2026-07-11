using JasperFx;
using LupiraTasksApi.Application;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Data;
using LupiraTasksApi.Dav;
using LupiraTasksApi.Domain.Items;
using LupiraTasksApi.Domain.Lists;
using LupiraTasksApi.Domain.Shares;
using LupiraTasksApi.Endpoints;
using LupiraTasksApi.Handlers;
using LupiraTasksApi.Http;
using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

// The build-time OpenAPI emitter (Microsoft.Extensions.ApiDescription.Server's GetDocument.Insider)
// loads this assembly to walk the document provider but never serves a request — so skip startup
// config that requires real infrastructure (here: the OIDC authority/audience), letting `dotnet
// build` emit the spec on a machine with no auth config or DB.
var isOpenApiBuild = Environment.GetCommandLineArgs()
    .Any(a => a.Contains("getdocument", StringComparison.OrdinalIgnoreCase));

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
        opts.UseSystemTextJsonForSerialization();
        opts.AutoCreateSchemaObjects = env.IsDevelopment()
            ? AutoCreate.CreateOrUpdate
            : AutoCreate.None;
        MartenRegistrations.Configure(opts);
        return opts;
    })
    .UseLightweightSessions();

// Liveness (/livez) + readiness (/readyz, pings Postgres) probes.
builder.Services.AddAppHealthChecks();

builder.Services.Configure<OidcAuthOptions>(builder.Configuration.GetSection(OidcAuthOptions.SectionName));
builder.Services.Configure<ShareLinkOptions>(builder.Configuration.GetSection(ShareLinkOptions.SectionName));

// Caller identity + authorization + per-request handlers. CurrentUser reads the
// validated JWT via IHttpContextAccessor and never writes to the DB.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();

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
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer((document, context, _) =>
    {
        document.Info = new()
        {
            Title = "Lupira Tasks API",
            Version = "v1",
            Description =
                "Task and command processing backend for Lupira. " +
                "Authenticate with a Bearer token issued by the OIDC provider (Authentik).",
        };
        document.Components ??= new();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "OIDC bearer token. Send as `Authorization: Bearer <token>`.",
        };
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, _) =>
    {
        // The "/shared/{token}" group carries the token in the path template, but the handlers read
        // it from the authenticated principal (ShareToken scheme) rather than binding a route arg —
        // so the generator omits the parameter and emits an invalid path. Declare it explicitly.
        if ((context.Description.RelativePath ?? "").Contains("{token}", StringComparison.OrdinalIgnoreCase)
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

        var endpointMetadata = context.Description.ActionDescriptor.EndpointMetadata;
        var requiresAuth = endpointMetadata.OfType<IAuthorizeData>().Any()
                        && !endpointMetadata.OfType<IAllowAnonymous>().Any();
        if (requiresAuth)
        {
            operation.Security ??= new List<OpenApiSecurityRequirement>();
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = new List<string>(),
            });
        }

        // Document the optional Idempotency-Key header on mutations tagged with it.
        if (endpointMetadata.OfType<IdempotentMutation>().Any())
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = IdempotencyKey.HeaderName,
                In = ParameterLocation.Header,
                Required = false,
                Description =
                    "Client-generated GUIDv7 command id. A redelivery with the same key is a " +
                    "no-op that returns the prior result (offline-safe retry).",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
            });
        }

        return Task.CompletedTask;
    });
});

var oidc = builder.Configuration.GetSection(OidcAuthOptions.SectionName).Get<OidcAuthOptions>() ?? new OidcAuthOptions();
if (!isOpenApiBuild && string.IsNullOrWhiteSpace(oidc.Authority))
{
    throw new InvalidOperationException("Auth:Oidc:Authority is required.");
}
if (!isOpenApiBuild && string.IsNullOrWhiteSpace(oidc.Audience))
{
    throw new InvalidOperationException("Auth:Oidc:Audience is required.");
}
// In Development a policy scheme is the default: it forwards to the dev-header handler when
// X-Dev-User is present, else to the real JWT bearer (so real Authentik tokens still work in
// dev). In every other environment the default is plain JWT bearer and the dev handler below
// is never registered.
const string devOrJwtScheme = "DevOrJwt";
var defaultScheme = builder.Environment.IsDevelopment()
    ? devOrJwtScheme
    : JwtBearerDefaults.AuthenticationScheme;

var authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = defaultScheme;
    options.DefaultChallengeScheme = defaultScheme;
});

authBuilder.AddJwtBearer(opts =>
{
    opts.Authority = oidc.Authority;
    opts.Audience = oidc.Audience;
    // Subject = email; group membership drives admin. Keep raw claim types (Authentik
    // emits "email"/"groups"), so don't remap inbound claims to legacy XML URIs.
    opts.MapInboundClaims = false;
    opts.TokenValidationParameters = new TokenValidationParameters
    {
        // Issuer + audience are mandatory (guarded at startup above), so validate
        // unconditionally — never silently disable validation on a blank config.
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        NameClaimType = "email",
        RoleClaimType = "groups",
    };
    opts.Events = new JwtBearerEvents
    {
        // MCP auth spec: a 401 on /mcp advertises the RFC 9728 metadata so clients can discover the issuer.
        OnChallenge = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/mcp"))
                ctx.Response.Headers.Append("WWW-Authenticate",
                    $"Bearer resource_metadata=\"{McpResourceMetadata.ResourceMetadataUrl(ctx.Request)}\"");
            return Task.CompletedTask;
        },
    };
});

// Account-less share-link recipients on /shared/{token}. Always registered (share links work in
// prod); used only by the "ShareToken" policy below, so it never affects the default scheme.
authBuilder.AddScheme<AuthenticationSchemeOptions, ShareTokenAuthHandler>(ShareTokenAuthHandler.SchemeName, _ => { });

if (builder.Environment.IsDevelopment())
{
    // Development-only: allow X-Dev-User header auth so the API can be exercised without Authentik.
    authBuilder.AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, _ => { });
    authBuilder.AddPolicyScheme(devOrJwtScheme, devOrJwtScheme, options =>
    {
        options.ForwardDefaultSelector = ctx =>
            ctx.Request.Headers.ContainsKey(DevAuthHandler.HeaderName)
                ? DevAuthHandler.SchemeName
                : JwtBearerDefaults.AuthenticationScheme;
    });
}

var davGatewayClientId = builder.Configuration["DavGateway:ClientId"];
builder.Services.AddAuthorization(o =>
{
    // The /shared/{token} group authenticates specifically with the ShareToken scheme, regardless of
    // the default (member) scheme.
    o.AddPolicy(ShareTokenAuthHandler.SchemeName, p => p
        .AddAuthenticationSchemes(ShareTokenAuthHandler.SchemeName)
        .RequireAuthenticatedUser());

    // The /dav-backend seam: a valid token for this API (aud) minted by the DAV gateway's client (azp).
    // Dev-header auth passes in Development so tests can drive the seam directly.
    o.AddPolicy("DavBackendPolicy", p => p
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx =>
            ctx.User.Identity?.AuthenticationType == DevAuthHandler.SchemeName
            || (davGatewayClientId is not null && ctx.User.HasClaim("azp", davGatewayClientId))));
});

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

builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1_000_000);

var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
if (!string.IsNullOrWhiteSpace(otlpEndpoint))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(
            serviceName: "lupira-tasks-api",
            serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
        .WithTracing(t => t
            .AddSource("LupiraTasksApi.*")
            .AddAspNetCoreInstrumentation(o => o.RecordException = true)
            .AddHttpClientInstrumentation()
            .AddOtlpExporter())
        .WithMetrics(m => m
            .AddMeter("LupiraTasksApi.*")
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddOtlpExporter());

    builder.Logging.AddOpenTelemetry(o =>
    {
        o.IncludeFormattedMessage = true;
        o.IncludeScopes = true;
        o.AddOtlpExporter();
    });
}

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

if (allowedOrigins.Length > 0) app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Keep the MCP surface LAN/WireGuard-only: reject any /mcp request that arrived via the
// Cloudflare Tunnel (backstop behind the tunnel ingress not routing /mcp at all).
app.UseMcpLanOnly();

app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
app.MapScalarApiReference("/scalar", o => o
        .WithTitle("Lupira Tasks API")
        .WithTheme(ScalarTheme.BluePlanet))
    .AllowAnonymous();

app.MapGet("/", () => TypedResults.Redirect("/scalar"))
   .ExcludeFromDescription()
   .AllowAnonymous();

app.MapAppHealthChecks(app.Environment);

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
// app.Configuration (not the eagerly-bound `oidc`) so a test host's config override is honoured.
app.MapMcpResourceMetadata(app.Configuration[$"{OidcAuthOptions.SectionName}:Authority"]);
app.MapMcp("/mcp")
   .RequireAuthorization();

app.Run();

// Exposed so the integration test project can host the app in-memory via
// WebApplicationFactory<Program> (top-level statements otherwise emit an internal Program).
public partial class Program;
