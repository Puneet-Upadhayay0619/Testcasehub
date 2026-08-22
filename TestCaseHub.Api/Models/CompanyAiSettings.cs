using System.ComponentModel.DataAnnotations;

namespace TestCaseHub.Api.Models;

// Second AI-generation path, added alongside the MCP-based one (explicit instruction: "dono
// option rakhne hai" -- keep both). This one lets a company plug in their OWN Anthropic API key
// so Test Case Hub's backend can call Claude directly (no external MCP-connected chat session
// needed) to generate an automation script from a test case + its linked repo code. One row per
// company. The key is encrypted at rest (SecretProtector) and never returned in full by any GET
// -- same convention as DB connection strings, repo access tokens, and environment credentials.
public class CompanyAiSettings
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    // Only Anthropic is wired up right now, but kept as free text (like AutomationScript.
    // Framework) so a second provider doesn't need a schema change later.
    [MaxLength(32)]
    public string Provider { get; set; } = "Anthropic";
    [MaxLength(64)]
    public string Model { get; set; } = "claude-sonnet-5";
    public string ApiKeyEncrypted { get; set; } = "";
    // Explicit on/off switch, separate from "is a key even saved" -- lets an Admin temporarily
    // disable direct generation (e.g. while rotating the key) without deleting the row.
    public bool Enabled { get; set; } = true;

    [MaxLength(256)]
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(256)]
    public string UpdatedBy { get; set; } = "";
    public DateTime? UpdatedAt { get; set; }
}
