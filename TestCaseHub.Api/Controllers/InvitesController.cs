using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Controllers;

// Admin-only: generate/revoke/list invite links. The actual redemption happens inside
// POST /api/auth/register (public, no auth) via the InviteCode field — see AuthController.
[ApiController]
[Authorize]
[Route("api/invites")]
public class InvitesController : ControllerBase
{
    private readonly IDataStore _store;
    public InvitesController(IDataStore store) => _store = store;

    private string ActorEmail => User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email) ?? "";
    private string ActorDisplayName => User.FindFirstValue("displayName") ?? "Unknown";

    private static string GenerateCode()
    {
        // 15 base32-ish chars from a cryptographically random source — short enough to paste
        // into a chat message, long enough not to be guessable.
        var bytes = RandomNumberGenerator.GetBytes(10);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    [HttpGet]
    public async Task<ActionResult<List<InviteLinkResponse>>> GetAll([FromQuery] int? companyId = null)
    {
        if (!User.CanManageUsers()) return Forbid();
        var effective = User.IsSuperAdmin() ? companyId : User.GetCompanyId();
        if (effective is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();
        var invites = await _store.GetInviteLinksAsync();
        return invites.Where(i => i.CompanyId == effective).Select(InviteLinkResponse.From).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<InviteLinkResponse>> Create(CreateInviteRequest req, [FromQuery] int? companyId = null)
    {
        if (!User.CanManageUsers()) return Forbid();
        companyId = User.ResolveActingCompanyId(companyId);
        if (companyId is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();
        var maxUses = req.MaxUses <= 0 ? 1 : req.MaxUses;
        var expiresInDays = req.ExpiresInDays <= 0 ? 7 : req.ExpiresInDays;

        var invite = new InviteLink
        {
            CompanyId = companyId.Value, Code = GenerateCode(), MaxUses = maxUses,
            ExpiresAt = DateTime.UtcNow.AddDays(expiresInDays),
            CreatedByEmail = ActorEmail
        };
        invite = await _store.CreateInviteLinkAsync(invite);

        await _store.AddAuditLogAsync(new AuditLog
        {
            ActorEmail = ActorEmail, ActorDisplayName = ActorDisplayName, Action = "InviteCreated",
            TargetDescription = invite.Code,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { invite.MaxUses, invite.ExpiresAt })
        });
        return InviteLinkResponse.From(invite);
    }

    [HttpPost("{id:int}/revoke")]
    public async Task<ActionResult<InviteLinkResponse>> Revoke(int id)
    {
        if (!User.CanManageUsers()) return Forbid();
        var invites = await _store.GetInviteLinksAsync();
        var invite = invites.FirstOrDefault(i => i.Id == id);
        if (invite is null) return NotFound();
        if (!User.HasCompanyAccess(invite.CompanyId)) return Forbid();

        invite.Revoked = true;
        invite = await _store.UpdateInviteLinkAsync(invite);

        await _store.AddAuditLogAsync(new AuditLog
        {
            ActorEmail = ActorEmail, ActorDisplayName = ActorDisplayName, Action = "InviteRevoked",
            TargetDescription = invite.Code, DetailsJson = "{}"
        });
        return InviteLinkResponse.From(invite);
    }
}
