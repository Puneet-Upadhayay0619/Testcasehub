using System.ComponentModel.DataAnnotations;

namespace TestCaseHub.Api.Models;

// CI/pipeline machine identity (agreed in planning): NOT tied to a real person, no expiry,
// Admin can revoke any time, scoped to a name so results attribute to "Automated (CI)" instead
// of a human. Only the hash is ever stored — same principle as passwords/refresh tokens.
public class ApiKey
{
    public int Id { get; set; }

    // Phase 8: company isolation -- a CI key belongs to exactly one company.
    public int CompanyId { get; set; }
    [Required, MaxLength(128)]
    public string Name { get; set; } = "";
    [Required, MaxLength(128)]
    public string KeyHash { get; set; } = "";
    [MaxLength(64)]
    public string Scope { get; set; } = "ReportResults";
    public bool Revoked { get; set; } = false;
    [MaxLength(256)]
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }

    // Optional, non-enforced label -- which platform's CI this key is meant for (e.g. a
    // dedicated key for the mobile GitHub Actions pipeline). Purely organizational/audit; a key
    // still authenticates for ANY company results regardless of this value. Blank = unscoped
    // (matches every platform filter), same convention as ModuleRepoLink's optional fields.
    [MaxLength(16)]
    public string TestingPlatform { get; set; } = "";
}

// The Dashboard/App-API/App taxonomy used across Test Case Hub, under a name that collides
// with neither TestRunResult.Platform (browser/device dimension, e.g. "Chrome 126") nor
// ModuleRepoLink.Layer (RepoLayer -- Frontend/Backend/Database, a repo-STRUCTURE concept).
// AutomationScript keeps its existing "Layer" field name (shipped first, no collision there) --
// same taxonomy, different name, for this historical reason. "Both" is valid only for entities
// that are pure organization/filtering (TestRun, TestSuite, ApiKey) -- never for something tied
// to one concrete piece of infrastructure (EnvironmentTarget, ModuleRepoLink), where exactly one
// real platform must be picked. Core3 is what those infra-bound entities validate against.
public static class TestingPlatform
{
    public const string Dashboard = "Dashboard";
    public const string AppApi = "App-API";
    public const string App = "App";
    public const string Both = "Both";
    public static readonly string[] Core3 = { Dashboard, AppApi, App };
    public static readonly string[] All = { Dashboard, AppApi, App, Both };
}

public static class EnvironmentType
{
    public const string Staging = "Staging";
    public const string Production = "Production";
    public static readonly string[] All = { Staging, Production };
}

// A concrete, addressable target for automation to run against: one tenant + one environment,
// with per-layer base URLs and the three-part DB architecture (MasterDB/TransactionDB/ReportDB)
// agreed in planning. Connection strings are stored ENCRYPTED (SecretProtector) and only ever
// returned to callers as a masked boolean ("is one configured"), never the plaintext.
// EnvironmentType is what the Production safety block (TestRunsController) keys off of.
public class EnvironmentTarget
{
    public int Id { get; set; }

    // Phase 8: company isolation.
    public int CompanyId { get; set; }
    [Required, MaxLength(128)]
    public string Name { get; set; } = "";
    [MaxLength(64)]
    public string Tenant { get; set; } = "";
    [MaxLength(16)]
    public string EnvironmentType { get; set; } = Models.EnvironmentType.Staging;

    [MaxLength(512)] public string DashboardBaseUrl { get; set; } = "";
    [MaxLength(512)] public string AppApiBaseUrl { get; set; } = "";
    [MaxLength(512)] public string AppBaseUrl { get; set; } = "";

    // Which ONE platform this environment record is for (see TestingPlatform.Core3 -- no
    // "Both" here, an environment is one concrete piece of infrastructure). Existing rows
    // migrate to Dashboard (everything real configured so far has been Dashboard). Native
    // execution's Execute endpoint rejects a script whose own Layer doesn't match this, so a
    // Dashboard script can no longer be accidentally pointed at an App-API-tagged environment.
    [MaxLength(16)]
    public string TestingPlatform { get; set; } = Models.TestingPlatform.Dashboard;

    public string MasterDbConnectionStringEncrypted { get; set; } = "";
    public string TransactionDbConnectionStringEncrypted { get; set; } = "";
    public string ReportDbConnectionStringEncrypted { get; set; } = "";

    // Documentation/reporting flag, not mechanically enforced (cleanup depends on the actual
    // target app) — surfaced so it's visible which environments need a teardown step.
    public bool RequiresTestDataCleanup { get; set; } = true;

    // The real FieldAssist tenant CompanyId (in FieldAssist's OWN database, NOT this Test Case
    // Hub's CompanyId) that native execution's SQL assertions should filter by. Added because
    // Test Case Hub's own CompanyId (e.g. 3 for Flick2know) has nothing to do with the numeric
    // CompanyId inside FieldAssist's MTModuleConfigurations table -- ScriptExecutionService
    // substitutes the "{{TestCompanyId}}" template token in a step's sql Params (or http Body)
    // with this value at run time, so step definitions stay portable across environments.
    public int? TestCompanyId { get; set; }
    // Second tenant CompanyId, needed only by cross-company isolation checks (e.g. DSH-037:
    // "saving Company A's config must never touch Company B's rows").
    public int? TestCompanyBId { get; set; }
    // A ModuleEnum value safe to toggle MTModules.IsActive on/off for a test (e.g. DSH-043)
    // without touching one of the 14 live-seeded production modules.
    public int? TestReservedModuleEnum { get; set; }

    // Guardrail (agreed in planning): a "sql"/"sqlForEach" step whose query looks destructive
    // (DELETE/UPDATE/INSERT/etc.) is refused by ScriptExecutionService unless this is explicitly
    // true. Admin-only toggle, off by default -- exists specifically so a misconfigured
    // TestCompanyId can never silently run destructive SQL against the wrong real company.
    public bool AllowDestructiveTestSql { get; set; } = false;

    [MaxLength(256)]
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
