namespace TestCaseHub.Api.Models;

// The four roles agreed on for Test Case Hub RBAC. Role is a hierarchy (Viewer < Contributor <
// Lead < Admin) for "at least this level" checks; Layer/Module scope (on User) is a separate,
// orthogonal restriction — a role says WHAT you can do, scope says WHICH layers/modules you can
// do it to. Both are enforced identically for REST controllers and MCP tools via the
// ClaimsPrincipal extension methods in Services/Permissions.cs, since both read the same JWT.
public static class Roles
{
    public const string Admin = "Admin";
    public const string Lead = "Lead";
    public const string Contributor = "Contributor";
    public const string Viewer = "Viewer";

    public static readonly string[] All = { Admin, Lead, Contributor, Viewer };

    public static bool IsValid(string? role) => role is not null && All.Contains(role);
}
