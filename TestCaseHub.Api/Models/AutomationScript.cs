using System.ComponentModel.DataAnnotations;

namespace TestCaseHub.Api.Models;

public static class AutomationScriptStatus
{
    public const string Draft = "Draft";
    public const string Reviewed = "Reviewed";
    public const string Approved = "Approved";
    public static readonly string[] All = { Draft, Reviewed, Approved };
}

// Smoke = a handful of representative scripts run right after every deploy to confirm native
// execution is alive at all (wide, shallow). Sanity = re-running just the script(s) related to
// whatever was just fixed (narrow, deep). Regression = the full suite, run periodically / before
// a release (wide and deep). Agreed in planning alongside the Mock-mode and capture-and-restore
// work -- lets "Run tier" batch-execute a whole group instead of one script at a time.
public static class TestTier
{
    public const string Smoke = "Smoke";
    public const string Sanity = "Sanity";
    public const string Regression = "Regression";
    public static readonly string[] All = { Smoke, Sanity, Regression };
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

    // Native execution definition (agreed: "test case hub se hi complete testing" -- no
    // Node/Playwright subprocess, no downloading a zip and running it externally). This is a
    // small JSON array of steps (http / sql / assert) that ScriptExecutionService interprets
    // directly in-process against an EnvironmentTarget + EnvironmentCredential. It is a
    // best-effort, hand-authored TRANSLATION of what Content (the real Playwright/TS script)
    // does -- kept separate from Content so the human-readable script (and its FLAG comments
    // documenting real-vs-spec gaps) is never lost or auto-generated away. Null/empty means
    // "not yet wired for native execution" -- Run stays disabled in the UI for that script.
    public string? ExecutionDefinitionJson { get; set; }

    // Which test tier this script belongs to for batch "Run tier" execution -- see TestTier
    // above. Defaults to Regression (the safest default: only runs when explicitly asked for the
    // full suite, never accidentally swept into a "just run Smoke" quick check).
    [MaxLength(16)]
    public string TestTier { get; set; } = Models.TestTier.Regression;
}
