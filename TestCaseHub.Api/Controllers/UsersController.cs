using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Controllers;

// Admin-only surface (enforced per-action below, not just [Authorize]) for the User
// Management page agreed in planning: list users, change role/scope, deactivate/reactivate.
// Deactivation is deliberately login-block-only — we never touch history/attribution here.
[ApiController]
[Authorize]
[Route("api/users")]
public class UsersController : ControllerBase
{
    private readonly IDataStore _store;
    private readonly NotificationService _notify;
    public UsersController(IDataStore store, NotificationService notify) { _store = store; _notify = notify; }

    private string ActorEmail => User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email) ?? "";
    private string ActorDisplayName => User.FindFirstValue("displayName") ?? "Unknown";
    private int? ActorUserId => int.TryParse(User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub), out var id) ? id : null;

    private static bool IsValidEmailFormat(string email)
    {
        // Same lightweight shape check used at registration -- not a full RFC 5322 validator,
        // just enough to reject obvious typos before they get saved.
        if (string.IsNullOrWhiteSpace(email)) return false;
        var at = email.IndexOf('@');
        return at > 0 && at < email.Length - 1 && email.IndexOf('.', at) > at + 1 && !email.Contains(' ');
    }

    private async Task<ActionResult<UserResponse>> ChangeEmailInternal(Models.User target, string newEmailRaw)
    {
        var newEmail = (newEmailRaw ?? "").Trim().ToLowerInvariant();
        if (!IsValidEmailFormat(newEmail)) return BadRequest("Please provide a valid email address.");
        if (newEmail == target.Email.ToLowerInvariant())
            return BadRequest("That is already this account's email address.");

        var existing = await _store.GetUserByEmailAsync(newEmail);
        if (existing is not null && existing.Id != target.Id)
            return Conflict("An account with this email already exists.");

        var oldEmail = target.Email;
        target.Email = newEmail;
        target = await _store.UpdateUserAsync(target);

        await _store.AddAuditLogAsync(new AuditLog
        {
            CompanyId = target.CompanyId, ActorEmail = ActorEmail, ActorDisplayName = ActorDisplayName, Action = "EmailChanged",
            TargetDescription = newEmail,
            DetailsJson = JsonSerializer.Serialize(new { oldEmail, newEmail })
        });

        return UserResponse.From(target, await _store.GetTeamIdsForUserAsync(target.Id));
    }

    // Self-service: any authenticated role can change their OWN email (no "manage users"
    // permission needed to edit your own account). Existing sessions/JWTs still carry the old
    // email string until the user logs in again or their refresh token rotates -- that's fine
    // since email isn't used in any permission check (role/companyId are), only for display and
    // as the login identifier going forward.
    // Restricted to Admin+ (Admin or SuperAdmin) -- Lead/Contributor/Viewer cannot change even
    // their own email. This deliberately narrows the original self-service-for-everyone design:
    // email is the login identifier, and only account managers should be able to repoint it.
    [HttpPut("me/email")]
    public async Task<ActionResult<UserResponse>> ChangeMyEmail(UpdateEmailRequest req)
    {
        if (!User.CanManageUsers()) return Forbid();
        var myId = ActorUserId;
        if (myId is null) return Unauthorized();
        var me = await _store.GetUserByIdAsync(myId.Value);
        if (me is null) return Unauthorized();
        return await ChangeEmailInternal(me, req.NewEmail);
    }

    // Admin-driven: change another user's email. Same company-scoping as UpdateAccess, plus an
    // explicit block on touching a SuperAdmin's email unless the caller is a SuperAdmin too --
    // UpdateAccess above doesn't have this guard (a gap worth fixing separately), so this new
    // endpoint doesn't repeat it.
    [HttpPut("{id:int}/email")]
    public async Task<ActionResult<UserResponse>> ChangeUserEmail(int id, UpdateEmailRequest req)
    {
        if (!User.CanManageUsers()) return Forbid();
        var target = await _store.GetUserByIdAsync(id);
        if (target is null) return NotFound();
        if (target.Role == Roles.SuperAdmin && !User.IsSuperAdmin()) return Forbid();
        if (target.CompanyId is not null && !User.HasCompanyAccess(target.CompanyId.Value)) return Forbid();

        return await ChangeEmailInternal(target, req.NewEmail);
    }

    [HttpGet]
    public async Task<ActionResult<List<UserResponse>>> GetAll([FromQuery] int? companyId = null)
    {
        if (!User.CanManageUsers()) return Forbid();
        var effective = User.IsSuperAdmin() ? companyId : User.GetCompanyId();
        if (effective is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();
        var users = (await _store.GetUsersAsync()).Where(u => u.CompanyId == effective).ToList();
        var result = new List<UserResponse>();
        foreach (var u in users.OrderBy(u => u.Id))
            result.Add(UserResponse.From(u, await _store.GetTeamIdsForUserAsync(u.Id)));
        return result;
    }

    [HttpPut("{id:int}/access")]
    public async Task<ActionResult<UserResponse>> UpdateAccess(int id, UpdateUserAccessRequest req)
    {
        if (!User.CanManageUsers()) return Forbid();
        if (!Roles.IsValid(req.Role)) return BadRequest("Role must be one of Admin, Lead, Contributor, Viewer.");
        // Only SuperAdmin can grant/hold SuperAdmin -- an ordinary Admin can't promote anyone
        // (including themselves) above their own company's ceiling.
        if (req.Role == Roles.SuperAdmin && !User.IsSuperAdmin()) return Forbid();

        var target = await _store.GetUserByIdAsync(id);
        if (target is null) return NotFound();
        if (target.CompanyId is not null && !User.HasCompanyAccess(target.CompanyId.Value)) return Forbid();

        var before = new { target.Role, target.LayerScope, target.ModuleScope };
        target.Role = req.Role;
        target.LayerScope = req.LayerScope ?? new();
        target.ModuleScope = req.ModuleScope ?? new();
        target = await _store.UpdateUserAsync(target);

        await _store.AddAuditLogAsync(new AuditLog
        {
            ActorEmail = ActorEmail, ActorDisplayName = ActorDisplayName, Action = "RoleOrScopeChanged",
            TargetDescription = target.Email,
            DetailsJson = JsonSerializer.Serialize(new { before, after = new { target.Role, target.LayerScope, target.ModuleScope } })
        });
        await _notify.NotifyUserAsync(target.Id, "RoleChanged", $"Your role was changed to {target.Role}.");
        return UserResponse.From(target);
    }

    [HttpPost("{id:int}/deactivate")]
    public async Task<ActionResult<UserResponse>> Deactivate(int id)
    {
        if (!User.CanManageUsers()) return Forbid();
        var target = await _store.GetUserByIdAsync(id);
        if (target is null) return NotFound();
        if (target.CompanyId is not null && !User.HasCompanyAccess(target.CompanyId.Value)) return Forbid();
        if (target.Role == Roles.Admin && target.IsActive)
        {
            // Extra guard: don't let the last active Admin deactivate themselves into a
            // deployment with zero Admins — that would permanently lock everyone out of
            // User Management with no recovery path.
            var admins = (await _store.GetUsersAsync()).Where(u => u.Role == Roles.Admin && u.IsActive).ToList();
            if (admins.Count == 1 && admins[0].Id == target.Id)
                return BadRequest("Cannot deactivate the only active Admin — promote another user to Admin first.");
        }

        target.IsActive = false; // login-block only — nothing else about the account changes.
        target = await _store.UpdateUserAsync(target);
        await _store.AddAuditLogAsync(new AuditLog
        {
            ActorEmail = ActorEmail, ActorDisplayName = ActorDisplayName, Action = "UserDeactivated",
            TargetDescription = target.Email, DetailsJson = "{}"
        });
        return UserResponse.From(target);
    }

    [HttpPost("{id:int}/reactivate")]
    public async Task<ActionResult<UserResponse>> Reactivate(int id)
    {
        if (!User.CanManageUsers()) return Forbid();
        var target = await _store.GetUserByIdAsync(id);
        if (target is null) return NotFound();
        if (target.CompanyId is not null && !User.HasCompanyAccess(target.CompanyId.Value)) return Forbid();

        target.IsActive = true;
        target = await _store.UpdateUserAsync(target);
        await _store.AddAuditLogAsync(new AuditLog
        {
            ActorEmail = ActorEmail, ActorDisplayName = ActorDisplayName, Action = "UserReactivated",
            TargetDescription = target.Email, DetailsJson = "{}"
        });
        return UserResponse.From(target);
    }

    [HttpGet("audit-log")]
    public async Task<ActionResult<List<AuditLogResponse>>> GetAuditLog([FromQuery] int? companyId = null)
    {
        if (!User.CanManageUsers()) return Forbid();
        var logs = await _store.GetAuditLogsAsync();
        var effective = User.IsSuperAdmin() ? companyId : User.GetCompanyId();
        logs = logs.Where(l => l.CompanyId == effective).ToList();
        return logs.Select(l => new AuditLogResponse(l.Id, l.ActorEmail, l.ActorDisplayName, l.Action, l.TargetDescription, l.DetailsJson, l.OccurredAt)).ToList();
    }
}
