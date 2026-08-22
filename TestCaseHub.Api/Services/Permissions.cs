using System.Security.Claims;
using TestCaseHub.Api.Models;

namespace TestCaseHub.Api.Services;

// The ONE place role/scope logic lives. REST controllers (via ControllerBase.User) and MCP
// tools (via the ClaimsPrincipal parameter the MCP SDK injects) both call these exact same
// extension methods against the exact same JWT claims — so "what can this person do" can never
// drift between the two surfaces, which was the whole point of building RBAC once instead of
// twice.
public static class Permissions
{
    private static readonly Dictionary<string, int> RoleRank = new()
    {
        [Roles.Viewer] = 0,
        [Roles.Contributor] = 1,
        [Roles.Lead] = 2,
        [Roles.Admin] = 3,
        [Roles.SuperAdmin] = 4,
    };

    public static string GetRole(this ClaimsPrincipal user) =>
        user.FindFirstValue("role") is { Length: > 0 } r && Roles.IsValid(r) ? r : Roles.Viewer;

    // "At least" check along the Viewer < Contributor < Lead < Admin < SuperAdmin hierarchy.
    public static bool IsAtLeast(this ClaimsPrincipal user, string minRole) =>
        RoleRank.TryGetValue(user.GetRole(), out var have) && RoleRank.TryGetValue(minRole, out var need) && have >= need;

    public static bool IsAdmin(this ClaimsPrincipal user) => user.GetRole() == Roles.Admin;
    public static bool IsSuperAdmin(this ClaimsPrincipal user) => user.GetRole() == Roles.SuperAdmin;

    // --- Feature-level checks (name the decision, not the role, so the role that satisfies it
    // can change later in one place without hunting through every call site) ---

    // Agreed: module creation is Contributor and above (moved down from Lead during planning).
    public static bool CanCreateModule(this ClaimsPrincipal user) => user.IsAtLeast(Roles.Contributor);

    // Deleting a module takes every one of its test cases with it -- a much bigger blast
    // radius than editing/creating one, so it sits at the same Admin-and-above bar as managing
    // users/teams, not the lower Contributor bar module CREATION uses.
    public static bool CanDeleteModule(this ClaimsPrincipal user) => user.IsAtLeast(Roles.Admin);

    // Viewer is read-only by design across the whole app: creating/editing/deprecating test
    // cases (including bulk-edit and spreadsheet import, which both go through the same
    // create/update path) requires Contributor and above, same bar as module creation.
    public static bool CanEditTestCases(this ClaimsPrincipal user) => user.IsAtLeast(Roles.Contributor);

    // Linking an ADO task to a module is content-editing, same bar as everything else here.
    public static bool CanEditTaskLinks(this ClaimsPrincipal user) => user.IsAtLeast(Roles.Contributor);

    // Creating a test suite (static or dynamic) is content-editing, same bar as everything
    // else here — Viewer can still view/resolve suites, just not create them.
    public static bool CanManageSuites(this ClaimsPrincipal user) => user.IsAtLeast(Roles.Contributor);

    // Agreed: only Lead/Admin/SuperAdmin can set or clear automationReady — this is the role
    // half of the flag-integrity control; the evidence-backed half (script ref / automation
    // config must actually exist) lands in Phase 6.
    public static bool CanManageAutomationReady(this ClaimsPrincipal user) => user.IsAtLeast(Roles.Lead);

    // Agreed: Admin (and SuperAdmin) manage other users' roles/scope, invites, deactivation —
    // ALWAYS scoped to the acting Admin's own company (see HasCompanyAccess below); a bare
    // Admin can never reach across into another company's users this way.
    public static bool CanManageUsers(this ClaimsPrincipal user) => user.IsAtLeast(Roles.Admin);

    // Only SuperAdmin creates companies / issues a company's first-admin referral code.
    public static bool CanManageCompanies(this ClaimsPrincipal user) => user.IsSuperAdmin();

    // Admin (within their own company) and SuperAdmin (anywhere) manage Team membership and
    // Team<->Module assignment.
    public static bool CanManageTeams(this ClaimsPrincipal user) => user.IsAtLeast(Roles.Admin);

    // --- Automation-generation architecture (agreed in planning) ---

    // Linking a module to a GitHub/ADO repo exposes read access to proprietary source code --
    // same Admin-and-above bar as configuring DB connection strings on an Environment Target.
    public static bool CanManageRepoLinks(this ClaimsPrincipal user) => user.IsAtLeast(Roles.Admin);

    // Setting/editing a named execution credential's password is Admin-and-above, same bar as
    // the DB connection strings it lives next to on the same Environment Target.
    public static bool CanConfigureAutomationCredentials(this ClaimsPrincipal user) => user.IsAtLeast(Roles.Admin);

    // Explicitly agreed: Company Admin AND Team Lead can both trigger an automation run --
    // Lead only ever picks a credential by Label, never sees the plaintext password.
    public static bool CanTriggerTestRun(this ClaimsPrincipal user) => user.IsAtLeast(Roles.Lead);

    // Saving/retrieving AI-generated automation scripts: same Contributor-and-above bar as
    // editing test cases -- a QA contributor should be able to save a script they just had
    // Claude generate for their own test case without needing a Lead/Admin to do it for them.
    public static bool CanManageAutomationScripts(this ClaimsPrincipal user) => user.IsAtLeast(Roles.Contributor);

    // --- Company scope (Phase 8) ---
    // Every role except SuperAdmin carries exactly one CompanyId; SuperAdmin carries none
    // (represented as null) because they're not confined to one company at all.
    public static int? GetCompanyId(this ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue("companyId");
        return int.TryParse(raw, out var v) ? v : null;
    }

    // Resolves "which company is this action happening in", for actions that WRITE a new
    // record (create module/team/suite/release/...). Non-SuperAdmin always act in their own
    // company (queryCompanyId is ignored -- they can't use it to write into someone else's
    // company just by passing a different id). SuperAdmin has no company of their own, so they
    // MUST pass queryCompanyId to "enter" a company and act as if they were its Admin -- this
    // is what lets a SuperAdmin do literally everything a real Admin could do inside any
    // company, not just create companies/referral codes.
    public static int? ResolveActingCompanyId(this ClaimsPrincipal user, int? queryCompanyId) =>
        user.IsSuperAdmin() ? queryCompanyId : user.GetCompanyId();

    // True if this user is allowed to touch a record belonging to targetCompanyId.
    // SuperAdmin: always true (spans every company, viewed one company at a time by their own
    // choice in the UI/API call, not by a database-level restriction).
    // Everyone else: true only if it's literally their own company.
    public static bool HasCompanyAccess(this ClaimsPrincipal user, int targetCompanyId) =>
        user.IsSuperAdmin() || user.GetCompanyId() == targetCompanyId;

    // --- Team scope (Phase 8) ---
    // Comma-separated Team ids embedded in the JWT at login (Team membership doesn't change
    // often enough to justify a per-request DB round trip just to compute this).
    public static List<int> GetTeamIds(this ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue("teamIds") ?? "";
        return raw.Length == 0 ? new() : raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var v) ? v : -1).Where(v => v >= 0).ToList();
    }

    // Admin and SuperAdmin bypass team-based module restriction entirely (Admin sees every
    // module in their own company; SuperAdmin sees every module in every company). Lead /
    // Contributor / Viewer are restricted to modules assigned to at least one team they're a
    // member of — moduleTeamIds is the set of Team ids assigned to the module in question; a
    // module assigned to NO team yet is visible to nobody below Admin (matches "not yet
    // organized into a team" rather than silently defaulting to open access).
    public static bool HasModuleAccessViaTeam(this ClaimsPrincipal user, List<int> moduleTeamIds)
    {
        if (user.IsAtLeast(Roles.Admin)) return true;
        var myTeams = user.GetTeamIds();
        return moduleTeamIds.Any(t => myTeams.Contains(t));
    }

    // --- Layer/Module scope (legacy, orthogonal to role — never enforced anywhere; superseded
    // by Team-based module access above, kept only so old User rows keep deserializing) ---

    public static List<string> GetLayerScope(this ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue("layerScope") ?? "";
        return raw.Length == 0 ? new() : raw.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public static List<int> GetModuleScope(this ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue("moduleScope") ?? "";
        return raw.Length == 0 ? new() : raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var v) ? v : -1).Where(v => v >= 0).ToList();
    }

    public static bool HasLayerAccess(this ClaimsPrincipal user, string layer)
    {
        var scope = user.GetLayerScope();
        return scope.Count == 0 || scope.Contains(layer);
    }

    public static bool HasModuleAccess(this ClaimsPrincipal user, int moduleId)
    {
        var scope = user.GetModuleScope();
        return scope.Count == 0 || scope.Contains(moduleId);
    }
}
