using Microsoft.AspNetCore.DataProtection;

namespace TestCaseHub.Api.Services;

// Wraps ASP.NET Core's Data Protection API so sensitive strings (DB connection strings, CI
// API keys — Phase 6) are encrypted before they're written to the database and decrypted only
// when actually needed, rather than sitting in plain text. "Purpose" strings scope keys so a
// value protected for one purpose can't be unprotected under another.
public class SecretProtector
{
    private readonly IDataProtector _protector;
    public SecretProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("TestCaseHub.Secrets.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);
    public string Unprotect(string protectedText) => _protector.Unprotect(protectedText);
}
