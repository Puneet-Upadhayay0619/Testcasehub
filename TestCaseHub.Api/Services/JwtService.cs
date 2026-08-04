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

    public string GenerateToken(User user)
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
            new Claim("moduleScope", string.Join(",", user.ModuleScope))
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
