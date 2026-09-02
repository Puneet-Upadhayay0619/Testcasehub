using System.ComponentModel.DataAnnotations;

namespace TestCaseHub.Api.Models;

public static class RepoHost
{
    public const string GitHub = "GitHub";
    public const string AzureDevOps = "AzureDevOps";
    public static readonly string[] All = { GitHub, AzureDevOps };
}

// Which slice of a module's real behaviour this repo covers -- agreed in planning that one
// module can (and in practice, for UWMC, DOES) have its frontend, backend, and DB-migration
// history living in three entirely separate repos. "Unspecified" is the escape hatch for a repo
// that covers more than one layer (a monorepo) rather than forcing an artificial split.
public static class RepoLayer
{
    public const string Frontend = "Frontend";
    public const string Backend = "Backend";
    public const string Database = "Database";
    public const string Unspecified = "Unspecified";
    public static readonly string[] All = { Frontend, Backend, Database, Unspecified };
}

// Tells the AI-generation flow WHERE to look for a module's real source code -- exactly the
// information that had to be discovered by hand (and via a wrong-org GitHub dead-end) before
// the UWMC automation project could be written for real. One module can have several of these
// (one per layer). The access token is read-only by design: this link only ever needs to be
// SEARCHED, never written to -- Test Case Hub commits nothing back to a company's own repo
// (the generated script itself is stored in AutomationScript, not pushed anywhere).
public class ModuleRepoLink
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int ModuleId { get; set; }

    [MaxLength(16)]
    public string RepoHost { get; set; } = Models.RepoHost.GitHub;
    [MaxLength(16)]
    public string Layer { get; set; } = RepoLayer.Unspecified;

    // GitHub: this is the org/user (e.g. "FieldAssist"). Azure DevOps: this is the organization
    // (e.g. "flick2know") and Project below is additionally required.
    [Required, MaxLength(128)]
    public string OrgOrAccount { get; set; } = "";
    // Azure DevOps only -- a repo lives inside a Project, GitHub has no equivalent concept.
    [MaxLength(128)]
    public string Project { get; set; } = "";
    [Required, MaxLength(128)]
    public string RepoName { get; set; } = "";
    [MaxLength(128)]
    public string Branch { get; set; } = "main";
    // Narrows search/generation to a subfolder of a large repo, e.g. "/FADashboard.Core" --
    // optional, purely a performance/precision aid.
    [MaxLength(256)]
    public string BasePath { get; set; } = "";

    // Which platform this repo belongs to (TestingPlatform.Core3 -- Dashboard/App-API/App,
    // no "Both": a single repo link is one concrete codebase). Independent of Layer above --
    // Layer is repo STRUCTURE (frontend/backend/db within that codebase), this is which
    // product/platform the codebase itself is for. E.g. App-API's backend lives in Azure
    // DevOps, App's mobile codebase lives in GitHub entirely separately -- this is what lets
    // both coexist on the same module without the RepoHost split causing any conflict. Existing
    // rows migrate to Dashboard (everything real configured so far).
    [MaxLength(16)]
    public string TestingPlatform { get; set; } = Models.TestingPlatform.Dashboard;

    // Read-only PAT, encrypted at rest via SecretProtector -- never returned in full by any GET.
    public string AccessTokenEncrypted { get; set; } = "";

    [MaxLength(256)]
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(256)]
    public string UpdatedBy { get; set; } = "";
    public DateTime? UpdatedAt { get; set; }
}
