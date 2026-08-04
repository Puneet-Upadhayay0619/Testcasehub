using System.ComponentModel.DataAnnotations;

namespace TestCaseHub.Api.Models;

public static class ReleaseStatus
{
    public const string Draft = "Draft";
    public const string InTesting = "InTesting";
    public const string ReadyForSignoff = "ReadyForSignoff";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public static readonly string[] All = { Draft, InTesting, ReadyForSignoff, Approved, Rejected };
}

// The thing everyone actually cares about per the stated final goal — "sanity ho, sanity status
// and release status sb kuch isse tool se ho". A Release moves through explicit stages;
// Approved/Rejected are the only two that need a named approver + timestamp + comment, so
// "release status" is always a traceable decision, not just a computed number.
public class Release
{
    public int Id { get; set; }
    [Required, MaxLength(128)]
    public string Name { get; set; } = "";
    [MaxLength(32)]
    public string Version { get; set; } = "";
    [MaxLength(32)]
    public string Status { get; set; } = ReleaseStatus.Draft;
    [MaxLength(256)]
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(256)]
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string ApprovalComment { get; set; } = "";
}

// A single execution pass over a Suite (or ad-hoc, SuiteId null) against one target
// environment. TargetEnvironment is just a label for now — the real multi-env/multi-DB target
// configuration (base URLs, connection strings) is Phase 6.
public class TestRun
{
    public int Id { get; set; }
    public int? ReleaseId { get; set; }
    public int? SuiteId { get; set; }
    [Required, MaxLength(128)]
    public string Name { get; set; } = "";
    [MaxLength(64)]
    public string TargetEnvironment { get; set; } = ""; // free-text label, kept for backward compat / ad-hoc runs
    // Phase 6: structured link to a configured EnvironmentTarget (base URLs + DB connections).
    // This is what the Production safety block checks -- if the linked environment's
    // EnvironmentType is Production, automated result posting is rejected outright.
    public int? EnvironmentTargetId { get; set; }
    [MaxLength(256)]
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// One recorded outcome for one test case within a Test Run. Deliberately APPEND-ONLY — a retry
// or a re-run after a fix creates a NEW row rather than overwriting the old one, so flakiness
// (did this fail once and then pass?) is visible in the data instead of being silently erased.
// Rollups always use the LATEST result per (TestRunId, TestCaseId, Platform).
public class TestRunResult
{
    public int Id { get; set; }
    public int TestRunId { get; set; }
    [Required, MaxLength(64)]
    public string TestCaseId { get; set; } = "";
    [MaxLength(64)]
    public string? Platform { get; set; } // e.g. "Chrome 126", "Android 14 / Pixel 8" — null for API/DB checks with no device dimension
    [MaxLength(16)]
    public string Status { get; set; } = "NotRun"; // Pass / Fail / Blocked / Skipped / NotRun
    public bool IsAutomated { get; set; } = false;
    [MaxLength(256)]
    public string ExecutedBy { get; set; } = ""; // display name for manual; "Automated (CI)" for automated
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
    public string Notes { get; set; } = "";
    // Phase 6 fields, modeled now so TestRunResult never needs a second migration for them:
    public int RetryCount { get; set; } = 0;              // how many retries happened before this final outcome
    [MaxLength(64)]
    public string? BugWorkItemId { get; set; }             // linked Azure DevOps Bug, if one was filed
    [MaxLength(128)]
    public string? RunAttemptKey { get; set; }              // idempotency key for automated CI posts (null for manual)
}

public class Notification
{
    public int Id { get; set; }
    public int UserId { get; set; }
    [MaxLength(32)]
    public string Type { get; set; } = "";
    [Required]
    public string Message { get; set; } = "";
    public bool Read { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
