using System.Security.Cryptography;
using System.Text;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Services;

// Issues and redeems the opaque refresh tokens described in Models/AuthTokens.cs. Shared by
// both the plain REST login (AuthController) and the OAuth token endpoint (OAuthController) so
// a web-app session and an MCP-connector session behave identically and get revoked the same
// way (e.g. on password reset).
public class RefreshTokenService
{
    private readonly IDataStore _store;
    public RefreshTokenService(IDataStore store) => _store = store;

    private static string Hash(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }

    public async Task<string> IssueAsync(int userId, string clientId = "")
    {
        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await _store.CreateRefreshTokenAsync(new RefreshToken
        {
            UserId = userId,
            TokenHash = Hash(raw),
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            ClientId = clientId
        });
        return raw;
    }

    // Validates the raw token, rotates it (old one revoked, a fresh one issued), and returns
    // the user + new raw refresh token — or null if the token is missing/expired/revoked/for a
    // deactivated user. Rotation means a stolen-and-already-used-once token is a dead end.
    public async Task<(User User, string NewRefreshToken)?> RedeemAsync(string rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        var rt = await _store.GetRefreshTokenByHashAsync(Hash(rawToken));
        if (rt is null || rt.Revoked || rt.ExpiresAt < DateTime.UtcNow) return null;

        var user = await _store.GetUserByIdAsync(rt.UserId);
        if (user is null || !user.IsActive) return null;

        rt.Revoked = true;
        await _store.UpdateRefreshTokenAsync(rt);

        var newRaw = await IssueAsync(user.Id, rt.ClientId);
        return (user, newRaw);
    }
}
