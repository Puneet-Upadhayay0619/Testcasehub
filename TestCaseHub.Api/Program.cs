using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using TestCaseHub.Api.Data;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

var builder = WebApplication.CreateBuilder(args);

// Render (and most PaaS hosts) hand the app a PORT env var and expect it to bind there on
// 0.0.0.0 -- ASP.NET Core has no built-in awareness of that convention, so without this the
// container starts fine but Render's load balancer can never actually reach it. Harmless
// locally/in the sandbox: PORT is simply unset there, so this block does nothing.
var renderPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(renderPort))
    builder.WebHost.UseUrls($"http://0.0.0.0:{renderPort}");

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Storage:Mode = "JsonFile" (no database needed at all — everything in one JSON file, good
// for small teams / getting started fast) or "SqlServer" (real database, for when you outgrow
// a single flat file — see README for the tradeoffs).
var storageMode = builder.Configuration["Storage:Mode"] ?? "JsonFile";
var dbProvider = builder.Configuration["Database:Provider"] ?? "Sqlite";

if (storageMode == "SqlServer")
{
    // "SqlServer" is the historical name for this Storage:Mode value (kept as-is so existing
    // deployments don't need an appsettings change) -- it really means "use a real relational
    // database via EF Core", and Database:Provider picks which one: SqlServer, Sqlite, or
    // Postgres (Supabase, Phase 7 -- ConnectionStrings:Postgres from the Supabase project's
    // connection string, e.g. "Host=...;Database=postgres;Username=...;Password=...").
    builder.Services.AddDbContext<AppDbContext>(options =>
    {
        if (dbProvider == "SqlServer")
            options.UseSqlServer(builder.Configuration.GetConnectionString("SqlServer"));
        else if (dbProvider == "Postgres")
            options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"));
        else
            options.UseSqlite(builder.Configuration.GetConnectionString("Sqlite") ?? "Data Source=testcasehub.db");
    });
    builder.Services.AddScoped<IDataStore, EfCoreDataStore>();
}
else
{
    // JsonFileDataStore keeps all data in memory + one JSON file, guarded by an internal lock,
    // so it must be a SINGLETON (one shared instance/lock for the whole app), not per-request.
    builder.Services.AddSingleton<IDataStore, JsonFileDataStore>();
}

builder.Services.AddScoped<JwtService>();
builder.Services.AddScoped<RefreshTokenService>();
builder.Services.AddScoped<TestCaseHub.Api.Services.NotificationService>();
builder.Services.AddScoped<TestCaseHub.Api.Services.ApiKeyService>();
builder.Services.AddHttpClient<TestCaseHub.Api.Services.AdoService>();
builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();
builder.Services.AddDataProtection();
builder.Services.AddSingleton<SecretProtector>();

// Rate limiting (Phase 3): partitioned per client IP so one abusive caller can't exhaust the
// budget for everyone else. 5 attempts / 15 minutes on the auth-sensitive endpoints
// (login, OAuth token exchange) — applied via [EnableRateLimiting("auth")] on those actions.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", httpContext => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(15),
            PermitLimit = 5,
            QueueLimit = 0
        }));
});

// MCP server: exposes the exact same IDataStore-backed operations as the REST controllers,
// as MCP tools, so a Cowork artifact (or Claude directly, via a custom connector) can drive
// the tool without a separate REST client. Protected by the same JWT bearer scheme as the
// REST API (see app.MapMcp(...).RequireAuthorization() below) so identity/attribution still
// works — each teammate adds this server as a connector with their own token.
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var jwtSection = builder.Configuration.GetSection("Jwt");
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    // Without this, the JWT handler silently rewrites well-known-sounding short claim types
    // (notably "role" -> the long ClaimTypes.Role URI) to .NET's legacy claim URIs before
    // ClaimsPrincipal ever sees them. Permissions.cs (and everything else in this app) reads
    // claims by the exact short name we put in the token (JwtService.cs) — "role", not the
    // remapped URI — so this must be off or role-based checks silently fall back to Viewer for
    // EVERY token, which is exactly the bug this line fixes.
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtSection["Issuer"],
        ValidAudience = jwtSection["Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSection["Key"]!))
    };
    // On a 401 from /mcp, point MCP clients (Claude, etc.) at the OAuth discovery document so
    // they can register + run the login flow automatically instead of needing a manually
    // configured token — this is the standard RFC 9728 "protected resource metadata" handshake.
    options.Events = new JwtBearerEvents
    {
        OnChallenge = ctx =>
        {
            // HandleResponse() stops the default handler from appending its own bare "Bearer"
            // WWW-Authenticate value after ours — we want exactly one, clean header here.
            ctx.HandleResponse();
            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            ctx.Response.StatusCode = 401;
            ctx.Response.Headers["WWW-Authenticate"] =
                $"Bearer resource_metadata=\"{baseUrl}/.well-known/oauth-protected-resource\"";
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddSingleton<TestCaseHub.Api.Services.OAuthStore>();
builder.Services.AddAuthorization();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        // Tighten this to your actual frontend origin(s) before going to production.
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

var app = builder.Build();

// Tunnels/reverse proxies (cloudflared, a real load balancer, etc.) terminate TLS and forward
// plain HTTP to this app on localhost — without this, Request.Scheme always reports "http" even
// though the outside world is on https, which would make the OAuth metadata below advertise the
// wrong scheme (https://... tunnel host over a bare http:// URL) and break Claude's login flow.
// The default KnownNetworks/KnownProxies (loopback only) are correct here since the proxy hop is
// always cloudflared -> this app on the same machine.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// API versioning (Phase 7): every existing route also answers under /api/v1/... so future
// breaking changes have somewhere to go (/api/v2/...) without pulling the rug out from under
// whatever's already calling /api/... today (the CI service-account key, the web app, MCP
// tools that hit REST directly, etc.) -- a rewrite, not a duplicate route table to maintain.
app.Use((context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1", out var remainder))
        context.Request.Path = "/api" + remainder;
    return next();
});

// IMPORTANT: without this explicit call, ASP.NET Core implicitly inserts routing at the very
// START of the pipeline (before even the rewrite middleware above), which would match/404 on
// the ORIGINAL /api/v1/... path before it's ever rewritten. Calling it explicitly here, after
// the rewrite, makes routing see the already-rewritten path instead.
app.UseRouting();

// Serves the Test Case Hub frontend (wwwroot/index.html) from this same server/origin,
// so one URL gives you the whole app (login screen -> the tool) with no separate
// "API server URL" step needed — the frontend auto-detects same-origin deployment.
app.UseDefaultFiles();
app.UseStaticFiles();

if (storageMode == "SqlServer")
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // Real SQL Server: apply the generated migrations (Migrations/ folder), which were
    // authored/type-checked against the SqlServer provider. Sqlite and Postgres both use
    // EnsureCreated() instead -- NOT because it's "less real", but because EF Core migration
    // FILES embed provider-specific column types (nvarchar, datetime2, bit, ...) that don't
    // translate across providers. For a brand-new Postgres/Supabase database, EnsureCreated()
    // builds the exact current schema straight from the C# model with correctly-Postgres-typed
    // columns, sidestepping that mismatch entirely. If the schema needs to evolve again AFTER
    // Postgres is live, generate a fresh, Postgres-specific migration set at that point
    // (Database:Provider=Postgres when running `dotnet ef migrations add`).
    if (dbProvider == "SqlServer")
        db.Database.Migrate();
    else
        db.Database.EnsureCreated();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();

// Streamable-HTTP MCP endpoint, gated behind the same JWT bearer auth as the REST API.
app.MapMcp("/mcp").RequireAuthorization();

// Keep-alive / cold-start target (Phase 7): a scheduled ping to THIS endpoint (e.g. a Cowork
// scheduled task, or any external uptime pinger) keeps Render from sleeping after 15 minutes
// of inactivity, and touches the DB so Supabase's 7-day-inactivity auto-pause never triggers
// either -- both were separate, previously-identified risks, both covered by one cheap ping.
app.MapGet("/health", async (IDataStore store) =>
{
    var moduleCount = (await store.GetModulesAsync()).Count;
    return Results.Ok(new { status = "ok", utc = DateTime.UtcNow, moduleCount });
}).AllowAnonymous();

app.Run();
