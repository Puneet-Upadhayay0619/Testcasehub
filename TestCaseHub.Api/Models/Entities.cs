using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace TestCaseHub.Api.Models;

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

    // RBAC (Phase 1). Role is one of Roles.All. IsActive=false means "deactivated" — login is
    // blocked but every historical CreatedBy/UpdatedBy/History record naming this user is left
    // untouched (per the explicit decision: deactivate = login-block only, nothing else).
    [MaxLength(32)]
    public string Role { get; set; } = Models.Roles.Viewer;
    public bool IsActive { get; set; } = true;

    // Layer/Module scope: which Dashboard/App-API/App layers and which specific Modules this
    // user is allowed to touch, ON TOP OF whatever their Role already permits. An EMPTY list
    // means "unrestricted" (all layers / all modules) — so existing users created before this
    // field existed keep working exactly as before with no scope narrowing.
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
