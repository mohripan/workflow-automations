using FlowForge.Infrastructure.Persistence.Platform;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace FlowForge.Security.Tests;

/// <summary>
/// Starts the WebApi against real Testcontainers (Postgres + Redis),
/// but replaces JWT validation with a test RSA key — no Keycloak required.
/// </summary>
public class TestWebAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg    = new PostgreSqlBuilder().Build();
    private readonly RedisContainer      _redis = new RedisBuilder().Build();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_pg.StartAsync(), _redis.StartAsync());
    }

    public new async Task DisposeAsync()
    {
        await _pg.DisposeAsync();
        await _redis.DisposeAsync();
        await base.DisposeAsync();
    }

    // ── Configuration ─────────────────────────────────────────────────────────

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Infrastructure
                ["ConnectionStrings:DefaultConnection"]            = _pg.GetConnectionString(),
                ["Redis:ConnectionString"]                         = _redis.GetConnectionString(),

                // Job connections — point to same Postgres; not exercised by security tests
                ["JobConnections:wf-jobs-minion:ConnectionString"] = _pg.GetConnectionString(),
                ["JobConnections:wf-jobs-minion:Provider"]         = "PostgreSQL",
                ["JobConnections:wf-jobs-titan:ConnectionString"]  = _pg.GetConnectionString(),
                ["JobConnections:wf-jobs-titan:Provider"]          = "PostgreSQL",

                // Keycloak config values — authority is replaced in ConfigureTestServices
                ["Keycloak:Authority"]                             = "https://test-issuer",
                ["Keycloak:Audience"]                             = TestTokenFactory.Audience,

                // Encryption key (same as integration test suite)
                ["FlowForge:EncryptionKey"]                        = "dGVzdGtleXRlc3RrZXl0ZXN0a2V5dGVzdGtleXRlc3Q=",

                // OTLP — no real collector; telemetry export will fail silently
                ["OpenTelemetry:OtlpEndpoint"]                     = "http://localhost:14317",
                ["AllowedOrigins:0"]                               = "http://localhost:3000",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Replace Keycloak OIDC discovery with a local test RSA key.
            // PostConfigure runs AFTER JwtBearerPostConfigureOptions, so it wins.
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                // Prevent any outbound HTTP call to the Keycloak authority
                options.Authority            = null;
                options.MetadataAddress      = null;
                options.ConfigurationManager = null;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer           = true,
                    ValidIssuer              = TestTokenFactory.Issuer,
                    ValidateAudience         = true,
                    ValidAudience            = TestTokenFactory.Audience,
                    ValidateLifetime         = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey         = TestTokenFactory.Key,
                    ClockSkew                = TimeSpan.Zero,
                };
            });
        });
    }

    // ── Helpers for tests ─────────────────────────────────────────────────────

    /// <summary>Opens a direct EF Core connection to the test Postgres for seeding / assertions.</summary>
    public PlatformDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(_pg.GetConnectionString())
            .Options;
        return new PlatformDbContext(options);
    }

    public HttpClient CreateClientWithToken(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public HttpClient CreateAdminClient()    => CreateClientWithToken(TestTokenFactory.ForAdmin());
    public HttpClient CreateOperatorClient() => CreateClientWithToken(TestTokenFactory.ForOperator());
    public HttpClient CreateViewerClient()   => CreateClientWithToken(TestTokenFactory.ForViewer());
    public HttpClient CreateAnonymousClient() => CreateClient();
}
