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

    [MaxLength(256)]
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
