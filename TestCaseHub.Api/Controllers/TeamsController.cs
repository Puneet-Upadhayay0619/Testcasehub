using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Controllers;

// Phase 8: Team CRUD + membership + module assignment, all company-scoped. Admin manages
// teams within their own company; SuperAdmin can act on any company (must pass CompanyId
// explicitly on Create/GetAll since they have no "own" company to default to).
[ApiController]
[Authorize]
[Route("api/teams")]
public class TeamsController : ControllerBase
{
    private readonly IDataStore _store;
    public TeamsController(IDataStore store) => _store = store;

    private string ActorDisplayName => User.FindFirstValue("displayName") ?? "Unknown";

    private async Task<TeamResponse> ToResponseAsync(Team t) =>
        TeamResponse.From(t, (await _store.GetTeamMembersAsync(t.Id)).Select(u => u.Id).ToList(), await _store.GetModuleIdsForTeamAsync(t.Id));

    [HttpGet]
    public async Task<ActionResult<List<TeamResponse>>> GetAll([FromQuery] int? companyId = null)
    {
        var effective = User.IsSuperAdmin() ? companyId : User.GetCompanyId();
        if (effective is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();
        var teams = await _store.GetTeamsAsync(effective.Value);
        var result = new List<TeamResponse>();
        foreach (var t in teams) result.Add(await ToResponseAsync(t));
        return result;
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<TeamResponse>> GetOne(int id)
    {
        var team = await _store.GetTeamAsync(id);
        if (team is null) return NotFound();
        if (!User.HasCompanyAccess(team.CompanyId)) return Forbid();
        return await ToResponseAsync(team);
    }

    [HttpPost]
    public async Task<ActionResult<TeamResponse>> Create(CreateTeamRequest req, [FromQuery] int? companyId = null)
    {
        if (!User.CanManageTeams()) return Forbid();
        // Accept the acting company from either the query string (SuperAdmin "entered" a
        // company via the frontend, which appends ?companyId= to every call automatically) or
        // the request body (req.CompanyId, kept for direct API/script callers) -- whichever is
        // present wins over User.GetCompanyId() for SuperAdmin, who has neither.
        var resolved = User.ResolveActingCompanyId(companyId ?? req.CompanyId);
        if (resolved is null)
            return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId= or CompanyId in the body.") : Forbid();
        var teamCompanyId = resolved.Value;
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Team name is required.");

        var team = new Team { CompanyId = teamCompanyId, Name = req.Name.Trim(), Description = req.Description ?? "", CreatedBy = ActorDisplayName };
        team = await _store.CreateTeamAsync(team);
        return await ToResponseAsync(team);
    }

    [HttpPost("{id:int}/members")]
    public async Task<ActionResult<TeamResponse>> AddMember(int id, TeamMemberRequest req)
    {
        if (!User.CanManageTeams()) return Forbid();
        var team = await _store.GetTeamAsync(id);
        if (team is null) return NotFound("Team not found.");
        if (!User.HasCompanyAccess(team.CompanyId)) return Forbid();

        var member = await _store.GetUserByIdAsync(req.UserId);
        if (member is null) return NotFound("User not found.");
        if (member.CompanyId != team.CompanyId) return BadRequest("That user does not belong to this team's company.");

        await _store.AddTeamMemberAsync(id, req.UserId);
        return await ToResponseAsync(team);
    }

    [HttpDelete("{id:int}/members/{userId:int}")]
    public async Task<ActionResult<TeamResponse>> RemoveMember(int id, int userId)
    {
        if (!User.CanManageTeams()) return Forbid();
        var team = await _store.GetTeamAsync(id);
        if (team is null) return NotFound("Team not found.");
        if (!User.HasCompanyAccess(team.CompanyId)) return Forbid();

        await _store.RemoveTeamMemberAsync(id, userId);
        return await ToResponseAsync(team);
    }

    [HttpPost("{id:int}/modules")]
    public async Task<ActionResult<TeamResponse>> AddModule(int id, TeamModuleRequest req)
    {
        if (!User.CanManageTeams()) return Forbid();
        var team = await _store.GetTeamAsync(id);
        if (team is null) return NotFound("Team not found.");
        if (!User.HasCompanyAccess(team.CompanyId)) return Forbid();

        var module = await _store.GetModuleAsync(req.ModuleId);
        if (module is null) return NotFound("Module not found.");
        if (module.CompanyId != team.CompanyId) return BadRequest("That module does not belong to this team's company.");

        // Deliberately no restriction against a module already being assigned to another team
        // -- this IS the "two teams share one module" case the whole feature was built for.
        await _store.AddTeamModuleAsync(id, req.ModuleId);
        return await ToResponseAsync(team);
    }

    [HttpDelete("{id:int}/modules/{moduleId:int}")]
    public async Task<ActionResult<TeamResponse>> RemoveModule(int id, int moduleId)
    {
        if (!User.CanManageTeams()) return Forbid();
        var team = await _store.GetTeamAsync(id);
        if (team is null) return NotFound("Team not found.");
        if (!User.HasCompanyAccess(team.CompanyId)) return Forbid();

        await _store.RemoveTeamModuleAsync(id, moduleId);
        return await ToResponseAsync(team);
    }
}
