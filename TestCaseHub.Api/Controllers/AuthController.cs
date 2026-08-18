using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IDataStore _store;
    private readonly JwtService _jwt;
    private readonly RefreshTokenService _refresh;
    private readonly IEmailSender _email;
    public AuthController(IDataStore store, JwtService jwt, RefreshTokenService refresh, IEmailSender email)
    { _store = store; _jwt = jwt; _refresh = refresh; _email = email; }

    private static string HashToken(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private async Task<string?> CompanyNameForAsync(User user)
    {
        if (user.CompanyId is null) return null;
        var company = await _store.GetCompanyAsync(user.CompanyId.Value);
        return company?.Name;
    }

    [HttpPost("register")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password) || string.IsNullOrWhiteSpace(req.DisplayName))
            return BadRequest("Email, password and display name are all required.");
        if (req.Password.Length < 8)
            return BadRequest("Password must be at least 8 characters.");

        var normalizedEmail = req.Email.Trim().ToLowerInvariant();
        if (await _store.GetUserByEmailAsync(normalizedEmail) is not null)
            return Conflict("An account with this email already exists.");

        // Bootstrap rule (agreed in planning, extended Phase 8): the very first account ever
        // created on a fresh deployment becomes SuperAdmin automatically (nobody could have
        // created a company or issued a referral code before this moment — there's nothing
        // else it could be). Every account after that MUST come through one of two codes:
        //   - a Company-Admin referral code (CompanyAdminInvite, SuperAdmin-issued) -> becomes
        //     that brand-new company's first Admin.
        //   - an ordinary invite link (InviteLink, Admin-issued) -> joins that Admin's existing
        //     company as Viewer, exactly as before Phase 8.
        // Open self-registration is closed the moment a first (SuperAdmin) user exists.
        var isFirstUser = await _store.CountUsersAsync() == 0;
        InviteLink? invite = null;
        CompanyAdminInvite? adminInvite = null;
        int? companyId = null;
        var role = Models.Roles.Viewer;

        if (isFirstUser)
        {
            role = Models.Roles.SuperAdmin; // companyId stays null -- SuperAdmin spans every company.
        }
        else
        {
            var code = (req.InviteCode ?? "").Trim();
            if (string.IsNullOrEmpty(code))
                return BadRequest("An invite code is required to register (ask an Admin, or your SuperAdmin, for one).");

            invite = await _store.GetInviteLinkByCodeAsync(code);
            if (invite is not null && invite.IsUsable)
            {
                companyId = invite.CompanyId;
                role = Models.Roles.Viewer;
            }
            else
            {
                invite = null;
                adminInvite = await _store.GetCompanyAdminInviteByCodeAsync(code);
                if (adminInvite is null || !adminInvite.IsUsable)
                    return BadRequest("This invite code is invalid, expired, already used up, or has been revoked.");
                companyId = adminInvite.CompanyId;
                role = Models.Roles.Admin;
            }
        }

        var user = new User
        {
            Email = normalizedEmail,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
            DisplayName = req.DisplayName.Trim(),
            Role = role,
            CompanyId = companyId
        };
        user = await _store.CreateUserAsync(user);

        if (invite is not null)
        {
            invite.UsedCount += 1;
            await _store.UpdateInviteLinkAsync(invite);
        }
        if (adminInvite is not null)
        {
            adminInvite.UsedCount += 1;
            await _store.UpdateCompanyAdminInviteAsync(adminInvite);
        }

        await _store.AddAuditLogAsync(new AuditLog
        {
            CompanyId = user.CompanyId,
            ActorEmail = user.Email, ActorDisplayName = user.DisplayName,
            Action = isFirstUser ? "BootstrapSuperAdminCreated" : adminInvite is not null ? "CompanyAdminRegistered" : "UserRegisteredViaInvite",
            TargetDescription = user.Email,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { user.Role, user.CompanyId, inviteCode = invite?.Code ?? adminInvite?.Code })
        });

        var refreshToken = await _refresh.IssueAsync(user.Id);
        var teamIds = await _store.GetTeamIdsForUserAsync(user.Id);
        return Ok(new AuthResponse(_jwt.GenerateToken(user, teamIds), user.Email, user.DisplayName, user.Role, refreshToken, user.CompanyId, teamIds, await CompanyNameForAsync(user)));
    }

    [HttpPost("login")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest req)
    {
        var normalizedEmail = (req.Email ?? "").Trim().ToLowerInvariant();
        var user = await _store.GetUserByEmailAsync(normalizedEmail);
        if (user is null || !BCrypt.Net.BCrypt.Verify(req.Password ?? "", user.PasswordHash))
            return Unauthorized("Invalid email or password.");

        // Deactivation = login-block only (explicit decision) — everything else about the
        // account (history/attribution) is left exactly as-is, so we don't touch or hide
        // anything here, we just refuse to hand out a new token.
        if (!user.IsActive)
            return Unauthorized("This account has been deactivated. Contact an Admin.");

        var refreshToken = await _refresh.IssueAsync(user.Id);
        var teamIds = await _store.GetTeamIdsForUserAsync(user.Id);
        return Ok(new AuthResponse(_jwt.GenerateToken(user, teamIds), user.Email, user.DisplayName, user.Role, refreshToken, user.CompanyId, teamIds, await CompanyNameForAsync(user)));
    }

    // Exchanges a still-valid refresh token for a brand-new access token (JWT) + a rotated
    // refresh token. This is what lets a web-app session or MCP connector stay logged in for
    // weeks without ever re-entering id/password, while the actual JWT in circulation at any
    // moment is only ever valid for an hour.
    [HttpPost("refresh")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<AuthResponse>> Refresh(RefreshRequest req)
    {
        var result = await _refresh.RedeemAsync(req.RefreshToken);
        if (result is null) return Unauthorized("Refresh token is invalid, expired, or already used — please log in again.");
        var (user, newRefreshToken) = result.Value;
        var teamIds = await _store.GetTeamIdsForUserAsync(user.Id);
        return Ok(new AuthResponse(_jwt.GenerateToken(user, teamIds), user.Email, user.DisplayName, user.Role, newRefreshToken, user.CompanyId, teamIds, await CompanyNameForAsync(user)));
    }

    // Deliberately returns the exact same generic response whether or not the email exists —
    // otherwise this endpoint could be used to enumerate registered accounts.
    [HttpPost("forgot-password")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult> ForgotPassword(ForgotPasswordRequest req)
    {
        var normalizedEmail = (req.Email ?? "").Trim().ToLowerInvariant();
        var user = await _store.GetUserByEmailAsync(normalizedEmail);
        if (user is not null)
        {
            var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            await _store.CreatePasswordResetTokenAsync(new PasswordResetToken
            {
                UserId = user.Id, TokenHash = HashToken(raw), ExpiresAt = DateTime.UtcNow.AddMinutes(30)
            });
            var baseUrl = $"{Request.Scheme}://{Request.Host}/reset-password";
            await _email.SendPasswordResetAsync(user.Email, raw, baseUrl);
        }
        return Ok(new { message = "If that email has an account, a password reset link has been sent." });
    }

    [HttpPost("reset-password")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult> ResetPassword(ResetPasswordRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 8)
            return BadRequest("New password must be at least 8 characters.");

        var reset = await _store.GetPasswordResetTokenByHashAsync(HashToken(req.Token ?? ""));
        if (reset is null || reset.Used || reset.ExpiresAt < DateTime.UtcNow)
            return BadRequest("This reset link is invalid, expired, or already used.");

        var user = await _store.GetUserByIdAsync(reset.UserId);
        if (user is null) return BadRequest("Account no longer exists.");

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
        await _store.UpdateUserAsync(user);

        reset.Used = true;
        await _store.UpdatePasswordResetTokenAsync(reset);

        // Security best practice: a password reset invalidates every existing session
        // (web app AND any MCP connector) — force everyone to log back in with the new
        // password rather than leaving old refresh tokens usable.
        await _store.RevokeAllRefreshTokensForUserAsync(user.Id);

        await _store.AddAuditLogAsync(new AuditLog
        {
            ActorEmail = user.Email, ActorDisplayName = user.DisplayName, Action = "PasswordReset",
            TargetDescription = user.Email, DetailsJson = "{}"
        });

        return Ok(new { message = "Password has been reset. Please log in again." });
    }
}
