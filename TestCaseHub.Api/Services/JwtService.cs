using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using TestCaseHub.Api.Models;

namespace TestCaseHub.Api.Services;

public class JwtService
{
    private readonly IConfiguration _config;
    public JwtService(IConfiguration config) => _config = config;

    // teamIds: the Team ids this user currently belongs to (Phase 8) — embedded at token-issue
    // time, same tradeoff as role/companyId: team membership doesn't change often enough to
    // justify a DB round trip on every single request just to compute "which modules can this
    // person see". Caller (AuthController/OAuthController) is responsible for fetching this
    // from IDataStore.GetTeamIdsForUserAsync before calling GenerateToken.
    public string GenerateToken(User user, List<int>? teamIds = null)
    {
        var jwtSection = _config.GetSection("Jwt");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSection["Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("displayName", user.DisplayName),
            // RBAC claims: read by Services/Permissions.cs on every request, identically for
            // REST controllers and MCP tools, since both authenticate with this same JWT.
            new Claim("role", user.Role),
            new Claim("layerScope", string.Join(",", user.LayerScope)),
            new Claim("moduleScope", string.Join(",", user.ModuleScope)),
            // Phase 8: multi-company + teams. companyId is "" for SuperAdmin (no single
            // company) — Permissions.GetCompanyId() returns null when this claim isn't a
            // parseable int, which is exactly the "spans every company" signal SuperAdmin needs.
            new Claim("companyId", user.CompanyId?.ToString() ?? ""),
            new Claim("teamIds", string.Join(",", teamIds ?? new List<int>()))
        };

        // Deliberately short-lived now that refresh tokens exist (Phase 3) — a leaked/stale
        // access token is only useful for an hour, and normal usage never notices because
        // RefreshTokenService quietly mints a new one behind the scenes.
        var token = new JwtSecurityToken(
            issuer: jwtSection["Issuer"],
            audience: jwtSection["Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds
        );
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
