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
    };

    public static string GetRole(this ClaimsPrincipal user) =>
        user.FindFirstValue("role") is { Length: > 0 } r && Roles.IsValid(r) ? r : Roles.Viewer;

    // "At least" check along the Viewer < Contributor < Lead < Admin hierarchy.
    public static bool IsAtLeast(this ClaimsPrincipal user, string minRole) =>
        RoleRank.TryGetValue(user.GetRole(), out var have) && RoleRank.TryGetValue(minRole, out var need) && have >= need;

    public static bool IsAdmin(this ClaimsPrincipal user) => user.GetRole() == Roles.Admin;

    // --- Feature-level checks (name the decision, not the role, so the role that satisfies it
    // can change later in one place without hunting through every call site) ---

    // Agreed: module creation is Contributor and above (moved down from Lead during planning).
    public static bool CanCreateModule(this ClaimsPrincipal user) => user.IsAtLeast(Roles.Contributor);

    // Viewer is read-only by design across the whole app: creating/editing/deprecating test
    // cases (including bulk-edit and spreadsheet import, which both go through the same
    // create/update path) requires Contributor and above, same bar as module creation.
    public static bool CanEditTestCases(this ClaimsPrincipal user) => user.IsAtLeast(Roles.Contributor);

    // Linking an ADO task to a module is content-editing, same bar as everything else here.
    public static bool CanEditTaskLinks(this ClaimsPrincipal user) => user.IsAtLeast(Roles.Contributor);

    // Creating a test suite (static or dynamic) is content-editing, same bar as everything
    // else here — Viewer can still view/resolve suites, just not create them.
    public static bool CanManageSuites(this ClaimsPrincipal user) => user.IsAtLeast(Roles.Contributor);

    // Agreed: only Lead/Admin can set or clear automationReady — this is the role half of the
    // flag-integrity control; the evidence-backed half (script ref / automation config must
    // actually exist) lands in Phase 6.
    public static bool CanManageAutomationReady(this ClaimsPrincipal user) => user.IsAtLeast(Roles.Lead);

    // Agreed: only Admin manages other users' roles/scope, invites, deactivation.
    public static bool CanManageUsers(this ClaimsPrincipal user) => user.IsAdmin();

    // --- Layer/Module scope (orthogonal to role) ---

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

    // Empty scope list = unrestricted (all layers / all modules) — see the comment on User.
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
