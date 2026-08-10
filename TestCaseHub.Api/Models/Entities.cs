using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace TestCaseHub.Api.Models;

// Phase 8 (multi-company): one deployment now serves multiple companies out of the SAME
// database, isolated by CompanyId rather than by separate deployments. SuperAdmin creates
// these; everything else (Users, Modules, ...) hangs off a CompanyId. Nullable-on-User only
// (SuperAdmin has no company -- they span all of them); everywhere else CompanyId is required.
public class Company
{
    public int Id { get; set; }
    [Required, MaxLength(256)]
    public string Name { get; set; } = "";
    [MaxLength(32)]
    public string Status { get; set; } = "Active"; // "Active" or "Suspended"
    [MaxLength(256)]
    public string CreatedBy { get; set; } = ""; // SuperAdmin's email
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// A company-scoped referral code a SuperAdmin issues so a company's FIRST user can self-register
// as that company's Admin -- deliberately separate from InviteLink (Admin.cs), which is how an
// existing company's Admin invites MORE users into an already-provisioned company. Same
// single-use-friendly shape (MaxUses/UsedCount/ExpiresAt/Revoked) as InviteLink, on purpose.
public class CompanyAdminInvite
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    [ForeignKey(nameof(CompanyId))]
    [JsonIgnore]
    public Company? Company { get; set; }

    [Required, MaxLength(64)]
    public string Code { get; set; } = "";
    public int MaxUses { get; set; } = 1;
    public int UsedCount { get; set; } = 0;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(14);
    public bool Revoked { get; set; } = false;
    [MaxLength(256)]
    public string CreatedByEmail { get; set; } = ""; // SuperAdmin's email
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool IsUsable => !Revoked && UsedCount < MaxUses && DateTime.UtcNow < ExpiresAt;
}

// A team within a company. A user can belong to multiple teams (TeamMember); a team can be
// assigned multiple modules and a module can be assigned to multiple teams (TeamModule) -- this
// is what makes "two teams sharing one module" a supported case rather than a conflict: a
// user's effective module access is the UNION of every team they're in.
public class Team
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    [ForeignKey(nameof(CompanyId))]
    [JsonIgnore]
    public Company? Company { get; set; }

    [Required, MaxLength(128)]
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    [MaxLength(256)]
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class TeamMember
{
    public int Id { get; set; }
    public int TeamId { get; set; }
    [ForeignKey(nameof(TeamId))]
    [JsonIgnore]
    public Team? Team { get; set; }
    public int UserId { get; set; }
    [ForeignKey(nameof(UserId))]
    [JsonIgnore]
    public User? User { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

public class TeamModule
{
    public int Id { get; set; }
    public int TeamId { get; set; }
    [ForeignKey(nameof(TeamId))]
    [JsonIgnore]
    public Team? Team { get; set; }
    public int ModuleId { get; set; }
    [ForeignKey(nameof(ModuleId))]
    [JsonIgnore]
    public Module? Module { get; set; }
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
}

public class User
{
    public int Id { get; set; }
    [Required, MaxLength(256)]
    public string Email { get; set; } = "";
    [Required]
    public string PasswordHash { get; set; } = "";
    [Required, MaxLength(128)]
    public string DisplayName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Phase 8: which company this user belongs to. Null ONLY for SuperAdmin -- every other
    // role must have a company. Existing users (pre-multi-company) are backfilled to a
    // "Default Company" created at startup so nothing they had access to disappears.
    public int? CompanyId { get; set; }
    [ForeignKey(nameof(CompanyId))]
    [JsonIgnore]
    public Company? Company { get; set; }

    // RBAC (Phase 1, extended Phase 8 with SuperAdmin). Role is one of Roles.All. IsActive=false
    // means "deactivated" -- login is blocked but every historical CreatedBy/UpdatedBy/History
    // record naming this user is left untouched (per the explicit decision: deactivate =
    // login-block only, nothing else).
    [MaxLength(32)]
    public string Role { get; set; } = Models.Roles.Viewer;
    public bool IsActive { get; set; } = true;

    // Layer/Module scope: legacy per-user override, superseded by Team-based module access
    // (Phase 8) but left in place (never enforced in any controller, so nothing breaks by
    // leaving it inert) rather than ripped out, since existing rows already carry "[]" values.
    public string LayerScopeJson { get; set; } = "[]";
    public string ModuleScopeJson { get; set; } = "[]";

    [NotMapped]
    public List<string> LayerScope
    {
        get => System.Text.Json.JsonSerializer.Deserialize<List<string>>(LayerScopeJson) ?? new();
        set => LayerScopeJson = System.Text.Json.JsonSerializer.Serialize(value);
    }

    [NotMapped]
    public List<int> ModuleScope
    {
        get => System.Text.Json.JsonSerializer.Deserialize<List<int>>(ModuleScopeJson) ?? new();
        set => ModuleScopeJson = System.Text.Json.JsonSerializer.Serialize(value);
    }
}

public class Module
{
    public int Id { get; set; }

    // Phase 8: every module belongs to exactly one company. Module.Code's uniqueness check
    // (IDataStore.ModuleCodeExistsAsync) is now scoped per-company, not global.
    public int CompanyId { get; set; }
    [ForeignKey(nameof(CompanyId))]
    [JsonIgnore]
    public Company? Company { get; set; }

    [Required, MaxLength(32)]
    public string Code { get; set; } = "";
    [Required, MaxLength(256)]
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Owner { get; set; } = "";
    [MaxLength(32)]
    public string Status { get; set; } = "Active";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public List<TaskLink> TaskLinks { get; set; } = new();
    [JsonIgnore] public List<TestCase> TestCases { get; set; } = new();
}

public class TaskLink
{
    public int Id { get; set; }
    public int ModuleId { get; set; }
    [ForeignKey(nameof(ModuleId))]
    [JsonIgnore]
    public Module? Module { get; set; }

    [Required, MaxLength(32)]
    public string Layer { get; set; } = "";
    [MaxLength(64)]
    public string AdoProject { get; set; } = "";
    [MaxLength(64)]
    public string AdoTaskId { get; set; } = "";
    [MaxLength(512)]
    public string AdoTaskTitle { get; set; } = "";
    [MaxLength(1024)]
    public string AdoTaskUrl { get; set; } = "";
    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;
}

// Steps and Tags are stored as JSON text columns (StepsJson/TagsJson) rather than
// normalized child tables — keeps the MVP schema simple. See README for the
// normalization note if step-level querying is ever needed.
public class TestCase
{
    // Business-key style Id (e.g. "TC-UMC-DSH-001") kept as the primary key so it matches
    // the ID convention already used across the tool (exports, history, audit trail).
    [Key, MaxLength(64)]
    public string Id { get; set; } = "";

    public int ModuleId { get; set; }
    [ForeignKey(nameof(ModuleId))]
    [JsonIgnore]
    public Module? Module { get; set; }

    // Phase 8: which Team authored/owns this case. Nullable -- old rows (pre-Team) and cases
    // created by a user who belongs to exactly one team (no choice to make) may leave this
    // unset. Company is NOT duplicated here on purpose -- it's derived from ModuleId->Module,
    // so a test case can never disagree with its own module about which company it belongs to.
    public int? TeamId { get; set; }
    [ForeignKey(nameof(TeamId))]
    [JsonIgnore]
    public Team? Team { get; set; }

    [Required, MaxLength(32)]
    public string Layer { get; set; } = "";
    [Required, MaxLength(32)]
    public string VerificationType { get; set; } = "";
    [Required, MaxLength(512)]
    public string Title { get; set; } = "";
    public string Preconditions { get; set; } = "";

    public string StepsJson { get; set; } = "[]";

    [MaxLength(32)]
    public string Priority { get; set; } = "P2";
    [MaxLength(32)]
    public string Type { get; set; } = "Functional";
    [MaxLength(32)]
    public string Status { get; set; } = "Draft";

    public string TagsJson { get; set; } = "[]";

    public bool AutomationReady { get; set; }
    [MaxLength(512)]
    public string AutomationScriptRef { get; set; } = "";

    // Phase 6: automation metadata. AutomationConfigJson holds the structured, declarative
    // config an automation engineer fills in (API endpoint/method/expected-status, or a DB
    // SELECT query + expected value) -- this is the "evidence" automationReady=true is checked
    // against, and it's what a CI script would read to actually run the check. SelectorStability
    // documents how fragile the UI locator strategy is (High=data-testid, Medium=role/text,
    // Low=structural CSS/XPath) so the team can prioritize review of fragile scripts.
    public string AutomationConfigJson { get; set; } = "{}";
    [MaxLength(16)]
    public string SelectorStability { get; set; } = ""; // "", "High", "Medium", "Low"

    // Cross-module scenarios (Phase 4): this test case's PRIMARY home is still ModuleId (used
    // for its generated ID prefix etc.) — LinkedModuleIds are OTHER modules this scenario also
    // touches, e.g. "create order in Module A, verify in Module B", so it's discoverable from
    // both places without duplicating the test case.
    public string LinkedModuleIdsJson { get; set; } = "[]";

    [NotMapped]
    public List<int> LinkedModuleIds
    {
        get => System.Text.Json.JsonSerializer.Deserialize<List<int>>(LinkedModuleIdsJson) ?? new();
        set => LinkedModuleIdsJson = System.Text.Json.JsonSerializer.Serialize(value);
    }

    [MaxLength(256)]
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(256)]
    public string UpdatedBy { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int Version { get; set; } = 1;

    [NotMapped]
    public List<TestCaseStep> Steps
    {
        get => System.Text.Json.JsonSerializer.Deserialize<List<TestCaseStep>>(StepsJson) ?? new();
        set => StepsJson = System.Text.Json.JsonSerializer.Serialize(value);
    }

    [NotMapped]
    public List<string> Tags
    {
        get => System.Text.Json.JsonSerializer.Deserialize<List<string>>(TagsJson) ?? new();
        set => TagsJson = System.Text.Json.JsonSerializer.Serialize(value);
    }
}

public class TestCaseStep
{
    public int StepNo { get; set; }
    public string Action { get; set; } = "";
    public string ExpectedResult { get; set; } = "";
}

public class TestCaseHistory
{
    public int Id { get; set; }
    [Required, MaxLength(64)]
    public string TestCaseId { get; set; } = "";
    [MaxLength(256)]
    public string ChangedBy { get; set; } = "";
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(32)]
    public string ChangeType { get; set; } = "";
    public string? OldSnapshotJson { get; set; }
    public string NewSnapshotJson { get; set; } = "{}";
    public string Comment { get; set; } = "";
}

public class PriorityOption
{
    public int Id { get; set; }
    [Required, MaxLength(32)]
    public string Value { get; set; } = "";
    public bool IsCustom { get; set; }
}

public class StatusOption
{
    public int Id { get; set; }
    [Required, MaxLength(32)]
    public string Value { get; set; } = "";
    public bool IsCustom { get; set; }
}
