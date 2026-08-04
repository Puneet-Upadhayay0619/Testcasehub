using System.ComponentModel.DataAnnotations;

namespace TestCaseHub.Api.Models;

// Long-lived opaque refresh token (30 days) paired with a short-lived JWT access token (1
// hour) — this is what lets an MCP connector (or the web app) stay logged in across days
// without re-entering id/password each time, which was the explicit ask this whole OAuth
// flow was built for. Only the SHA-256 hash is ever stored — the raw token is handed to the
// client once and can never be recovered from the DB, same principle as password hashing.
// Rotated on every use (old one revoked, new one issued) so a stolen-but-unused old token is
// a dead end.
public class RefreshToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    [Required, MaxLength(128)]
    public string TokenHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(30);
    public bool Revoked { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    // "" for a direct web-app login; the OAuth client_id when issued through /token, so a
    // revoked/rotated connector session can't be conflated with a direct login session.
    [MaxLength(64)]
    public string ClientId { get; set; } = "";
}

// Short-lived (30 min), single-use password reset token. Same hashing principle as
// RefreshToken — only the hash is stored.
public class PasswordResetToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    [Required, MaxLength(128)]
    public string TokenHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(30);
    public bool Used { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
