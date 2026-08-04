using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Controllers;

// A minimal OAuth 2.1 (authorization code + PKCE, RFC 7636) authorization server, plus dynamic
// client registration (RFC 7591) and the discovery documents (RFC 8414 / RFC 9728) MCP clients
// like Claude use to find it automatically. This exists for exactly one reason: so that adding
// this MCP server as a custom connector shows Claude's normal "log in" flow (email + password,
// the same account as the web app) instead of requiring anyone to copy a JWT into a config file.
[ApiController]
public class OAuthController : ControllerBase
{
    private readonly IDataStore _store;
    private readonly JwtService _jwt;
    private readonly OAuthStore _oauth;
    private readonly RefreshTokenService _refresh;
    public OAuthController(IDataStore store, JwtService jwt, OAuthStore oauth, RefreshTokenService refresh)
    { _store = store; _jwt = jwt; _oauth = oauth; _refresh = refresh; }

    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    [HttpGet("/.well-known/oauth-authorization-server")]
    public IActionResult AuthServerMetadata()
    {
        var b = BaseUrl;
        return Ok(new
        {
            issuer = b,
            authorization_endpoint = b + "/authorize",
            token_endpoint = b + "/token",
            registration_endpoint = b + "/register",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "refresh_token" },
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "none" }
        });
    }

    [HttpGet("/.well-known/oauth-protected-resource")]
    public IActionResult ProtectedResourceMetadata()
    {
        var b = BaseUrl;
        return Ok(new
        {
            resource = b + "/mcp",
            authorization_servers = new[] { b }
        });
    }

    public record RegisterClientRequest(List<string>? redirect_uris);

    [HttpPost("/register")]
    public IActionResult RegisterClient([FromBody] RegisterClientRequest req)
    {
        var redirectUris = req?.redirect_uris ?? new List<string>();
        var client = _oauth.RegisterClient(redirectUris);
        return Ok(new
        {
            client_id = client.ClientId,
            redirect_uris = client.RedirectUris,
            token_endpoint_auth_method = "none",
            grant_types = new[] { "authorization_code" },
            response_types = new[] { "code" }
        });
    }

    private static string EscapeHtml(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    [HttpGet("/authorize")]
    public IActionResult AuthorizeForm(
        [FromQuery] string client_id, [FromQuery] string redirect_uri, [FromQuery] string state,
        [FromQuery] string code_challenge, [FromQuery] string code_challenge_method, [FromQuery] string? error = null)
    {
        var client = _oauth.GetClient(client_id);
        if (client is null) return BadRequest("Unknown client_id — the connector must register first.");
        if (!client.RedirectUris.Contains(redirect_uri)) return BadRequest("redirect_uri does not match the registered client.");

        var html = $@"<!doctype html>
<html><head><meta charset='utf-8'><title>Test Case Hub — Sign in</title>
<style>
  body{{font-family:-apple-system,Segoe UI,Roboto,Arial,sans-serif;background:#f5f6f8;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;}}
  .card{{background:#fff;border:1px solid #dcdfe4;border-radius:10px;padding:28px 32px;width:340px;}}
  h1{{font-size:18px;margin:0 0 4px;}}
  p{{font-size:13px;color:#5b6270;margin:0 0 18px;}}
  label{{font-size:12px;font-weight:600;color:#5b6270;display:block;margin-bottom:4px;}}
  input{{width:100%;box-sizing:border-box;padding:8px 10px;border:1px solid #dcdfe4;border-radius:6px;margin-bottom:14px;font-size:13px;}}
  button{{width:100%;padding:9px;border-radius:6px;border:1px solid #2f5496;background:#2f5496;color:#fff;font-size:13px;cursor:pointer;}}
  .err{{color:#a32d2d;font-size:13px;margin:0 0 12px;}}
</style></head>
<body>
  <div class='card'>
    <h1>Test Case Hub</h1>
    <p>Sign in to let Claude use this connector as you.</p>
    {(error != null ? $"<p class='err'>{EscapeHtml(error)}</p>" : "")}
    <form method='post' action='/authorize'>
      <input type='hidden' name='client_id' value='{EscapeHtml(client_id)}'>
      <input type='hidden' name='redirect_uri' value='{EscapeHtml(redirect_uri)}'>
      <input type='hidden' name='state' value='{EscapeHtml(state)}'>
      <input type='hidden' name='code_challenge' value='{EscapeHtml(code_challenge)}'>
      <input type='hidden' name='code_challenge_method' value='{EscapeHtml(code_challenge_method)}'>
      <label>Email</label>
      <input type='email' name='email' required autofocus>
      <label>Password</label>
      <input type='password' name='password' required>
      <button type='submit'>Log in</button>
    </form>
  </div>
</body></html>";
        return Content(html, "text/html");
    }

    public record AuthorizeFormData(string client_id, string redirect_uri, string state, string code_challenge, string code_challenge_method, string email, string password);

    [HttpPost("/authorize")]
    [Consumes("application/x-www-form-urlencoded")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> AuthorizeSubmit([FromForm] AuthorizeFormData form)
    {
        var client = _oauth.GetClient(form.client_id);
        if (client is null || !client.RedirectUris.Contains(form.redirect_uri))
            return BadRequest("Invalid client or redirect_uri.");

        var normalizedEmail = (form.email ?? "").Trim().ToLowerInvariant();
        var user = await _store.GetUserByEmailAsync(normalizedEmail);
        if (user is null || !BCrypt.Net.BCrypt.Verify(form.password ?? "", user.PasswordHash))
        {
            var retryUrl = $"/authorize?client_id={Uri.EscapeDataString(form.client_id)}&redirect_uri={Uri.EscapeDataString(form.redirect_uri)}&state={Uri.EscapeDataString(form.state)}&code_challenge={Uri.EscapeDataString(form.code_challenge)}&code_challenge_method={Uri.EscapeDataString(form.code_challenge_method)}&error={Uri.EscapeDataString("Invalid email or password.")}";
            return Redirect(retryUrl);
        }

        var code = _oauth.IssueCode(form.client_id, form.redirect_uri, form.code_challenge, form.code_challenge_method, user.Id);
        var sep = form.redirect_uri.Contains('?') ? "&" : "?";
        return Redirect($"{form.redirect_uri}{sep}code={code.Code}&state={Uri.EscapeDataString(form.state)}");
    }

    public record TokenRequest(string? grant_type, string? code, string? redirect_uri, string? client_id, string? code_verifier, string? refresh_token);

    [HttpPost("/token")]
    [Consumes("application/x-www-form-urlencoded")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Token([FromForm] TokenRequest req)
    {
        // Standard RFC 6749 refresh_token grant — this is what lets Claude's MCP connector
        // silently mint a new access token when the 1-hour one expires, instead of bouncing
        // the person back to the /authorize login screen every hour.
        if (req.grant_type == "refresh_token")
        {
            var result = await _refresh.RedeemAsync(req.refresh_token ?? "");
            if (result is null)
                return BadRequest(new { error = "invalid_grant", error_description = "Refresh token is invalid, expired, or already used." });
            var (rUser, rNewRefresh) = result.Value;
            return Ok(new { access_token = _jwt.GenerateToken(rUser), token_type = "Bearer", expires_in = 3600, refresh_token = rNewRefresh });
        }

        if (req.grant_type != "authorization_code")
            return BadRequest(new { error = "unsupported_grant_type" });

        var authCode = _oauth.ConsumeCode(req.code ?? "");
        if (authCode is null)
            return BadRequest(new { error = "invalid_grant", error_description = "Code is invalid, expired, or already used." });
        if (authCode.ClientId != req.client_id || authCode.RedirectUri != req.redirect_uri)
            return BadRequest(new { error = "invalid_grant", error_description = "Client or redirect_uri mismatch." });

        // PKCE: SHA256(code_verifier), base64url, must equal the code_challenge captured at /authorize.
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.ASCII.GetBytes(req.code_verifier ?? ""));
        var computedChallenge = Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (authCode.CodeChallengeMethod == "S256" && computedChallenge != authCode.CodeChallenge)
            return BadRequest(new { error = "invalid_grant", error_description = "PKCE verification failed." });

        var user = await _store.GetUserByIdAsync(authCode.UserId);
        if (user is null) return BadRequest(new { error = "invalid_grant", error_description = "User no longer exists." });

        var accessToken = _jwt.GenerateToken(user);
        var refreshToken = await _refresh.IssueAsync(user.Id, authCode.ClientId);
        return Ok(new { access_token = accessToken, token_type = "Bearer", expires_in = 3600, refresh_token = refreshToken });
    }
}
