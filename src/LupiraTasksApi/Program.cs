using JasperFx;
using LupiraTasksApi;
using LupiraTasksApi.Auth;
using LupiraTasksApi.Endpoints;
using LupiraTasksApi.Handlers;
using LupiraTasksApi.Services;
using Marten;
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

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("tasks")
    ?? throw new InvalidOperationException("ConnectionStrings:tasks is required.");

builder.Services
    .AddMarten(opts =>
    {
        opts.Connection(connectionString);
        opts.DatabaseSchemaName = "tasks";
        opts.UseSystemTextJsonForSerialization();
        opts.AutoCreateSchemaObjects = builder.Environment.IsDevelopment()
            ? AutoCreate.CreateOrUpdate
            : AutoCreate.None;
        MartenRegistrations.Configure(opts);
    })
    .UseLightweightSessions();

// Liveness (/livez) + readiness (/readyz, pings Postgres) probes.
builder.Services.AddAppHealthChecks();

builder.Services.Configure<OidcAuthOptions>(builder.Configuration.GetSection(OidcAuthOptions.SectionName));

// Caller identity + authorization + per-request handlers. CurrentUser reads the
// validated JWT via IHttpContextAccessor and never writes to the DB.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<AccessResolver>();
builder.Services.AddScoped<Idempotency>();
builder.Services.AddScoped<MeHandler>();
builder.Services.AddScoped<ListsHandler>();
builder.Services.AddScoped<ItemsHandler>();
builder.Services.AddScoped<SyncHandler>();

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
                Name = Idempotency.HeaderName,
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
if (string.IsNullOrWhiteSpace(oidc.Authority))
{
    throw new InvalidOperationException("Auth:Oidc:Authority is required.");
}
if (string.IsNullOrWhiteSpace(oidc.Audience))
{
    throw new InvalidOperationException("Auth:Oidc:Audience is required.");
}
builder.Services
    .AddAuthentication(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
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
    });
builder.Services.AddAuthorization();

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

if (allowedOrigins.Length > 0) app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

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
app.MapLists();
app.MapItems();
app.MapSync();

app.Run();
