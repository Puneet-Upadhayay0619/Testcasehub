using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/modules")]
public class ModulesController : ControllerBase
{
    private readonly IDataStore _store;
    public ModulesController(IDataStore store) => _store = store;

    // Phase 8: SuperAdmin has no company of their own, so they must say which company's
    // modules they want to look at (companyId query param) — "company-wise, never mixed", per
    // the agreed design. Everyone else ignores this param entirely and always sees only their
    // own company; they can't use it to peek into another company's data even if they pass one.
    [HttpGet]
    public async Task<ActionResult<List<ModuleResponse>>> GetAll(int? companyId = null, int? teamId = null)
    {
        int effectiveCompanyId;
        if (User.IsSuperAdmin())
        {
            if (companyId is null) return BadRequest("SuperAdmin must specify ?companyId= to view a company's modules.");
            effectiveCompanyId = companyId.Value;
        }
        else
        {
            var myCompany = User.GetCompanyId();
            if (myCompany is null) return Forbid();
            effectiveCompanyId = myCompany.Value;
        }

        var modules = (await _store.GetModulesAsync()).Where(m => m.CompanyId == effectiveCompanyId).ToList();

        // Team-based module visibility (Phase 8). Two very different rules depending on role:
        //
        // Admin/SuperAdmin: NOT restricted to their own team memberships at all -- they can
        // browse modules by ANY team in the company via ?teamId= (a management/browsing view,
        // not an access restriction), or see everything via ?teamId=0 / omitting it entirely.
        // Admin additionally defaults to their own first-joined team when no teamId is given
        // AND they happen to personally be on one (matches what's normally most relevant to
        // them); SuperAdmin has no personal team membership, so their default with no teamId
        // is always "every module" -- there's nothing to default to.
        //
        // Lead/Contributor/Viewer: ALWAYS restricted to exactly ONE of THEIR OWN teams at a
        // time -- default their first (earliest-joined) team, or whichever of their OWN teams
        // they explicitly pass via ?teamId= -- never a union of every team they're in, and
        // never allowed to browse a team they don't belong to or see "everything".
        if (User.IsAtLeast(Roles.Admin))
        {
            if (teamId.HasValue && teamId != 0)
            {
                var companyTeams = await _store.GetTeamsAsync(effectiveCompanyId);
                if (companyTeams.Any(t => t.Id == teamId.Value))
                {
                    var visible = new List<Module>();
                    foreach (var m in modules)
                    {
                        var moduleTeamIds = await _store.GetTeamIdsForModuleAsync(m.Id);
                        if (moduleTeamIds.Contains(teamId.Value)) visible.Add(m);
                    }
                    modules = visible;
                }
                // else: teamId doesn't belong to this company -- ignore it, fall through to
                // the full list rather than erroring.
            }
            else if (!teamId.HasValue && !User.IsSuperAdmin())
            {
                var myTeamIds = User.GetTeamIds();
                if (myTeamIds.Count > 0)
                {
                    var scopeTeamId = myTeamIds[0];
                    var visible = new List<Module>();
                    foreach (var m in modules)
                    {
                        var moduleTeamIds = await _store.GetTeamIdsForModuleAsync(m.Id);
                        if (moduleTeamIds.Contains(scopeTeamId)) visible.Add(m);
                    }
                    modules = visible;
                }
            }
            // teamId == 0, or SuperAdmin with no teamId at all -> leave `modules` as the full
            // company list ("All modules").
        }
        else
        {
            var myTeamIds = User.GetTeamIds();
            int? scopeTeamId = (teamId.HasValue && myTeamIds.Contains(teamId.Value))
                ? teamId.Value
                : (myTeamIds.Count > 0 ? myTeamIds[0] : (int?)null);

            var visible = new List<Module>();
            if (scopeTeamId.HasValue)
            {
                foreach (var m in modules)
                {
                    var moduleTeamIds = await _store.GetTeamIdsForModuleAsync(m.Id);
                    if (moduleTeamIds.Contains(scopeTeamId.Value)) visible.Add(m);
                }
            }
            modules = visible;
        }

        var counts = await _store.GetTestCaseCountsByModuleAsync();
        return modules.Select(m => new ModuleResponse(
            m.Id, m.Name, m.Code, m.Description, m.Owner, m.Status, m.CreatedAt,
            counts.TryGetValue(m.Id, out var c) ? c : 0
        )).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<ModuleResponse>> Create(ModuleCreateRequest req, [FromQuery] int? companyId = null)
    {
        // Agreed in planning: module creation is Contributor and above (Viewer cannot).
        if (!User.CanCreateModule())
            return Forbid();

        var resolvedCompanyId = User.ResolveActingCompanyId(companyId);
        if (resolvedCompanyId is null)
            return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId= to create a module inside a company.") : Forbid();
        companyId = resolvedCompanyId;

        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Code))
            return BadRequest("Module name and code are required.");

        var code = req.Code.Trim().ToUpperInvariant();
        if (await _store.ModuleCodeExistsAsync(companyId.Value, code))
            return Conflict($"A module with code '{code}' already exists.");

        var module = new Module
        {
            CompanyId = companyId.Value,
            Name = req.Name.Trim(), Code = code, Description = req.Description ?? "",
            Owner = req.Owner ?? "", Status = string.IsNullOrWhiteSpace(req.Status) ? "Active" : req.Status
        };
        module = await _store.CreateModuleAsync(module);
        return Ok(new ModuleResponse(module.Id, module.Name, module.Code, module.Description, module.Owner, module.Status, module.CreatedAt, 0));
    }

    [HttpPost("{moduleId:int}/task-links")]
    public async Task<ActionResult<TaskLinkResponse>> AddTaskLink(int moduleId, TaskLinkCreateRequest req)
    {
        if (!User.CanEditTaskLinks()) return Forbid();

        var module = await _store.GetModuleAsync(moduleId);
        if (module is null) return NotFound("Module not found.");
        if (!User.HasCompanyAccess(module.CompanyId)) return Forbid();
        if (string.IsNullOrWhiteSpace(req.AdoTaskId)) return BadRequest("Task ID is required.");

        var link = new TaskLink
        {
            ModuleId = moduleId, Layer = req.Layer, AdoProject = req.AdoProject,
            AdoTaskId = req.AdoTaskId.Trim(), AdoTaskTitle = req.AdoTaskTitle ?? "", AdoTaskUrl = req.AdoTaskUrl ?? ""
        };
        link = await _store.CreateTaskLinkAsync(link);
        return Ok(new TaskLinkResponse(link.Id, link.ModuleId, link.Layer, link.AdoProject, link.AdoTaskId, link.AdoTaskTitle, link.AdoTaskUrl, link.LinkedAt));
    }

    [HttpGet("{moduleId:int}/task-links")]
    public async Task<ActionResult<List<TaskLinkResponse>>> GetTaskLinks(int moduleId)
    {
        var module = await _store.GetModuleAsync(moduleId);
        if (module is null) return NotFound("Module not found.");
        if (!User.HasCompanyAccess(module.CompanyId)) return Forbid();

        var links = await _store.GetTaskLinksAsync(moduleId);
        return links.Select(l => new TaskLinkResponse(l.Id, l.ModuleId, l.Layer, l.AdoProject, l.AdoTaskId, l.AdoTaskTitle, l.AdoTaskUrl, l.LinkedAt)).ToList();
    }
}
