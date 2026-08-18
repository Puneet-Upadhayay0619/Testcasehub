using System.Collections.Concurrent;

namespace TestCaseHub.Api.Services;

// Minimal in-memory OAuth 2.1 (authorization code + PKCE) state, scoped to what MCP clients like
// Claude actually need: dynamic client registration (RFC 7591) then the standard code+PKCE dance.
// Clients and codes are short-lived by nature (a code lives seconds; a client registration is
// cheap to redo), so keeping this in memory (not the JSON data file) is a deliberate choice —
// it resets on server restart, which just means Claude re-registers/re-authorizes, no data lost.
public class OAuthClient
{
    public string ClientId { get; set; } = "";
    public List<string> RedirectUris { get; set; } = new();
}

public class OAuthCode
{
    public string Code { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string CodeChallenge { get; set; } = "";
    public string CodeChallengeMethod { get; set; } = "S256";
    public int UserId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool Used { get; set; }
}

public class OAuthStore
{
    private readonly ConcurrentDictionary<string, OAuthClient> _clients = new();
    private readonly ConcurrentDictionary<string, OAuthCode> _codes = new();

    public OAuthClient RegisterClient(List<string> redirectUris)
    {
        var client = new OAuthClient { ClientId = "mcp_" + Guid.NewGuid().ToString("N"), RedirectUris = redirectUris };
        _clients[client.ClientId] = client;
        return client;
    }

    public OAuthClient? GetClient(string clientId) => _clients.TryGetValue(clientId, out var c) ? c : null;

    public OAuthCode IssueCode(string clientId, string redirectUri, string codeChallenge, string codeChallengeMethod, int userId)
    {
        var code = new OAuthCode
        {
            Code = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            ClientId = clientId, RedirectUri = redirectUri,
            CodeChallenge = codeChallenge, CodeChallengeMethod = codeChallengeMethod,
            UserId = userId, ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
        _codes[code.Code] = code;
        return code;
    }

    public OAuthCode? ConsumeCode(string code)
    {
        if (!_codes.TryGetValue(code, out var c)) return null;
        if (c.Used || c.ExpiresAt < DateTime.UtcNow) return null;
        c.Used = true;
        return c;
    }
}
