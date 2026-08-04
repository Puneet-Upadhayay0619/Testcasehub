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

    [HttpGet]
    public async Task<ActionResult<List<UserResponse>>> GetAll()
    {
        if (!User.CanManageUsers()) return Forbid();
        var users = await _store.GetUsersAsync();
        return users.OrderBy(u => u.Id).Select(UserResponse.From).ToList();
    }

    [HttpPut("{id:int}/access")]
    public async Task<ActionResult<UserResponse>> UpdateAccess(int id, UpdateUserAccessRequest req)
    {
        if (!User.CanManageUsers()) return Forbid();
        if (!Roles.IsValid(req.Role)) return BadRequest("Role must be one of Admin, Lead, Contributor, Viewer.");

        var target = await _store.GetUserByIdAsync(id);
        if (target is null) return NotFound();

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
    public async Task<ActionResult<List<AuditLogResponse>>> GetAuditLog()
    {
        if (!User.CanManageUsers()) return Forbid();
        var logs = await _store.GetAuditLogsAsync();
        return logs.Select(l => new AuditLogResponse(l.Id, l.ActorEmail, l.ActorDisplayName, l.Action, l.TargetDescription, l.DetailsJson, l.OccurredAt)).ToList();
    }
}
