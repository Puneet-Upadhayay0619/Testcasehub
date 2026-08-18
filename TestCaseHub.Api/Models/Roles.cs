namespace TestCaseHub.Api.Models;

// Role hierarchy: Viewer < Contributor < Lead < Admin < SuperAdmin, for "at least this level"
// checks. SuperAdmin (Phase 8 -- multi-company) sits ABOVE Admin: Admin is scoped to their OWN
// company, SuperAdmin spans every company (creates companies, issues each company's first
// admin referral code, and can drill into any single company's data -- always company-wise,
// never a mixed cross-company view). Layer/Module scope (on User) and Team membership are
// separate, orthogonal restrictions -- a role says WHAT you can do, company says WHICH tenant's
// data you're even allowed to touch, team says WHICH modules within that company. All three are
// enforced identically for REST controllers and MCP tools via the ClaimsPrincipal extension
// methods in Services/Permissions.cs, since both read the same JWT.
public static class Roles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Admin = "Admin";
    public const string Lead = "Lead";
    public const string Contributor = "Contributor";
    public const string Viewer = "Viewer";

    public static readonly string[] All = { SuperAdmin, Admin, Lead, Contributor, Viewer };

    public static bool IsValid(string? role) => role is not null && All.Contains(role);
}
