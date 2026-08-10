using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Controllers;

// SuperAdmin-only (Phase 8): creating companies and issuing each company's first Company-Admin
// referral code. This is deliberately a SEPARATE code namespace from InviteLink
// (InvitesController) -- an InviteLink is how an already-provisioned company's Admin invites
// MORE users into that SAME company; a CompanyAdminInvite is how a brand-new company gets its
// very first user at all, and that first user always lands as Admin, never Viewer.
[ApiController]
[Authorize]
[Route("api/companies")]
public class CompaniesController : ControllerBase
{
    private readonly IDataStore _store;
    public CompaniesController(IDataStore store) => _store = store;

    private string ActorEmail => User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email) ?? "";

    private static string GenerateCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(10);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    [HttpGet]
    public async Task<ActionResult<List<CompanyResponse>>> GetAll()
    {
        if (!User.CanManageCompanies()) return Forbid();
        return (await _store.GetCompaniesAsync()).Select(CompanyResponse.From).ToList();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<CompanyResponse>> GetOne(int id)
    {
        if (!User.CanManageCompanies()) return Forbid();
        var c = await _store.GetCompanyAsync(id);
        return c is null ? NotFound() : CompanyResponse.From(c);
    }

    [HttpPost]
    public async Task<ActionResult<CompanyResponse>> Create(CreateCompanyRequest req)
    {
        if (!User.CanManageCompanies()) return Forbid();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Company name is required.");

        var company = new Company { Name = req.Name.Trim(), CreatedBy = ActorEmail };
        company = await _store.CreateCompanyAsync(company);

        await _store.AddAuditLogAsync(new AuditLog
        {
            CompanyId = null, ActorEmail = ActorEmail, ActorDisplayName = ActorEmail, Action = "CompanyCreated",
            TargetDescription = company.Name, DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { company.Id })
        });
        return CompanyResponse.From(company);
    }

    // Issues a one-time (or few-time) referral code for THIS company's first Admin to
    // self-register with -- see AuthController.Register, which checks this code namespace
    // whenever an InviteLink lookup misses.
    [HttpPost("{id:int}/admin-invites")]
    public async Task<ActionResult<CompanyAdminInviteResponse>> CreateAdminInvite(int id, CreateCompanyAdminInviteRequest req)
    {
        if (!User.CanManageCompanies()) return Forbid();
        var company = await _store.GetCompanyAsync(id);
        if (company is null) return NotFound("Company not found.");

        var maxUses = req.MaxUses <= 0 ? 1 : req.MaxUses;
        var expiresInDays = req.ExpiresInDays <= 0 ? 14 : req.ExpiresInDays;
        var invite = new CompanyAdminInvite
        {
            CompanyId = id, Code = GenerateCode(), MaxUses = maxUses,
            ExpiresAt = DateTime.UtcNow.AddDays(expiresInDays), CreatedByEmail = ActorEmail
        };
        invite = await _store.CreateCompanyAdminInviteAsync(invite);

        await _store.AddAuditLogAsync(new AuditLog
        {
            CompanyId = id, ActorEmail = ActorEmail, ActorDisplayName = ActorEmail, Action = "CompanyAdminInviteCreated",
            TargetDescription = company.Name, DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { invite.Code, invite.MaxUses, invite.ExpiresAt })
        });
        return CompanyAdminInviteResponse.From(invite);
    }

    [HttpGet("{id:int}/admin-invites")]
    public async Task<ActionResult<List<CompanyAdminInviteResponse>>> GetAdminInvites(int id)
    {
        if (!User.CanManageCompanies()) return Forbid();
        return (await _store.GetCompanyAdminInvitesAsync(id)).Select(CompanyAdminInviteResponse.From).ToList();
    }

    [HttpPost("admin-invites/{inviteId:int}/revoke")]
    public async Task<ActionResult<CompanyAdminInviteResponse>> RevokeAdminInvite(int inviteId)
    {
        if (!User.CanManageCompanies()) return Forbid();
        var invites = await _store.GetCompanyAdminInvitesAsync(null);
        var invite = invites.FirstOrDefault(i => i.Id == inviteId);
        if (invite is null) return NotFound();
        invite.Revoked = true;
        invite = await _store.UpdateCompanyAdminInviteAsync(invite);
        return CompanyAdminInviteResponse.From(invite);
    }

    // One-time-migration-style bulk move: every user whose email ends with @<domain>,
    // REGARDLESS of which company they're currently in, gets moved into this company. Built
    // for exactly the "our existing users are all sitting in the auto-created Default Company
    // -- split them into real companies by email domain" case. Deliberately does NOT touch
    // modules/test cases/teams the user already created -- those stay wherever they were, only
    // the USER's own company membership moves. SuperAdmin-only, same bar as everything else on
    // this controller.
    [HttpPost("{id:int}/assign-users-by-domain")]
    public async Task<ActionResult<AssignUsersByDomainResult>> AssignUsersByDomain(int id, AssignUsersByDomainRequest req)
    {
        if (!User.CanManageCompanies()) return Forbid();
        var company = await _store.GetCompanyAsync(id);
        if (company is null) return NotFound("Company not found.");

        var domain = (req.EmailDomain ?? "").Trim().TrimStart('@').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(domain)) return BadRequest("emailDomain is required, e.g. \"gmail.com\".");

        var matched = (await _store.GetUsersAsync())
            .Where(u => u.Role != Roles.SuperAdmin && u.Email.ToLowerInvariant().EndsWith("@" + domain))
            .ToList();

        foreach (var u in matched)
        {
            u.CompanyId = id;
            await _store.UpdateUserAsync(u);
        }

        await _store.AddAuditLogAsync(new AuditLog
        {
            CompanyId = id, ActorEmail = User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email) ?? "",
            ActorDisplayName = "SuperAdmin", Action = "UsersBulkAssignedByDomain",
            TargetDescription = company.Name,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { domain, matchedCount = matched.Count, emails = matched.Select(u => u.Email) })
        });

        return new AssignUsersByDomainResult(matched.Count, matched.Select(u => u.Email).ToList());
    }
}
