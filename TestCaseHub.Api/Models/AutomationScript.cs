using System.ComponentModel.DataAnnotations;

namespace TestCaseHub.Api.Models;

public static class AutomationScriptStatus
{
    public const string Draft = "Draft";
    public const string Reviewed = "Reviewed";
    public const string Approved = "Approved";
    public static readonly string[] All = { Draft, Reviewed, Approved };
}

// The whole point of this entity (per explicit instruction: "me company ke repo me save
// kraunga to test case hub ka kya fayda?") is that a generated automation script lives HERE,
// in Test Case Hub's own database -- retrievable company/module/suite-wise -- never pushed to
// the company's own repo. Generation itself (Phase near-term) happens OUTSIDE this API, via an
// MCP-connected Claude session that has both Test Case Hub's MCP and the company's repo MCP
// (ModuleRepoLink) open at once; save_automation_script/get_automation_scripts (McpTools) are
// the only two calls that flow is expected to make against this table.
//
// SuiteId is optional: a script can be generated straight from one TestCaseId before it's ever
// organized into a suite. Version increments on every save for the same
// Company+Module+TestCase+FileName combination, rather than overwriting -- so a bad generation
// can always be compared against (or rolled back to) the previous one.
public class AutomationScript
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int ModuleId { get; set; }
    [MaxLength(64)]
    public string? TestCaseId { get; set; }
    public int? SuiteId { get; set; }

    [Required, MaxLength(256)]
    public string FileName { get; set; } = "";
    // e.g. "Playwright-TypeScript", "Playwright-Python" -- free text on purpose, new frameworks
    // shouldn't need a code change here.
    [MaxLength(64)]
    public string Framework { get; set; } = "";
    [Required]
    public string Content { get; set; } = "";

    [MaxLength(16)]
    public string Status { get; set; } = AutomationScriptStatus.Draft;

    // "AI (Claude via MCP)" vs a human editing it by hand afterwards -- kept as free text so it
    // can name the actual generation path without a fixed enum.
    [MaxLength(128)]
    public string GeneratedBy { get; set; } = "";
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public int Version { get; set; } = 1;

    // Which ModuleRepoLink(s) fed this generation -- stored as a comma-separated list of
    // ModuleRepoLink Ids rather than a hard FK, since a script is typically generated from
    // MULTIPLE linked repos at once (frontend + backend + database) and should still resolve
    // to something readable even if a link is later deleted.
    [MaxLength(128)]
    public string SourceRepoRefs { get; set; } = "";
}
