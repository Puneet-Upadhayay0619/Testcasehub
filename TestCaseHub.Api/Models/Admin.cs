using System.ComponentModel.DataAnnotations;

namespace TestCaseHub.Api.Models;

// Separate from TestCaseHistory (which tracks test-case content changes) — this tracks
// security/permission-relevant actions: role/scope changes, deactivation, invite-link
// issuance/revocation, API-key issuance/revocation (Phase 6). Agreed in planning as its own
// audit trail so "who changed whose access, and when" is always answerable.
public class AuditLog
{
    public int Id { get; set; }

    // Phase 8: company isolation. Null for SuperAdmin-level actions (creating a company, etc.)
    public int? CompanyId { get; set; }
    [MaxLength(256)]
    public string ActorEmail { get; set; } = "";
    [MaxLength(128)]
    public string ActorDisplayName { get; set; } = "";
    [MaxLength(64)]
    public string Action { get; set; } = ""; // e.g. "RoleChanged", "UserDeactivated", "InviteCreated"
    [MaxLength(256)]
    public string TargetDescription { get; set; } = ""; // e.g. target user's email
    public string DetailsJson { get; set; } = "{}";
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

// Admin-issued invite link (agreed approach to keep registration controlled without making
// Admin manually create every account): Admin generates a Code with an expiry + max uses,
// shares it once, and anyone with the code can self-register — always as Viewer, same as the
// old open-registration default. Consumed atomically (MaxUses/UsedCount) so it can't be
// replayed beyond its intended use count.
public class InviteLink
{
    public int Id { get; set; }

    // Phase 8: which company this invite adds new users into.
    public int CompanyId { get; set; }
    [Required, MaxLength(64)]
    public string Code { get; set; } = "";
    public int MaxUses { get; set; } = 1;
    public int UsedCount { get; set; } = 0;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(7);
    public bool Revoked { get; set; } = false;
    [MaxLength(256)]
    public string CreatedByEmail { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsUsable => !Revoked && UsedCount < MaxUses && DateTime.UtcNow < ExpiresAt;
}
