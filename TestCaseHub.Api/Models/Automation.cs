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
