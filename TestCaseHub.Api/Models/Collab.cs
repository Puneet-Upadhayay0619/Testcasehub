using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TestCaseHub.Api.Models;

// A named group of test cases a Test Run (Phase 5) can target — e.g. "Sanity Suite", so a
// sanity run doesn't have to mean "every test case in the module". Static = an explicit list
// of TestCaseIds (curated by hand). Dynamic = a saved filter that's re-evaluated every time the
// suite is resolved, so e.g. "everything tagged Regression in Loyalty module" always reflects
// the current set of matching cases, including ones added after the suite was created.
public class TestSuite
{
    public int Id { get; set; }

    // Phase 8: company isolation -- a suite lives in exactly one company.
    public int CompanyId { get; set; }
    [Required, MaxLength(128)]
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    [MaxLength(16)]
    public string Kind { get; set; } = "Static"; // "Static" or "Dynamic"

    public string TestCaseIdsJson { get; set; } = "[]"; // used when Kind == Static
    public string FilterJson { get; set; } = "{}";      // used when Kind == Dynamic (ModuleId/Layer/Status/Priority/Tag/Search)

    [MaxLength(256)]
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Mandatory platform tag (TestingPlatform.All) -- applies to BOTH Static and Dynamic suites,
    // set explicitly at creation. Separate from the optional "Layer" criterion Dynamic suites
    // may also carry inside FilterJson (which test cases match), since a suite's own platform
    // classification and its matching criterion can legitimately differ. Existing rows migrate
    // to Dashboard.
    [MaxLength(16)]
    public string TestingPlatform { get; set; } = Models.TestingPlatform.Dashboard;

    [NotMapped]
    public List<string> TestCaseIds
    {
        get => System.Text.Json.JsonSerializer.Deserialize<List<string>>(TestCaseIdsJson) ?? new();
        set => TestCaseIdsJson = System.Text.Json.JsonSerializer.Serialize(value);
    }
}

// Discussion thread on a test case. Soft-delete only (Deleted flag) so moderation still leaves
// an audit trail instead of destroying the record outright.
public class TestCaseComment
{
    public int Id { get; set; }
    [Required, MaxLength(64)]
    public string TestCaseId { get; set; } = "";
    [MaxLength(256)]
    public string AuthorEmail { get; set; } = "";
    [MaxLength(128)]
    public string AuthorDisplayName { get; set; } = "";
    [Required]
    public string Body { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool Deleted { get; set; } = false;
    [MaxLength(256)]
    public string? DeletedBy { get; set; }
}
