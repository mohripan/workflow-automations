using FlowForge.Infrastructure;
using FlowForge.Infrastructure.Messaging.Abstractions;
using FlowForge.Infrastructure.Messaging.Redis;
using Microsoft.AspNetCore.HttpOverrides;
using OpenTelemetry.Trace;
using FlowForge.WebApi.Hubs;
using FlowForge.WebApi.Middleware;
using FlowForge.WebApi.Options;
using FlowForge.WebApi.Services;
using FlowForge.WebApi.Workers;
using FlowForge.WebApi.Validators;
using FlowForge.WebApi.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using FluentValidation;
using FluentValidation.AspNetCore;
using System.Security.Claims;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Add Infrastructure
builder.Services.AddInfrastructure(builder.Configuration, "WebApi");
builder.Services.AddOpenTelemetry().WithTracing(t => t.AddAspNetCoreInstrumentation());

// Add Audit Logging (requires HttpContext — must come after AddInfrastructure)
builder.Services.AddHttpContextAccessor();
builder.Services.AddAuditLogging();

// Add Health Checks
var pgConnStr = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:DefaultConnection");
var redisConnStr = builder.Configuration["Redis:ConnectionString"]
    ?? throw new InvalidOperationException("Missing Redis:ConnectionString");

builder.Services.AddHealthChecks()
    .AddNpgSql(pgConnStr, healthQuery: "SELECT 1;", name: "postgres")
    .AddRedis(redisConnStr, name: "redis");

// Add Authentication
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Keycloak:Authority"];
        options.Audience  = builder.Configuration["Keycloak:Audience"];
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();

        // In Docker, the WebAPI fetches OIDC metadata via the internal container hostname
        // (keycloak:8080) while browser-issued tokens carry the external issuer URL
        // (localhost:8180). MetadataAddress overrides where keys are fetched from, and
        // ValidIssuers explicitly lists every accepted issuer so both paths are trusted.
        var metadataAddress = builder.Configuration["Keycloak:MetadataAddress"];
        if (metadataAddress is not null)
            options.MetadataAddress = metadataAddress;

        var validIssuers = builder.Configuration
            .GetSection("Keycloak:ValidIssuers").Get<string[]>();

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidIssuers             = validIssuers,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ClockSkew                = TimeSpan.FromSeconds(30),
        };

        // SignalR clients cannot set the Authorization header on the WebSocket upgrade
        // request; the token is passed in the query string instead.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = token;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly",       p => p.RequireRole("admin"));
    options.AddPolicy("OperatorOrAbove", p => p.RequireRole("admin", "operator"));
    options.AddPolicy("ViewerOrAbove",   p => p.RequireRole("admin", "operator", "viewer"));
    // M2M: tokens issued to the flowforge-jobautomator service account
    options.AddPolicy("InternalService", p => p.RequireClaim("azp", "flowforge-jobautomator"));
});

builder.Services.AddSingleton<IClaimsTransformation, KeycloakRolesClaimsTransformation>();

// Add Services
builder.Services.AddScoped<IAutomationService, AutomationService>();
builder.Services.AddScoped<IJobService, JobService>();

// Add Controllers
builder.Services.AddControllers(options =>
{
    options.Filters.Add(new AuthorizeFilter());
});

// Add OpenAPI
builder.Services.AddOpenApi();

// Add FluentValidation
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<CreateAutomationRequestValidator>();

// Add SignalR
builder.Services.AddSignalR();

// Worker options
builder.Services.Configure<OutboxRelayOptions>(
    builder.Configuration.GetSection(OutboxRelayOptions.SectionName));

// Add Background Workers
builder.Services.AddHostedService<AutomationTriggeredConsumer>();
builder.Services.AddHostedService<JobStatusChangedConsumer>();
builder.Services.AddHostedService<OutboxRelayWorker>();

// Add Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    // Anonymous webhook endpoint: 30 requests / minute per IP
    options.AddPolicy("webhook", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit          = 30,
                Window               = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            }));

    // Global: authenticated non-admin users → 300 / minute per sub claim.
    // Anonymous and admin requests are not globally limited.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null || context.User.IsInRole("admin"))
            return RateLimitPartition.GetNoLimiter("unlimited");

        return RateLimitPartition.GetFixedWindowLimiter(userId, _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit          = 300,
                Window               = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0,
            });
    });

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Too many requests. Please retry after 60 seconds." }, ct);
    };
});

// Add CORS
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? ["http://localhost:3000"])
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()));

var app = builder.Build();

// Programmatic Migration
using (var scope = app.Services.CreateScope())
{
    await FlowForge.Infrastructure.Persistence.DatabaseInitializer.InitializeDatabasesAsync(scope.ServiceProvider);
}

// Bootstrap Redis consumer groups
var bootstrapper = app.Services.GetRequiredService<IStreamBootstrapper>();
await bootstrapper.EnsureAsync(StreamNames.AutomationTriggered, "webapi");
await bootstrapper.EnsureAsync(StreamNames.JobStatusChanged, "webapi");

// Middleware
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/openapi/v1.json", "FlowForge Web API v1");
        c.RoutePrefix = "swagger";
    });
}

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapHub<JobStatusHub>("/hubs/job-status").RequireAuthorization();

// Health endpoints — must be reachable without a token (liveness probes)
app.MapHealthChecks("/health/live",  new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = _ => true  }).AllowAnonymous();

app.Run();

// Required for WebApplicationFactory<Program> in test projects
public partial class Program { }
