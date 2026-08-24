using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.IdentityModel.Tokens;
using TestCaseHub.Api.Data;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

// Fix for a real production crash: "The configured user limit (128) on the number of inotify
// instances has been reached" during WebApplication.CreateBuilder(args). ASP.NET Core's default
// config sources (appsettings.json / appsettings.{Environment}.json) are added with
// reloadOnChange:true, which sets up a FileSystemWatcher (one inotify instance) per file. On a
// constrained/shared host like Render's free tier, that watcher setup can fail outright once the
// container-wide inotify instance limit is exhausted -- and since this happens inside
// CreateBuilder itself, it's an unhandled exception that kills the whole process before it can
// even start listening (explaining the repeated "Instance failed: exited with status 139"
// crash-loop). Production has no need to live-reload appsettings.json anyway, so disabling this
// removes our app's only use of file-watching entirely. Must be set before CreateBuilder runs --
// setting it here (in-process) has the same effect as setting the environment variable
// DOTNET_hostBuilder__reloadConfigOnChange=false in Render's dashboard, without depending on
// someone remembering to configure that separately.
Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");

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
builder.Services.AddHttpClient<TestCaseHub.Api.Services.RepoContentService>();
builder.Services.AddHttpClient<TestCaseHub.Api.Services.AnthropicClient>();
builder.Services.AddHttpClient<TestCaseHub.Api.Services.ScriptExecutionService>();
builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();
builder.Services.AddDataProtection();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddScoped<TestCaseHub.Api.Services.AutomationGenerationService>();

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
    else if (dbProvider == "Postgres")
    {
        // EnsureCreated() is a no-op the moment the database itself already exists -- and on
        // Supabase it always does (the project ships with its own auth/storage/etc. schemas
        // already present), so EnsureCreated()'s "does the DB exist" check returns true and it
        // silently skips creating our tables entirely. CreateTables() instead builds our schema
        // unconditionally; the 42P07 (duplicate_table) catch makes this safe to run again on
        // every redeploy once the tables already exist.
        try
        {
            db.GetService<IRelationalDatabaseCreator>().CreateTables();
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P07")
        {
            // Tables already exist from a previous deploy -- nothing to do.
        }

        // Phase 8 (multi-company/teams) schema delta. CreateTables() above only helps a
        // BRAND-NEW database -- on an already-populated one (our real Supabase DB) it throws
        // 42P07 on the very first table and the catch above skips the WHOLE call, so none of
        // these genuinely-new tables/columns ever get created that way. Every statement here
        // is written to be safe to run on EVERY startup, on EITHER a brand-new DB (where
        // everything already exists from CreateTables() above, so this is all no-ops) or an
        // existing one (where this IS what adds the new tables/columns) -- IF NOT EXISTS /
        // ADD COLUMN IF NOT EXISTS everywhere, no exception handling needed because Postgres
        // itself treats these as idempotent.
        var deltaSql = new[]
        {
            // New tables.
            @"CREATE TABLE IF NOT EXISTS ""Companies"" (
                ""Id"" serial PRIMARY KEY,
                ""Name"" varchar(256) NOT NULL,
                ""Status"" varchar(32) NOT NULL DEFAULT 'Active',
                ""CreatedBy"" varchar(256) NOT NULL DEFAULT '',
                ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT now()
            );",
            @"CREATE TABLE IF NOT EXISTS ""CompanyAdminInvites"" (
                ""Id"" serial PRIMARY KEY,
                ""CompanyId"" integer NOT NULL,
                ""Code"" varchar(64) NOT NULL,
                ""MaxUses"" integer NOT NULL DEFAULT 1,
                ""UsedCount"" integer NOT NULL DEFAULT 0,
                ""ExpiresAt"" timestamp with time zone NOT NULL,
                ""Revoked"" boolean NOT NULL DEFAULT false,
                ""CreatedByEmail"" varchar(256) NOT NULL DEFAULT '',
                ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT now()
            );",
            @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_CompanyAdminInvites_Code"" ON ""CompanyAdminInvites"" (""Code"");",
            @"CREATE TABLE IF NOT EXISTS ""Teams"" (
                ""Id"" serial PRIMARY KEY,
                ""CompanyId"" integer NOT NULL,
                ""Name"" varchar(128) NOT NULL,
                ""Description"" text NOT NULL DEFAULT '',
                ""CreatedBy"" varchar(256) NOT NULL DEFAULT '',
                ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT now()
            );",
            @"CREATE TABLE IF NOT EXISTS ""TeamMembers"" (
                ""Id"" serial PRIMARY KEY,
                ""TeamId"" integer NOT NULL,
                ""UserId"" integer NOT NULL,
                ""AddedAt"" timestamp with time zone NOT NULL DEFAULT now()
            );",
            @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_TeamMembers_TeamId_UserId"" ON ""TeamMembers"" (""TeamId"",""UserId"");",
            @"CREATE TABLE IF NOT EXISTS ""TeamModules"" (
                ""Id"" serial PRIMARY KEY,
                ""TeamId"" integer NOT NULL,
                ""ModuleId"" integer NOT NULL,
                ""AssignedAt"" timestamp with time zone NOT NULL DEFAULT now()
            );",
            @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_TeamModules_TeamId_ModuleId"" ON ""TeamModules"" (""TeamId"",""ModuleId"");",

            // New columns on existing tables. Non-nullable ones use DEFAULT 0 -- Postgres 11+
            // applies that default to every EXISTING row as a fast metadata-only change, no
            // full table rewrite, so this is safe to run against a live, populated table.
            // 0 doubles as our "not yet assigned to a company" backfill sentinel (handled by
            // the seeding step right after this block).
            @"ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""CompanyId"" integer NULL;",
            @"ALTER TABLE ""Modules"" ADD COLUMN IF NOT EXISTS ""CompanyId"" integer NOT NULL DEFAULT 0;",
            @"ALTER TABLE ""TestCases"" ADD COLUMN IF NOT EXISTS ""TeamId"" integer NULL;",
            @"ALTER TABLE ""TestSuites"" ADD COLUMN IF NOT EXISTS ""CompanyId"" integer NOT NULL DEFAULT 0;",
            @"ALTER TABLE ""Releases"" ADD COLUMN IF NOT EXISTS ""CompanyId"" integer NOT NULL DEFAULT 0;",
            @"ALTER TABLE ""TestRuns"" ADD COLUMN IF NOT EXISTS ""CompanyId"" integer NOT NULL DEFAULT 0;",
            @"ALTER TABLE ""ApiKeys"" ADD COLUMN IF NOT EXISTS ""CompanyId"" integer NOT NULL DEFAULT 0;",
            @"ALTER TABLE ""EnvironmentTargets"" ADD COLUMN IF NOT EXISTS ""CompanyId"" integer NOT NULL DEFAULT 0;",
            @"ALTER TABLE ""InviteLinks"" ADD COLUMN IF NOT EXISTS ""CompanyId"" integer NOT NULL DEFAULT 0;",
            @"ALTER TABLE ""AuditLogs"" ADD COLUMN IF NOT EXISTS ""CompanyId"" integer NULL;",

            // Module.Code used to be globally unique; now only unique WITHIN a company.
            @"DROP INDEX IF EXISTS ""IX_Modules_Code"";",
            @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Modules_CompanyId_Code"" ON ""Modules"" (""CompanyId"",""Code"");",

            // Automation-generation architecture (agreed in planning): per-module repo links,
            // per-environment named execution credentials, and the AI-generated scripts
            // themselves -- all stored here in Test Case Hub's own database, never pushed to a
            // company's own repo. Same idempotent CREATE TABLE IF NOT EXISTS convention as the
            // Phase 8 tables above -- safe on every startup, on a brand-new or already-populated
            // Postgres DB alike.
            @"CREATE TABLE IF NOT EXISTS ""ModuleRepoLinks"" (
                ""Id"" serial PRIMARY KEY,
                ""CompanyId"" integer NOT NULL DEFAULT 0,
                ""ModuleId"" integer NOT NULL,
                ""RepoHost"" varchar(16) NOT NULL DEFAULT 'GitHub',
                ""Layer"" varchar(16) NOT NULL DEFAULT 'Unspecified',
                ""OrgOrAccount"" varchar(128) NOT NULL DEFAULT '',
                ""Project"" varchar(128) NOT NULL DEFAULT '',
                ""RepoName"" varchar(128) NOT NULL DEFAULT '',
                ""Branch"" varchar(128) NOT NULL DEFAULT 'main',
                ""BasePath"" varchar(256) NOT NULL DEFAULT '',
                ""AccessTokenEncrypted"" text NOT NULL DEFAULT '',
                ""CreatedBy"" varchar(256) NOT NULL DEFAULT '',
                ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT now(),
                ""UpdatedBy"" varchar(256) NOT NULL DEFAULT '',
                ""UpdatedAt"" timestamp with time zone NULL
            );",
            @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ModuleRepoLinks_ModuleId_Layer"" ON ""ModuleRepoLinks"" (""ModuleId"",""Layer"");",

            @"CREATE TABLE IF NOT EXISTS ""EnvironmentCredentials"" (
                ""Id"" serial PRIMARY KEY,
                ""EnvironmentTargetId"" integer NOT NULL,
                ""Label"" varchar(128) NOT NULL DEFAULT '',
                ""Email"" varchar(256) NOT NULL DEFAULT '',
                ""PasswordEncrypted"" text NOT NULL DEFAULT '',
                ""Tag"" varchar(128) NOT NULL DEFAULT '',
                ""CreatedBy"" varchar(256) NOT NULL DEFAULT '',
                ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT now(),
                ""UpdatedBy"" varchar(256) NOT NULL DEFAULT '',
                ""UpdatedAt"" timestamp with time zone NULL
            );",
            @"CREATE INDEX IF NOT EXISTS ""IX_EnvironmentCredentials_EnvironmentTargetId"" ON ""EnvironmentCredentials"" (""EnvironmentTargetId"");",

            // Company's own Anthropic API key (second AI-generation path, agreed alongside the
            // existing MCP-based one).
            @"CREATE TABLE IF NOT EXISTS ""CompanyAiSettings"" (
                ""Id"" serial PRIMARY KEY,
                ""CompanyId"" integer NOT NULL,
                ""Provider"" varchar(32) NOT NULL DEFAULT 'Anthropic',
                ""Model"" varchar(64) NOT NULL DEFAULT 'claude-sonnet-5',
                ""ApiKeyEncrypted"" text NOT NULL DEFAULT '',
                ""Enabled"" boolean NOT NULL DEFAULT true,
                ""CreatedBy"" varchar(256) NOT NULL DEFAULT '',
                ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT now(),
                ""UpdatedBy"" varchar(256) NOT NULL DEFAULT '',
                ""UpdatedAt"" timestamp with time zone NULL
            );",
            @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_CompanyAiSettings_CompanyId"" ON ""CompanyAiSettings"" (""CompanyId"");",

            // Test Run -> named execution credential (agreed in planning: Lead can trigger a run
            // using a stored credential by Label, never seeing the password).
            @"ALTER TABLE ""TestRuns"" ADD COLUMN IF NOT EXISTS ""EnvironmentCredentialId"" integer NULL;",

            @"CREATE TABLE IF NOT EXISTS ""AutomationScripts"" (
                ""Id"" serial PRIMARY KEY,
                ""CompanyId"" integer NOT NULL DEFAULT 0,
                ""ModuleId"" integer NOT NULL,
                ""TestCaseId"" varchar(64) NULL,
                ""SuiteId"" integer NULL,
                ""FileName"" varchar(256) NOT NULL DEFAULT '',
                ""Framework"" varchar(64) NOT NULL DEFAULT '',
                ""Content"" text NOT NULL DEFAULT '',
                ""Status"" varchar(16) NOT NULL DEFAULT 'Draft',
                ""GeneratedBy"" varchar(128) NOT NULL DEFAULT '',
                ""GeneratedAt"" timestamp with time zone NOT NULL DEFAULT now(),
                ""Version"" integer NOT NULL DEFAULT 1,
                ""SourceRepoRefs"" varchar(128) NOT NULL DEFAULT ''
            );",

            // Native execution DSL (Phase 10 -- "test case hub se hi complete testing", no
            // Node/Playwright subprocess). Nullable text; empty/null = not wired for native run.
            @"ALTER TABLE ""AutomationScripts"" ADD COLUMN IF NOT EXISTS ""ExecutionDefinitionJson"" text NULL;",

            @"ALTER TABLE ""EnvironmentTargets"" ADD COLUMN IF NOT EXISTS ""TestCompanyId"" integer NULL;",
            @"ALTER TABLE ""EnvironmentTargets"" ADD COLUMN IF NOT EXISTS ""TestCompanyBId"" integer NULL;",
            @"ALTER TABLE ""EnvironmentTargets"" ADD COLUMN IF NOT EXISTS ""TestReservedModuleEnum"" integer NULL;",
        };
        foreach (var stmt in deltaSql)
            await db.Database.ExecuteSqlRawAsync(stmt);
    }
    else
        db.Database.EnsureCreated();
}

// Phase 8 seed/backfill -- runs on EVERY startup, for EVERY storage mode (JsonFile, Sqlite,
// Postgres, SqlServer all go through IDataStore identically here), and is safe to re-run:
//   1) Make sure at least one Company exists ("Default Company") so pre-Phase-8 data has
//      somewhere to land the first time this code ever runs.
//   2) Backfill anything created before Phase 8 existed (CompanyId is null, or 0 -- the "not
//      yet assigned" sentinel used for the non-nullable columns) into that Default Company,
//      and put all of it into one "Default Team" so nobody who could already see a module
//      loses that access the moment this code first runs.
//   3) (REMOVED -- this used to force-promote puneet@flick2know.com back to SuperAdmin on
//      EVERY startup, which silently reverted any deliberate role change made afterwards --
//      including via the Manage Users screen -- the next time the app restarted/redeployed.
//      That's exactly the bug reported: a role change "kept undoing itself". The one-time
//      bootstrap rule (first-ever registered user on a brand-new deployment becomes SuperAdmin)
//      already lives in AuthController.Register and is not affected by this removal -- it only
//      fires once, when the very first account is created, never again afterwards.
using (var seedScope = app.Services.CreateScope())
{
    var seedStore = seedScope.ServiceProvider.GetRequiredService<IDataStore>();

    var companies = await seedStore.GetCompaniesAsync();
    var defaultCompany = companies.FirstOrDefault(c => c.Name == "Default Company");
    if (defaultCompany is null)
        defaultCompany = await seedStore.CreateCompanyAsync(new TestCaseHub.Api.Models.Company { Name = "Default Company", CreatedBy = "system" });

    var allUsers = await seedStore.GetUsersAsync();
    foreach (var u in allUsers.Where(u => u.CompanyId is null && u.Role != TestCaseHub.Api.Models.Roles.SuperAdmin))
    {
        u.CompanyId = defaultCompany.Id;
        await seedStore.UpdateUserAsync(u);
    }

    var allModules = await seedStore.GetModulesAsync();
    var backfilledModules = allModules.Where(m => m.CompanyId == 0).ToList();
    foreach (var m in backfilledModules)
    {
        m.CompanyId = defaultCompany.Id;
        await seedStore.UpdateModuleAsync(m);
    }

    foreach (var s in (await seedStore.GetSuitesAsync()))
        if (s.CompanyId == 0) { s.CompanyId = defaultCompany.Id; await seedStore.UpdateSuiteAsync(s); }
    foreach (var r in (await seedStore.GetReleasesAsync()))
        if (r.CompanyId == 0) { r.CompanyId = defaultCompany.Id; await seedStore.UpdateReleaseAsync(r); }
    foreach (var tr in (await seedStore.GetTestRunsAsync(null)))
        if (tr.CompanyId == 0) { tr.CompanyId = defaultCompany.Id; await seedStore.UpdateTestRunAsync(tr); }
    foreach (var k in (await seedStore.GetApiKeysAsync()))
        if (k.CompanyId == 0) { k.CompanyId = defaultCompany.Id; await seedStore.UpdateApiKeyAsync(k); }
    foreach (var e in (await seedStore.GetEnvironmentTargetsAsync()))
        if (e.CompanyId == 0) { e.CompanyId = defaultCompany.Id; await seedStore.UpdateEnvironmentTargetAsync(e); }
    foreach (var i in (await seedStore.GetInviteLinksAsync()))
        if (i.CompanyId == 0) { i.CompanyId = defaultCompany.Id; await seedStore.UpdateInviteLinkAsync(i); }

    // Default Team: everyone already in Default Company, every module already in Default
    // Company -- so pre-Phase-8 users keep seeing exactly what they could see before.
    var defaultTeams = await seedStore.GetTeamsAsync(defaultCompany.Id);
    var defaultTeam = defaultTeams.FirstOrDefault(t => t.Name == "Default Team");
    if (defaultTeam is null)
        defaultTeam = await seedStore.CreateTeamAsync(new TestCaseHub.Api.Models.Team { CompanyId = defaultCompany.Id, Name = "Default Team", CreatedBy = "system" });
    foreach (var u in (await seedStore.GetUsersAsync()).Where(u => u.CompanyId == defaultCompany.Id))
        await seedStore.AddTeamMemberAsync(defaultTeam.Id, u.Id);
    foreach (var m in (await seedStore.GetModulesAsync()).Where(m => m.CompanyId == defaultCompany.Id))
        await seedStore.AddTeamModuleAsync(defaultTeam.Id, m.Id);

    // Native execution definitions for the 19 Approved UWMC scripts (see
    // Data/UwmcExecutionDefinitions.cs) -- idempotent: only fills in scripts that don't already
    // have one, so a manually-edited definition is never overwritten by a redeploy. Scoped to
    // module id 8 (UWMC) wherever it lives; safe no-op if that module/company doesn't exist yet
    // (e.g. a brand-new deployment with no UWMC data at all).
    foreach (var company in await seedStore.GetCompaniesAsync())
    {
        var uwmcScripts = (await seedStore.GetAutomationScriptsAsync(company.Id, moduleId: null, suiteId: null, testCaseId: null))
            .Where(s => TestCaseHub.Api.Data.UwmcExecutionDefinitions.ByTestCaseId.ContainsKey(s.TestCaseId ?? "") && string.IsNullOrWhiteSpace(s.ExecutionDefinitionJson));
        foreach (var script in uwmcScripts)
            await seedStore.SetExecutionDefinitionAsync(script.Id, TestCaseHub.Api.Data.UwmcExecutionDefinitions.ByTestCaseId[script.TestCaseId!]);
    }
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

// The password-reset email links (AuthController.ForgotPassword) point at
// "<origin>/reset-password?token=...", but that's a client-side-only route -- there's no
// actual /reset-password file or controller action. Without this, that link would just 404.
// index.html itself checks the "token" query parameter and shows the reset form when present.
app.MapFallbackToFile("index.html");

app.Run();
