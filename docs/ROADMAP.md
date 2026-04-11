# ROADMAP.md — FlowForge Security Hardening

This document tracks the next phase of FlowForge development. The focus is on securing the platform end-to-end: authenticated API access via Keycloak, role-based authorization, service-to-service identity, audit trails, and general API hardening. Items are ordered by dependency — each item builds on the ones before it.

---

## Status Legend

| Symbol | Meaning |
|---|---|
| `[ ]` | Not started |
| `[~]` | In progress |
| `[x]` | Done |

---

## Items

| # | Title | Area | Status |
|---|---|---|---|
| 1 | [Keycloak Setup & Realm Configuration](#1-keycloak-setup--realm-configuration) | Infrastructure / AuthN | `[x]` |
| 2 | [WebApi JWT Bearer Authentication](#2-webapi-jwt-bearer-authentication) | AuthN | `[x]` |
| 3 | [Role-Based Authorization (RBAC)](#3-role-based-authorization-rbac) | AuthZ | `[x]` |
| 4 | [SignalR Authentication](#4-signalr-authentication) | AuthN | `[x]` |
| 5 | [Service-to-Service Auth (M2M Client Credentials)](#5-service-to-service-auth-m2m-client-credentials) | AuthN | `[x]` |
| 6 | [User Context & Audit Logging](#6-user-context--audit-logging) | Observability / Security | `[x]` |
| 7 | [Webhook HMAC-SHA256 Upgrade](#7-webhook-hmac-sha256-upgrade) | Security | `[x]` |
| 8 | [Rate Limiting & Throttling](#8-rate-limiting--throttling) | Security | `[x]` |
| 9 | [HTTPS in Development](#9-https-in-development) | Infrastructure | `[x]` |
| 10 | [Security Test Suite](#10-security-test-suite) | Testing | `[x]` |

---

## 1. Keycloak Setup & Realm Configuration

### Problem

There is no identity provider. All API endpoints are fully public — any process that can reach the WebApi port can read, modify, or trigger any automation. Before any authentication can be enforced in code, the identity infrastructure must exist.

### Design

Deploy Keycloak as a docker-compose service and configure it with a `flowforge` realm via an exported realm JSON file committed to the repository. The realm JSON approach means the full IdP configuration is reproducible from a single `docker compose up`.

**Realm structure:**

```
Realm: flowforge
│
├── Clients
│   ├── flowforge-webapi          (confidential, authorization code + client credentials)
│   ├── flowforge-jobautomator    (confidential, client credentials only — M2M)
│   └── flowforge-frontend        (public, authorization code + PKCE — for future UI)
│
└── Roles (realm-level)
    ├── admin       — full CRUD on automations, host groups, DLQ management
    ├── operator    — read/write automations, trigger webhooks, view/cancel jobs
    └── viewer      — read-only: list automations, list jobs, view outputs
```

**docker-compose additions:**

```yaml
services:
  keycloak:
    image: quay.io/keycloak/keycloak:26.2
    command: start-dev --import-realm
    environment:
      KEYCLOAK_ADMIN: admin
      KEYCLOAK_ADMIN_PASSWORD: admin
      KC_DB: postgres
      KC_DB_URL: jdbc:postgresql://postgres-platform:5432/keycloak
      KC_DB_USERNAME: postgres
      KC_DB_PASSWORD: postgres
    ports:
      - "8180:8080"
    volumes:
      - ./keycloak/flowforge-realm.json:/opt/keycloak/data/import/flowforge-realm.json
    depends_on:
      postgres-platform: { condition: service_healthy }
    healthcheck:
      test: ["CMD-SHELL", "exec 3<>/dev/tcp/localhost/9000 && echo -e 'GET /health/ready HTTP/1.1\r\nHost: localhost\r\n\r\n' >&3 && cat <&3 | grep -q '\"status\":\"UP\"'"]
      interval: 10s
      timeout: 5s
      retries: 10
```

**Realm JSON file** (`deploy/docker/keycloak/flowforge-realm.json`): exported from Keycloak after initial manual setup. Includes clients, roles, default scopes, and token claim mappers. Committed so any developer gets a ready IdP from `docker compose up`.

**Token claims of interest:**

- `sub` — user UUID (stable identity, use as `createdBy` in audit log)
- `preferred_username` — human-readable label for logs
- `realm_access.roles` — list of realm roles (`["admin"]`, `["operator"]`, etc.)

### Files to Create / Modify

- `deploy/docker/compose.yaml` — add `keycloak` service, add `keycloak` DB on `postgres-platform`
- `deploy/docker/keycloak/flowforge-realm.json` — realm export (clients, roles, mappers)

---

## 2. WebApi JWT Bearer Authentication

### Problem

Even after Keycloak is running, the WebApi still accepts all requests without a token. Every endpoint needs a `[Authorize]` gate; token validation must be wired into the ASP.NET Core auth pipeline.

### Design

Use `Microsoft.AspNetCore.Authentication.JwtBearer` (already in the .NET 10 default meta-package). Configure it to validate tokens issued by the Keycloak `flowforge` realm.

**Registration (`Program.cs`):**

```csharp
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Keycloak:Authority"];
        // e.g. "http://keycloak:8080/realms/flowforge"

        options.Audience = builder.Configuration["Keycloak:Audience"];
        // e.g. "flowforge-webapi"

        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ClockSkew                = TimeSpan.FromSeconds(30),
        };
    });

builder.Services.AddAuthorization();
```

**Middleware order (must be in this sequence):**

```csharp
app.UseAuthentication();
app.UseAuthorization();
```

**Global default — require auth everywhere:**

```csharp
builder.Services.AddControllers(options =>
{
    options.Filters.Add(new AuthorizeFilter());
});
```

Webhook fire endpoints must be carved out as `[AllowAnonymous]` because external senders (CI/CD pipelines, external services) authenticate via webhook secret, not a Keycloak token.

**Configuration (`appsettings.json`):**

```json
{
  "Keycloak": {
    "Authority": "http://localhost:8180/realms/flowforge",
    "Audience": "flowforge-webapi"
  }
}
```

**ExceptionHandlingMiddleware additions:**

Map auth failures to correct HTTP status codes:
- `AuthenticationException` → 401
- `UnauthorizedException` (domain, if added) → 403

**Health probes:** `/health/live` and `/health/ready` remain `[AllowAnonymous]` — liveness probes must not require tokens.

### Files to Create / Modify

- `FlowForge.WebApi/FlowForge.WebApi.csproj` — add `Microsoft.AspNetCore.Authentication.JwtBearer` if not already included
- `FlowForge.WebApi/Program.cs` — register auth/authz, apply global `AuthorizeFilter`
- `FlowForge.WebApi/appsettings.json` — add `Keycloak` section
- `FlowForge.WebApi/Controllers/AutomationsController.cs` — add `[AllowAnonymous]` to webhook fire action
- `FlowForge.WebApi/Controllers/TaskTypesController.cs` — add `[AllowAnonymous]` (public discovery)
- `FlowForge.WebApi/Controllers/HealthController.cs` (or program health endpoints) — ensure `[AllowAnonymous]`
- `FlowForge.WebApi/Middleware/ExceptionHandlingMiddleware.cs` — map auth exceptions

---

## 3. Role-Based Authorization (RBAC)

### Problem

Authentication (item #2) only answers "who are you?". Authorization answers "what are you allowed to do?". Without explicit policies, every authenticated user can do everything. The three roles (`admin`, `operator`, `viewer`) must be enforced per-endpoint.

### Design

Keycloak places roles in `realm_access.roles` inside the JWT. The default ASP.NET Core claim mapper does not read this location. A custom claims transformation extracts realm roles and maps them to `ClaimTypes.Role` so `[Authorize(Roles = "admin")]` works out of the box.

**Claims transformation:**

```csharp
public class KeycloakRolesClaimsTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = (ClaimsIdentity)principal.Identity!;
        var realmAccess = principal.FindFirst("realm_access")?.Value;
        if (realmAccess is null) return Task.FromResult(principal);

        var roles = JsonSerializer.Deserialize<RealmAccess>(realmAccess);
        foreach (var role in roles?.Roles ?? [])
            identity.AddClaim(new Claim(ClaimTypes.Role, role));

        return Task.FromResult(principal);
    }
}
```

Registered as a singleton: `services.AddSingleton<IClaimsTransformation, KeycloakRolesClaimsTransformation>()`.

**Policy definitions:**

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly",    p => p.RequireRole("admin"));
    options.AddPolicy("OperatorOrAbove", p => p.RequireRole("admin", "operator"));
    options.AddPolicy("ViewerOrAbove",   p => p.RequireRole("admin", "operator", "viewer"));
});
```

**Endpoint authorization matrix:**

| Endpoint | Required Policy |
|---|---|
| `GET /api/automations` | `ViewerOrAbove` |
| `GET /api/automations/{id}` | `ViewerOrAbove` |
| `POST /api/automations` | `OperatorOrAbove` |
| `PUT /api/automations/{id}` | `OperatorOrAbove` |
| `DELETE /api/automations/{id}` | `AdminOnly` |
| `POST /api/automations/{id}/enable` | `OperatorOrAbove` |
| `POST /api/automations/{id}/disable` | `OperatorOrAbove` |
| `POST /api/automations/{id}/triggers/{name}/webhook` | `[AllowAnonymous]` (secret-based auth) |
| `GET /api/jobs` | `ViewerOrAbove` |
| `POST /api/jobs/{id}/cancel` | `OperatorOrAbove` |
| `GET /api/task-types` | `[AllowAnonymous]` |
| `GET /api/dlq` | `AdminOnly` |
| `POST /api/dlq/{id}/replay` | `AdminOnly` |
| `DELETE /api/dlq/{id}` | `AdminOnly` |

### Files to Create / Modify

- `FlowForge.WebApi/Auth/KeycloakRolesClaimsTransformation.cs` — new
- `FlowForge.WebApi/Program.cs` — register transformation, define policies
- `FlowForge.WebApi/Controllers/AutomationsController.cs` — add `[Authorize(Policy = "...")]` per action
- `FlowForge.WebApi/Controllers/JobsController.cs` — add policy attributes
- `FlowForge.WebApi/Controllers/DlqController.cs` — add `AdminOnly`
- `FlowForge.WebApi/Controllers/TaskTypesController.cs` — add `[AllowAnonymous]`

---

## 4. SignalR Authentication

### Problem

The `JobStatusHub` at `/hubs/job-status` is currently unauthenticated. Any client that knows the URL can subscribe to real-time job updates for any job ID. After applying the global `AuthorizeFilter`, the WebSocket handshake will also require a valid token — but browsers and SignalR clients send the token in the query string (`access_token`), not in the `Authorization` header. This requires specific middleware configuration.

### Design

SignalR clients cannot set custom HTTP headers on a WebSocket upgrade request. The ASP.NET Core JWT bearer middleware has built-in support for reading the token from the query string on hub paths.

**Token extraction from query string:**

```csharp
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // existing config...

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                var path  = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(token) &&
                    path.StartsWithSegments("/hubs"))
                {
                    context.Token = token;
                }
                return Task.CompletedTask;
            }
        };
    });
```

**Hub authorization:**

```csharp
[Authorize]
public class JobStatusHub : Hub
{
    // Existing subscribe/unsubscribe logic unchanged.
    // Hub.Context.UserIdentifier is automatically set to ClaimTypes.NameIdentifier (sub).
}
```

**Map with auth:**

```csharp
app.MapHub<JobStatusHub>("/hubs/job-status").RequireAuthorization();
```

**Frontend/client changes:** The SignalR client must retrieve a Keycloak token and append it to the hub URL: `new HubConnectionBuilder().withUrl("/hubs/job-status?access_token=<token>")`.

### Files to Create / Modify

- `FlowForge.WebApi/Program.cs` — add `OnMessageReceived` token extraction, `RequireAuthorization()` on hub map
- `FlowForge.WebApi/Hubs/JobStatusHub.cs` — add `[Authorize]` attribute

---

## 5. Service-to-Service Auth (M2M Client Credentials)

### Problem

`JobAutomator` makes HTTP calls to `WebApi` (e.g., `GET /api/automations/snapshots` during startup). Once the WebApi requires a token, internal service calls will receive `401`. Internal services are not human users — they authenticate using the OAuth 2.0 **Client Credentials** flow (machine-to-machine, no user involved).

### Design

Each internal service that calls the WebApi gets a Keycloak confidential client with `client_credentials` grant type. The services use `Microsoft.Extensions.Http` with a delegating handler that fetches and caches a client credentials token.

**Dedicated Keycloak clients (from realm config in item #1):**
- `flowforge-jobautomator` — client secret stored in `Keycloak:ClientSecret` env var

**Token cache delegating handler:**

```csharp
public class ClientCredentialsHandler(
    IOptions<KeycloakClientOptions> options,
    IHttpClientFactory factory) : DelegatingHandler
{
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        if (_cachedToken is null || DateTimeOffset.UtcNow >= _tokenExpiry)
            await RefreshTokenAsync(ct);

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", _cachedToken);

        return await base.SendAsync(request, ct);
    }
}
```

**KeycloakClientOptions:**

```csharp
public class KeycloakClientOptions
{
    public const string SectionName = "Keycloak";
    public string Authority    { get; init; } = "";  // realm base URL
    public string ClientId     { get; init; } = "";
    public string ClientSecret { get; init; } = "";
}
```

**Registration in JobAutomator `Program.cs`:**

```csharp
builder.Services.Configure<KeycloakClientOptions>(
    builder.Configuration.GetSection(KeycloakClientOptions.SectionName));

builder.Services.AddTransient<ClientCredentialsHandler>();

builder.Services.AddHttpClient("webapi", client =>
    client.BaseAddress = new Uri(builder.Configuration["WebApi:BaseUrl"]!))
    .AddHttpMessageHandler<ClientCredentialsHandler>();
```

**Scope:** The `flowforge-jobautomator` client needs a service-level scope (e.g., `flowforge:internal`) that bypasses human role checks on internal endpoints like `/api/automations/snapshots`. A separate `[Authorize(Policy = "InternalService")]` policy using audience or scope claim is cleaner than granting the service the `admin` role.

**docker-compose update:** Each service's environment block gains `Keycloak__ClientId` and `Keycloak__ClientSecret`.

### Files to Create / Modify

- `FlowForge.Infrastructure/Auth/KeycloakClientOptions.cs` — new (shared options class)
- `FlowForge.Infrastructure/Auth/ClientCredentialsHandler.cs` — new (delegating handler with cache)
- `FlowForge.Infrastructure/ServiceCollectionExtensions.cs` — add `AddKeycloakClientCredentials()` extension
- `FlowForge.JobAutomator/Program.cs` — call `AddKeycloakClientCredentials()`, wire to HttpClient
- `FlowForge.WebApi/Program.cs` — add `"InternalService"` policy (scope/audience check)
- `FlowForge.WebApi/Controllers/AutomationsController.cs` — apply `InternalService` policy to snapshot endpoint
- `deploy/docker/compose.yaml` — add `Keycloak__*` env vars per service

---

## 6. User Context & Audit Logging

### Problem

Once users are authenticated, there is no record of who created an automation, who disabled it, or who cancelled a job. This is an operational and compliance gap — you cannot answer "who changed this?" after the fact.

### Design

Extract the actor identity from the JWT on every mutating request and persist it alongside the change.

**ICurrentUserService:**

```csharp
public interface ICurrentUserService
{
    string? UserId   { get; }   // JWT "sub" claim
    string? Username { get; }   // JWT "preferred_username" claim
    bool    IsAuthenticated { get; }
}

public class HttpContextCurrentUserService(IHttpContextAccessor accessor)
    : ICurrentUserService
{
    public string? UserId =>
        accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
    public string? Username =>
        accessor.HttpContext?.User.FindFirstValue("preferred_username");
    public bool IsAuthenticated =>
        accessor.HttpContext?.User.Identity?.IsAuthenticated ?? false;
}
```

**AuditLog entity (Platform DB):**

```csharp
public class AuditLog
{
    public Guid   Id         { get; private set; }
    public string Action     { get; private set; }  // "automation.created", "job.cancelled", etc.
    public string? EntityId  { get; private set; }  // automation/job GUID as string
    public string? UserId    { get; private set; }  // JWT sub
    public string? Username  { get; private set; }  // JWT preferred_username
    public string? Detail    { get; private set; }  // JSON blob of relevant fields
    public DateTimeOffset OccurredAt { get; private set; }
}
```

**Audit actions to capture:**

| Action | Trigger |
|---|---|
| `automation.created` | `AutomationService.CreateAsync` |
| `automation.updated` | `AutomationService.UpdateAsync` |
| `automation.deleted` | `AutomationService.DeleteAsync` |
| `automation.enabled` | `AutomationService.EnableAsync` |
| `automation.disabled` | `AutomationService.DisableAsync` |
| `job.cancelled` | `JobService.CancelAsync` |
| `dlq.replayed` | `DlqController` replay action |
| `dlq.deleted` | `DlqController` delete action |

**IAuditLogger interface:**

```csharp
public interface IAuditLogger
{
    Task LogAsync(string action, string? entityId = null,
                  object? detail = null, CancellationToken ct = default);
}
```

`IAuditLogger` resolves `ICurrentUserService` internally and persists to the `AuditLogs` table.

**API endpoint:** `GET /api/audit-logs` — admin only, supports filtering by `entityId` and date range.

**Migration:** `CREATE TABLE "AuditLogs" (...)` in the platform DB.

### Files to Create / Modify

- `FlowForge.Domain/Entities/AuditLog.cs` — new entity
- `FlowForge.Domain/Repositories/IAuditLogRepository.cs` — new
- `FlowForge.Infrastructure/Auth/ICurrentUserService.cs` — new interface
- `FlowForge.Infrastructure/Auth/HttpContextCurrentUserService.cs` — new
- `FlowForge.Infrastructure/Persistence/Platform/PlatformDbContext.cs` — add `AuditLogs` DbSet
- `FlowForge.Infrastructure/Persistence/Platform/Configurations/AuditLogConfiguration.cs` — new
- `FlowForge.Infrastructure/Audit/IAuditLogger.cs` — new
- `FlowForge.Infrastructure/Audit/AuditLogger.cs` — new implementation
- `FlowForge.Infrastructure/ServiceCollectionExtensions.cs` — register `ICurrentUserService`, `IAuditLogger`
- Platform migration — add `AuditLogs` table
- `FlowForge.WebApi/Program.cs` — add `AddHttpContextAccessor()`
- `FlowForge.WebApi/Services/AutomationService.cs` — inject `IAuditLogger`, call `LogAsync` on mutations
- `FlowForge.WebApi/Services/JobService.cs` — call `LogAsync` on cancel
- `FlowForge.WebApi/Controllers/DlqController.cs` — call `LogAsync` on replay/delete
- `FlowForge.WebApi/Controllers/AuditLogsController.cs` — new, `GET /api/audit-logs`

---

## 7. Webhook HMAC-SHA256 Upgrade

### Problem

Webhook secrets are currently hashed with BCrypt and validated per-request. BCrypt is intentionally slow (a CPU-bound hash), which is appropriate for passwords but wrong for webhooks — a high-throughput webhook sender will cause measurable latency and CPU spikes. The industry standard for webhook authentication is HMAC-SHA256: the sender signs the request body with a shared secret; the receiver verifies the signature in microseconds.

### Design

Replace the BCrypt verify step with HMAC-SHA256 signature verification, matching the pattern used by GitHub, Stripe, and most modern webhook APIs.

**Signature scheme:**

- Sender computes: `HMAC-SHA256(secret, requestBody)` → hex string
- Sender sets header: `X-FlowForge-Signature: sha256=<hex>`
- Receiver recomputes the HMAC and compares with `CryptographicOperations.FixedTimeEquals` (constant-time, prevents timing attacks)

**Secret storage:** The raw webhook secret is still stored as a BCrypt hash (to prevent recovery from a database dump). On `CreateAutomation` / `UpdateAutomation`, the caller supplies the plaintext secret; the API stores the BCrypt hash. On webhook receipt, the API **cannot** reverse the hash — instead, the design changes: the raw secret is stored encrypted (using the existing `AesEncryptionService`, marked as a sensitive field in `WebhookTriggerDescriptor`), and BCrypt is dropped entirely.

**Updated `WebhookTriggerDescriptor.GetSensitiveFieldNames()`:**

```csharp
public override IReadOnlyList<string> GetSensitiveFieldNames() => ["secret"];
```

The `secret` field will be encrypted at rest (AES-256-GCM, same as SQL `connectionString`) and decrypted at verification time.

**Verification (in `AutomationService.FireWebhookAsync`):**

```csharp
var secretBytes = Encoding.UTF8.GetBytes(decryptedSecret);
var bodyBytes   = Encoding.UTF8.GetBytes(rawRequestBody);
var computed    = HMACSHA256.HashData(secretBytes, bodyBytes);
var expected    = Convert.FromHexString(signatureHeader["sha256=".Length..]);

if (!CryptographicOperations.FixedTimeEquals(computed, expected))
    throw new UnauthorizedWebhookException();
```

**Migration:** Existing webhooks with BCrypt-hashed secrets are incompatible — operators must rotate secrets after the upgrade. Document this as a breaking change.

**Why drop BCrypt:** BCrypt is a one-way hash; you cannot use it to verify a signature over the request body. There is no way to keep both schemes simultaneously without storing the secret in recoverable form anyway.

### Files to Create / Modify

- `FlowForge.Infrastructure/Triggers/Descriptors/WebhookTriggerDescriptor.cs` — add `secret` to `GetSensitiveFieldNames()`
- `FlowForge.WebApi/Services/AutomationService.cs` — replace BCrypt verify with HMAC-SHA256
- `FlowForge.WebApi/Controllers/AutomationsController.cs` — read raw request body, pass to service
- `FlowForge.WebApi/DTOs/Requests/AutomationRequests.cs` — document secret field as plaintext-on-write only
- `FlowForge.WebApi/DTOs/Responses/Responses.cs` — ensure `secret` is always redacted in responses
- `docs/` — migration guide for operators rotating webhook secrets

---

## 8. Rate Limiting & Throttling

### Problem

The WebApi has no request rate limiting. A misconfigured client, a runaway automation, or a bad actor can flood the API — consuming DB connections, spawning unlimited jobs, and disrupting other users. The webhook endpoint is especially exposed because it is `[AllowAnonymous]`.

### Design

Use ASP.NET Core's built-in `RateLimiter` middleware (available since .NET 7, no extra package required).

**Rate limit policies:**

| Policy | Applies To | Limit |
|---|---|---|
| `webhook` | `POST .../webhook` (anonymous) | 30 requests / minute per IP |
| `authenticated-user` | All authenticated endpoints | 300 requests / minute per `sub` claim |
| `admin` | Admin-only endpoints | No limit (internal operators) |

**Registration:**

```csharp
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("webhook", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit      = 30,
                Window           = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit       = 0,
            }));

    options.AddPolicy("authenticated-user", context =>
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(userId, _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window      = TimeSpan.FromMinutes(1),
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
```

**Apply on webhook endpoint:**

```csharp
// In AutomationsController
[AllowAnonymous]
[EnableRateLimiting("webhook")]
public async Task<IActionResult> FireWebhook(...) { ... }
```

**Apply globally for authenticated endpoints:**

```csharp
app.UseRateLimiter();
// In controller base or via global filter
```

**DLQ replay endpoint:** also rate limited under `authenticated-user` — replaying DLQ entries is a fan-out operation that can spawn many jobs.

### Files to Create / Modify

- `FlowForge.WebApi/Program.cs` — register and apply `AddRateLimiter`
- `FlowForge.WebApi/Controllers/AutomationsController.cs` — add `[EnableRateLimiting("webhook")]`
- `FlowForge.WebApi/Middleware/ExceptionHandlingMiddleware.cs` — ensure 429 is handled cleanly

---

## 9. HTTPS in Development

### Problem

All services run over plain HTTP in the local docker-compose stack. In production, TLS is terminated at the ingress layer, but local dev has no HTTPS at all. This means developers cannot test redirect-to-HTTPS behavior, HSTS headers, or TLS-only cookie settings — and Keycloak's `RequireHttpsMetadata = false` workaround needed in item #2 becomes a permanent crutch.

### Design

Add a local TLS termination proxy (Caddy) to the docker-compose stack. Caddy auto-generates self-signed certs for local domains and reverse-proxies to each service.

**Local domain convention:** `*.flowforge.local` resolved via `/etc/hosts` (or the Caddy `host` directive with a wildcard).

**Caddyfile (`deploy/docker/Caddyfile`):**

```caddy
api.flowforge.local {
    tls internal
    reverse_proxy webapi:8080
}

auth.flowforge.local {
    tls internal
    reverse_proxy keycloak:8080
}
```

**docker-compose addition:**

```yaml
caddy:
  image: caddy:2
  ports:
    - "80:80"
    - "443:443"
  volumes:
    - ./Caddyfile:/etc/caddy/Caddyfile
    - caddy_data:/data
  depends_on:
    - webapi
    - keycloak
```

**HSTS and security headers (in Caddyfile):**

```caddy
api.flowforge.local {
    tls internal
    header Strict-Transport-Security "max-age=31536000; includeSubDomains"
    header X-Content-Type-Options nosniff
    header X-Frame-Options DENY
    header Referrer-Policy strict-origin-when-cross-origin
    reverse_proxy webapi:8080
}
```

**WebApi changes:** Once behind the proxy, set `RequireHttpsMetadata = true` (it sees internal HTTP from Caddy). The WebApi must trust the `X-Forwarded-*` headers Caddy adds:

```csharp
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});
```

**Developer setup:** Run `caddy trust` once to add Caddy's local CA to the system trust store (eliminates browser cert warnings).

### Files to Create / Modify

- `deploy/docker/Caddyfile` — new
- `deploy/docker/compose.yaml` — add `caddy` service and `caddy_data` volume
- `FlowForge.WebApi/Program.cs` — add `UseForwardedHeaders`, set `RequireHttpsMetadata = true` outside dev
- `FlowForge.WebApi/appsettings.json` — update `Keycloak:Authority` to use `https://auth.flowforge.local/realms/flowforge`
- `docs/` — developer setup section: `/etc/hosts` entries, `caddy trust` command

---

## 10. Security Test Suite

### Problem

Security controls without test coverage are controls that will silently break. A future refactor that accidentally removes `[Authorize]` or a misconfigured policy will not be caught until it ships.

### Design

Add a dedicated `FlowForge.Security.Tests` project (integration tests, using `WebApplicationFactory<Program>`). This project does not use Testcontainers for the auth layer — instead, it configures the WebApi's JWT middleware to accept tokens signed by a test RSA key, bypassing Keycloak entirely. The test RSA key is generated in the test fixture and its JWKS endpoint is mocked.

**Test categories:**

1. **AuthN tests** — verify that each protected endpoint returns `401` with no token and `401` with an expired/invalid token.
2. **AuthZ / RBAC tests** — verify that endpoints return `403` when an authenticated user lacks the required role (e.g., a `viewer` attempting `DELETE /api/automations/{id}`).
3. **Rate limit tests** — verify that the webhook endpoint returns `429` after exceeding the limit.
4. **Audit log tests** — verify that `AuditLogs` records are created for mutating operations.
5. **Webhook HMAC tests** — verify that webhooks with a wrong or missing signature return `401`, and a correctly signed request succeeds.

**Test token factory:**

```csharp
public static class TestTokenFactory
{
    private static readonly RsaSecurityKey _key = new(RSA.Create(2048));

    public static string CreateToken(string userId = "test-user",
                                     string[] roles = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new("preferred_username", userId),
            new("realm_access",
                JsonSerializer.Serialize(new { roles = roles ?? [] })),
        };
        var credentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            issuer   : "test-issuer",
            audience : "flowforge-webapi",
            claims   : claims,
            expires  : DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
```

**WebApplicationFactory override:** Replace the real Keycloak authority with the test key's JWKS in `TestWebAppFactory.ConfigureTestServices`.

**Coverage targets:**

| Test | Scenarios |
|---|---|
| Anonymous → protected endpoint | `401` for all protected routes |
| Expired token → protected endpoint | `401` |
| `viewer` → write endpoint | `403` |
| `operator` → `AdminOnly` endpoint | `403` |
| `admin` → any endpoint | `200` / `2xx` |
| Correct HMAC signature | Webhook fires |
| Wrong HMAC signature | `401` |
| Missing signature header | `401` |
| Webhook > rate limit | `429` |
| Audit log written on `POST /api/automations` | Row in `AuditLogs` |

### Files to Create / Modify

- `tests/FlowForge.Security.Tests/FlowForge.Security.Tests.csproj` — new project
- `FlowForge.sln` — add new test project
- `tests/FlowForge.Security.Tests/TestTokenFactory.cs` — new
- `tests/FlowForge.Security.Tests/TestWebAppFactory.cs` — new
- `tests/FlowForge.Security.Tests/AuthN/UnauthenticatedAccessTests.cs` — new
- `tests/FlowForge.Security.Tests/AuthZ/RbacTests.cs` — new
- `tests/FlowForge.Security.Tests/Webhook/WebhookSignatureTests.cs` — new
- `tests/FlowForge.Security.Tests/RateLimit/WebhookRateLimitTests.cs` — new
- `tests/FlowForge.Security.Tests/Audit/AuditLogTests.cs` — new

---

## Implementation Order

The items have the following hard dependencies:

```
#1 (Keycloak Setup)   ──► #2 (JWT Auth)  ──► #3 (RBAC)
                                          ──► #4 (SignalR Auth)
                                          ──► #5 (M2M Auth)
#3 + #5               ──► #6 (Audit Log)
#2                    ──► #7 (Webhook HMAC)   [auth context needed for redaction]
#2 + #3               ──► #8 (Rate Limiting)  [user identity used for partition key]
#1 + #2               ──► #9 (HTTPS Dev)
#2 + #3 + #6 + #7 + #8 ──► #10 (Security Tests)
```

Items #1 → #2 → #3 form the critical path. Everything else branches from there.

---

## Phase 2: Messaging Abstraction & Dapr/Kafka Migration

This tracks the refactoring from direct Redis Streams to a provider-agnostic messaging layer using Dapr sidecars and Apache Kafka.

| # | Title | Area | Status |
|---|---|---|---|
| 11 | [Generic Messaging Abstractions](#11-generic-messaging-abstractions) | Architecture | `[x]` |
| 12 | [Event Handler Extraction](#12-event-handler-extraction) | Architecture | `[x]` |
| 13 | [Outbox Relay Generalization](#13-outbox-relay-generalization) | Messaging | `[x]` |
| 14 | [Dapr Provider Implementation](#14-dapr-provider-implementation) | Messaging | `[x]` |
| 15 | [Provider Registration & Switching](#15-provider-registration--switching) | DI/Config | `[x]` |
| 16 | [Service Program.cs Updates](#16-service-programcs-updates) | Services | `[x]` |
| 17 | [Docker & Kafka Infrastructure](#17-docker--kafka-infrastructure) | Infrastructure | `[x]` |
| 18 | [Per-Host Routing with Dapr](#18-per-host-routing-with-dapr) | Messaging | `[x]` |
| 19 | [DLQ Controller Dual-Provider](#19-dlq-controller-dual-provider) | Messaging | `[x]` |
| 20 | [Testing (All Tests Green)](#20-testing-all-tests-green) | Testing | `[x]` |
| 21 | [Documentation](#21-documentation) | Documentation | `[x]` |

---

### 11. Generic Messaging Abstractions

Created the provider-agnostic messaging surface area: `TopicNames.cs` centralises all stream/topic name constants, `IEventHandler<TEvent>` defines a uniform contract for processing inbound events, `IMessagingInfrastructure` abstracts consumer lifecycle management, and `MessagingOptions` exposes the `Messaging:Provider` configuration key. These abstractions live in the shared Infrastructure layer so every service can depend on them without coupling to a specific transport.

---

### 12. Event Handler Extraction

Extracted 7 event handlers that were previously embedded inside `BackgroundService` workers into standalone `IEventHandler<T>` classes. Each handler is a focused, testable unit that receives a strongly-typed event and returns a `Task`. This decoupled business logic from transport concerns, enabling the same handlers to be invoked by either the Redis consumer loop or the Dapr subscription endpoints.

---

### 13. Outbox Relay Generalization

Changed `OutboxRelayWorker` from directly calling `IConnectionMultiplexer` (Redis) to publishing through the `IMessagePublisher` abstraction. The relay still polls the `OutboxMessage` table on its 500 ms interval, but the actual publish call is now delegated to whichever provider is registered at startup. This was the single most impactful change for transport independence.

---

### 14. Dapr Provider Implementation

Added the `Dapr.AspNetCore` NuGet package and implemented three new types: `DaprMessagePublisher` (publishes events via the Dapr pub/sub building block), `DaprDlqWriter` (routes failed messages to the DLQ topic through Dapr), and `DaprSubscriptionEndpoints` (registers programmatic Dapr subscription routes that map topics to the extracted `IEventHandler<T>` classes). Together these form the complete Dapr provider.

---

### 15. Provider Registration & Switching

Split the former `AddRedis()` extension method into two independent methods: `AddRedisCaching()` (retains Redis as the distributed cache) and `AddMessaging()` (registers the messaging provider). The `AddMessaging()` method reads `Messaging:Provider` from configuration and registers either the Redis Streams or Dapr implementation. Switching providers requires only a config change — no code modifications.

---

### 16. Service Program.cs Updates

Updated all four service `Program.cs` files (WebApi, JobAutomator, JobOrchestrator, WorkflowHost) with conditional worker and endpoint registration. When the Dapr provider is selected, `BackgroundService` consumer workers are not registered and Dapr subscription endpoints are mapped instead. When Redis is selected, the original workers run as before. This keeps both paths exercised and deployable.

---

### 17. Docker & Kafka Infrastructure

Created a `compose.dapr.yaml` docker-compose override that adds Apache Kafka in KRaft mode (no ZooKeeper), Dapr sidecars for each service, and the necessary Dapr component YAML files for pub/sub and state store configuration. Running `docker compose -f compose.yaml -f compose.dapr.yaml up` starts the full Dapr/Kafka stack alongside the existing PostgreSQL and Redis infrastructure.

---

### 18. Per-Host Routing with Dapr

Ensured that per-host topics (e.g., `host-{hostName}`) work identically in both Redis Streams and Dapr/Kafka modes. In Redis the pattern maps to a dedicated stream key; in Dapr it maps to a Kafka topic with the same name. The `TopicNames.ForHost(hostName)` helper generates the correct topic name, and both providers route messages to the correct WorkflowHost instance transparently.

---

### 19. DLQ Controller Dual-Provider

Introduced an `IDlqReader` abstraction and refactored `DlqController` to be fully provider-agnostic. The controller no longer references Redis commands directly — it reads dead-letter messages through `IDlqReader`, which has both a Redis Streams implementation (reading from `flowforge:dlq`) and a Dapr implementation (reading from the Kafka DLQ topic). Replay and purge operations go through the same abstraction.

---

### 20. Testing (All Tests Green)

Verified all 235 tests pass across the three test projects: 124 domain unit tests, 68 integration tests (Testcontainers), and 43 security tests. The extracted event handlers are covered by the existing domain and integration suites. No test changes were required beyond updating DI registrations in test fixtures to use the new `AddMessaging()` extension.

---

### 21. Documentation

Updated `ROADMAP.md` with this Phase 2 section, revised `AGENTS.md` to describe the dual-provider architecture, added messaging provider conventions to `CONVENTIONS.md`, and updated `CLAUDE.md` to reflect the new build and configuration options. All documentation now accurately describes both the Redis Streams fallback path and the primary Dapr/Kafka path.

---

## Phase 2 — Implementation Order

The items have the following hard dependencies:

```
#11 (Abstractions)     ──► #12 (Handler Extraction)  ──► #13 (Outbox Relay)
#11                    ──► #14 (Dapr Provider)
#12 + #13 + #14        ──► #15 (Provider Registration)
#15                    ──► #16 (Service Program.cs)
#15                    ──► #17 (Docker & Kafka Infra)
#16 + #17              ──► #18 (Per-Host Routing)
#15                    ──► #19 (DLQ Dual-Provider)
#16 + #17 + #18 + #19  ──► #20 (Testing)
#20                    ──► #21 (Documentation)
```

Items #11 → #12 → #13 and #11 → #14 form two parallel critical paths that converge at #15.
