using System.ComponentModel.DataAnnotations;

namespace TestCaseHub.Api.Models;

// A named login that automation can use to actually sign into the target app for a given
// EnvironmentTarget. Agreed in planning: one environment often needs SEVERAL distinct logins
// (a plain admin, a non-Modern-Trade company's admin, a second company for isolation checks --
// exactly the three the UWMC automation project itself needed), so this is a child collection
// on EnvironmentTarget rather than a single Username/Password pair bolted onto it. Password is
// encrypted at rest (SecretProtector) and, like the DB connection strings on EnvironmentTarget,
// is never returned in full by any GET -- only "is one set" plus the Label/Tag used to pick it.
//
// Permission split (agreed in planning): setting/editing a credential's password requires
// CanConfigureAutomationCredentials (Admin+, same bar as the DB connection strings it sits next
// to); actually TRIGGERING a run that uses one requires only CanTriggerTestRun (Lead+) -- a Lead
// picks a credential by Label from a dropdown and never sees the plaintext.
public class EnvironmentCredential
{
    public int Id { get; set; }
    public int EnvironmentTargetId { get; set; }

    // Human-readable identifier so a Lead (who never sees the password) can still pick the
    // right login, e.g. "Primary Admin", "Non-MT Test Company", "Company B Admin".
    [Required, MaxLength(128)]
    public string Label { get; set; } = "";
    [MaxLength(256)]
    public string Email { get; set; } = "";
    public string PasswordEncrypted { get; set; } = "";

    // Optional free-form tag so AI-generation can auto-match a test case's precondition to the
    // right credential -- e.g. "modern-trade-enabled" / "modern-trade-disabled" -- without a
    // human having to hand-pick one every time a script runs. Not mechanically enforced; a hint,
    // not a guarantee.
    [MaxLength(128)]
    public string Tag { get; set; } = "";

    [MaxLength(256)]
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(256)]
    public string UpdatedBy { get; set; } = "";
    public DateTime? UpdatedAt { get; set; }
}
