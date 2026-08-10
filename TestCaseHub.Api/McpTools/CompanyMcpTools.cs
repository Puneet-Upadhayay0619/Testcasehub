using System.ComponentModel;
using System.Security.Claims;
using ModelContextProtocol.Server;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.McpTools;

// Phase 8 (multi-company/teams) MCP tools -- mirrors CompaniesController/UsersController/
// TeamsController exactly, one role-check per tool, same as the REST surface. IMPORTANT: MCP
// access is NOT a separate privilege tier -- a Viewer's JWT is still a Viewer's JWT whether it
// arrives over REST or over MCP, so every tool here re-checks the SAME Permissions.cs rule its
// REST equivalent uses (CanManageCompanies = SuperAdmin only, CanManageUsers = Admin+, etc.)
// before touching anything. A lower-privileged account gets a clear "you do not have
// permission" object back, never a silent bypass just because the call came in as an MCP tool
// instead of an HTTP request.
[McpServerToolType]
public class CompanyMcpTools
{
    private readonly IDataStore _store;
    public CompanyMcpTools(IDataStore store) => _store = store;

    private static string EmailOf(ClaimsPrincipal user) =>
        user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email) ?? "";

    [McpServerTool(Name = "list_companies"), Description("SuperAdmin only. List every company on this deployment.")]
    public async Task<object> ListCompanies(ClaimsPrincipal user)
    {
        if (!user.CanManageCompanies())
            return new { error = "You do not have permission to manage companies (SuperAdmin only)." };
        return (await _store.GetCompaniesAsync()).Select(CompanyResponse.From).ToList();
    }

    [McpServerTool(Name = "create_company"), Description("SuperAdmin only. Create a new company (tenant). Every company's data -- modules, test cases, teams, users -- is isolated from every other company automatically.")]
    public async Task<object> CreateCompany(ClaimsPrincipal user, [Description("Company name, e.g. 'Acme Corp'")] string name)
    {
        if (!user.CanManageCompanies())
            return new { error = "You do not have permission to manage companies (SuperAdmin only)." };
        if (string.IsNullOrWhiteSpace(name))
            return new { error = "Company name is required." };

        var company = new Company { Name = name.Trim(), CreatedBy = EmailOf(user) };
        company = await _store.CreateCompanyAsync(company);

        await _store.AddAuditLogAsync(new AuditLog
        {
            CompanyId = null, ActorEmail = EmailOf(user), ActorDisplayName = EmailOf(user), Action = "CompanyCreated",
            TargetDescription = company.Name, DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { company.Id, via = "mcp" })
        });
        return CompanyResponse.From(company);
    }

    [McpServerTool(Name = "create_company_admin_invite"), Description("SuperAdmin only. Issue a referral code so the given company's FIRST user can self-register as that company's Admin.")]
    public async Task<object> CreateCompanyAdminInvite(ClaimsPrincipal user, int companyId, int maxUses = 1, int expiresInDays = 14)
    {
        if (!user.CanManageCompanies())
            return new { error = "You do not have permission to manage companies (SuperAdmin only)." };
        var company = await _store.GetCompanyAsync(companyId);
        if (company is null) return new { error = "Company not found." };

        var invite = new CompanyAdminInvite
        {
            CompanyId = companyId,
            Code = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(10)).ToLowerInvariant(),
            MaxUses = maxUses <= 0 ? 1 : maxUses,
            ExpiresAt = DateTime.UtcNow.AddDays(expiresInDays <= 0 ? 14 : expiresInDays),
            CreatedByEmail = EmailOf(user)
        };
        invite = await _store.CreateCompanyAdminInviteAsync(invite);
        return CompanyAdminInviteResponse.From(invite);
    }

    [McpServerTool(Name = "list_users_in_company"), Description("Admin (own company) or SuperAdmin (any company, must pass companyId). Lists every user in that company with their role.")]
    public async Task<object> ListUsersInCompany(ClaimsPrincipal user, int? companyId = null)
    {
        if (!user.CanManageUsers())
            return new { error = "You do not have permission to view users (Admin role or above required)." };
        var effective = user.IsSuperAdmin() ? companyId : user.GetCompanyId();
        if (effective is null)
            return new { error = user.IsSuperAdmin() ? "SuperAdmin must specify companyId." : "You have no company." };

        var users = (await _store.GetUsersAsync()).Where(u => u.CompanyId == effective).OrderBy(u => u.Id).ToList();
        var result = new List<UserResponse>();
        foreach (var u in users)
            result.Add(UserResponse.From(u, await _store.GetTeamIdsForUserAsync(u.Id)));
        return result;
    }

    [McpServerTool(Name = "assign_users_by_domain"), Description(
        "SuperAdmin only. Moves every existing user whose email ends with @<emailDomain> into the given company " +
        "-- for splitting users who were auto-backfilled into one company by email domain into their real " +
        "companies. Does NOT move modules/test cases/teams that user already created; only their own company " +
        "membership moves. Returns how many users matched and their emails.")]
    public async Task<object> AssignUsersByDomain(ClaimsPrincipal user, int companyId, [Description("e.g. 'gmail.com' (the @ is optional)")] string emailDomain)
    {
        if (!user.CanManageCompanies())
            return new { error = "You do not have permission to manage companies (SuperAdmin only)." };
        var company = await _store.GetCompanyAsync(companyId);
        if (company is null) return new { error = "Company not found." };

        var domain = (emailDomain ?? "").Trim().TrimStart('@').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(domain))
            return new { error = "emailDomain is required, e.g. 'gmail.com'." };

        var matched = (await _store.GetUsersAsync())
            .Where(u => u.Role != Roles.SuperAdmin && u.Email.ToLowerInvariant().EndsWith("@" + domain))
            .ToList();

        foreach (var u in matched)
        {
            u.CompanyId = companyId;
            await _store.UpdateUserAsync(u);
        }

        await _store.AddAuditLogAsync(new AuditLog
        {
            CompanyId = companyId, ActorEmail = EmailOf(user), ActorDisplayName = EmailOf(user), Action = "UsersBulkAssignedByDomain",
            TargetDescription = company.Name,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { domain, matchedCount = matched.Count, emails = matched.Select(u => u.Email), via = "mcp" })
        });

        return new AssignUsersByDomainResult(matched.Count, matched.Select(u => u.Email).ToList());
    }

    [McpServerTool(Name = "list_all_modules"), Description(
        "SuperAdmin only. List every module across every company (with company id/name and test case count) -- " +
        "use this to find a module's id and current company before calling move_module_to_company. " +
        "Regular Admin/Contributor/etc. should use list_modules instead (scoped to their own company).")]
    public async Task<object> ListAllModules(ClaimsPrincipal user)
    {
        if (!user.CanManageCompanies())
            return new { error = "You do not have permission to manage companies (SuperAdmin only)." };

        var modules = await _store.GetModulesAsync();
        var companies = (await _store.GetCompaniesAsync()).ToDictionary(c => c.Id, c => c.Name);
        var counts = await _store.GetTestCaseCountsByModuleAsync();

        return modules
            .OrderBy(m => m.CompanyId).ThenBy(m => m.Name)
            .Select(m => new
            {
                m.Id,
                m.Name,
                m.Code,
                m.CompanyId,
                CompanyName = companies.TryGetValue(m.CompanyId, out var cn) ? cn : "(unknown company)",
                TestCaseCount = counts.TryGetValue(m.Id, out var c) ? c : 0
            })
            .ToList();
    }

    [McpServerTool(Name = "move_module_to_company"), Description(
        "SuperAdmin only. Moves a module into a different company. Test cases are NOT stored with their own " +
        "CompanyId -- they inherit it from their module -- so every test case under this module moves along " +
        "with it automatically; nothing else needs to be done for the test cases. Any team<->module links from " +
        "the module's old company are removed (a team in company A cannot see a module now owned by company B).")]
    public async Task<object> MoveModuleToCompany(ClaimsPrincipal user, int moduleId, int targetCompanyId)
    {
        if (!user.CanManageCompanies())
            return new { error = "You do not have permission to manage companies (SuperAdmin only)." };

        var module = await _store.GetModuleAsync(moduleId);
        if (module is null) return new { error = "Module not found." };

        var targetCompany = await _store.GetCompanyAsync(targetCompanyId);
        if (targetCompany is null) return new { error = "Target company not found." };

        if (module.CompanyId == targetCompanyId)
            return new { error = $"Module '{module.Name}' is already in {targetCompany.Name}." };

        if (await _store.ModuleCodeExistsAsync(targetCompanyId, module.Code))
            return new { error = $"Target company already has a module with code '{module.Code}'. Rename one of them first." };

        var fromCompanyId = module.CompanyId;
        var fromCompany = await _store.GetCompanyAsync(fromCompanyId);

        // Drop team<->module links from the old company -- those teams belong to fromCompany and
        // have no business seeing a module that now lives in targetCompanyId.
        var staleTeamIds = await _store.GetTeamIdsForModuleAsync(moduleId);
        foreach (var teamId in staleTeamIds)
            await _store.RemoveTeamModuleAsync(teamId, moduleId);

        module.CompanyId = targetCompanyId;
        await _store.UpdateModuleAsync(module);

        var testCaseCount = (await _store.GetTestCaseCountsByModuleAsync()).TryGetValue(moduleId, out var c) ? c : 0;

        await _store.AddAuditLogAsync(new AuditLog
        {
            CompanyId = targetCompanyId, ActorEmail = EmailOf(user), ActorDisplayName = EmailOf(user), Action = "ModuleMovedToCompany",
            TargetDescription = module.Name,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                moduleId, module.Code, fromCompanyId, fromCompanyName = fromCompany?.Name,
                toCompanyId = targetCompanyId, toCompanyName = targetCompany.Name,
                testCasesMoved = testCaseCount, teamLinksRemoved = staleTeamIds.Count, via = "mcp"
            })
        });

        return new
        {
            ok = true, moduleId, moduleName = module.Name,
            fromCompanyId, fromCompanyName = fromCompany?.Name,
            toCompanyId = targetCompanyId, toCompanyName = targetCompany.Name,
            testCasesMoved = testCaseCount, teamLinksRemoved = staleTeamIds.Count
        };
    }

    [McpServerTool(Name = "create_team"), Description(
        "Admin (own company, companyId optional) or SuperAdmin (any company, companyId required). " +
        "Creates a new team inside a company -- teams are how you group users and share modules between them.")]
    public async Task<object> CreateTeam(ClaimsPrincipal user, [Description("Team name, e.g. 'OneWorld Test'")] string name, int? companyId = null, string? description = null)
    {
        // Same rule as the REST POST /api/teams endpoint -- Admin role or above, company resolved
        // the same way (SuperAdmin must pass companyId explicitly; everyone else uses their own).
        if (!user.CanManageTeams())
            return new { error = "You do not have permission to manage teams (Admin role or above required)." };

        var resolved = user.ResolveActingCompanyId(companyId);
        if (resolved is null)
            return new { error = user.IsSuperAdmin() ? "SuperAdmin must specify companyId." : "You have no company." };

        if (string.IsNullOrWhiteSpace(name))
            return new { error = "Team name is required." };

        var company = await _store.GetCompanyAsync(resolved.Value);
        if (company is null) return new { error = "Company not found." };

        var team = new Team { CompanyId = resolved.Value, Name = name.Trim(), Description = description ?? "", CreatedBy = EmailOf(user) };
        team = await _store.CreateTeamAsync(team);

        return new TeamResponse(team.Id, team.CompanyId, team.Name, team.Description, team.CreatedBy, team.CreatedAt, new List<int>(), new List<int>());
    }

    [McpServerTool(Name = "add_team_module"), Description(
        "Admin (own company) or SuperAdmin (any company, as long as the team and module are both in that same " +
        "company). Shares a module with a team -- this is what lets two different teams both see the same " +
        "module. A module already assigned to another team is fine; that's the whole point of this feature.")]
    public async Task<object> AddTeamModule(ClaimsPrincipal user, int teamId, int moduleId)
    {
        // Same rule and order as the REST POST /api/teams/{id}/modules endpoint.
        if (!user.CanManageTeams())
            return new { error = "You do not have permission to manage teams (Admin role or above required)." };

        var team = await _store.GetTeamAsync(teamId);
        if (team is null) return new { error = "Team not found." };
        if (!user.HasCompanyAccess(team.CompanyId)) return new { error = "Team not found." };

        var module = await _store.GetModuleAsync(moduleId);
        if (module is null) return new { error = "Module not found." };
        if (module.CompanyId != team.CompanyId) return new { error = "That module does not belong to this team's company." };

        await _store.AddTeamModuleAsync(teamId, moduleId);

        return new
        {
            ok = true, teamId, teamName = team.Name, moduleId, moduleName = module.Name,
            moduleIds = await _store.GetModuleIdsForTeamAsync(teamId)
        };
    }

    [McpServerTool(Name = "remove_team_module"), Description("Admin (own company) or SuperAdmin (any company). Unshares a module from a team.")]
    public async Task<object> RemoveTeamModule(ClaimsPrincipal user, int teamId, int moduleId)
    {
        if (!user.CanManageTeams())
            return new { error = "You do not have permission to manage teams (Admin role or above required)." };

        var team = await _store.GetTeamAsync(teamId);
        if (team is null) return new { error = "Team not found." };
        if (!user.HasCompanyAccess(team.CompanyId)) return new { error = "Team not found." };

        await _store.RemoveTeamModuleAsync(teamId, moduleId);
        return new { ok = true, teamId, moduleId, moduleIds = await _store.GetModuleIdsForTeamAsync(teamId) };
    }

    private static bool IsValidEmailFormat(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        var at = email.IndexOf('@');
        return at > 0 && at < email.Length - 1 && email.IndexOf('.', at) > at + 1 && !email.Contains(' ');
    }

    [McpServerTool(Name = "update_user_email"), Description(
        "Change a user's email/login address. Omit userId (or pass your own id) to change your OWN account's " +
        "email -- that needs no special permission, same as the profile page. To change SOMEONE ELSE's email " +
        "you need Admin role or above (own company) or SuperAdmin (any company); only a SuperAdmin can change " +
        "another SuperAdmin's email.")]
    public async Task<object> UpdateUserEmail(ClaimsPrincipal user, [Description("New email address")] string newEmail, [Description("Target user id; omit to change your own account")] int? userId = null)
    {
        var myIdStr = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
        var myId = int.TryParse(myIdStr, out var parsed) ? parsed : (int?)null;
        if (myId is null) return new { error = "Could not identify the calling user." };

        var targetId = userId ?? myId.Value;
        Models.User? target;
        if (targetId == myId.Value)
        {
            target = await _store.GetUserByIdAsync(myId.Value);
        }
        else
        {
            // Same rule as the REST PUT /api/users/{id}/email endpoint.
            if (!user.CanManageUsers())
                return new { error = "You do not have permission to change other users' email (Admin role or above required)." };
            target = await _store.GetUserByIdAsync(targetId);
            if (target is not null)
            {
                if (target.Role == Roles.SuperAdmin && !user.IsSuperAdmin())
                    return new { error = "Only a SuperAdmin can change another SuperAdmin's email." };
                if (target.CompanyId is not null && !user.HasCompanyAccess(target.CompanyId.Value))
                    return new { error = "User not found." };
            }
        }
        if (target is null) return new { error = "User not found." };

        var normalized = (newEmail ?? "").Trim().ToLowerInvariant();
        if (!IsValidEmailFormat(normalized)) return new { error = "Please provide a valid email address." };
        if (normalized == target.Email.ToLowerInvariant())
            return new { error = "That is already this account's email address." };

        var existing = await _store.GetUserByEmailAsync(normalized);
        if (existing is not null && existing.Id != target.Id)
            return new { error = "An account with this email already exists." };

        var oldEmail = target.Email;
        target.Email = normalized;
        target = await _store.UpdateUserAsync(target);

        await _store.AddAuditLogAsync(new AuditLog
        {
            CompanyId = target.CompanyId, ActorEmail = EmailOf(user), ActorDisplayName = EmailOf(user), Action = "EmailChanged",
            TargetDescription = normalized,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { oldEmail, newEmail = normalized, via = "mcp" })
        });

        return UserResponse.From(target, await _store.GetTeamIdsForUserAsync(target.Id));
    }
}
