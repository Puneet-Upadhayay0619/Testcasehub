using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Controllers;

// Named groups of test cases (agreed in planning) so a Test Run (Phase 5) can target e.g.
// "Sanity Suite" instead of every test case in a module. Static = hand-picked list. Dynamic =
// a saved filter re-evaluated on every /resolve call.
[ApiController]
[Authorize]
[Route("api/suites")]
public class SuitesController : ControllerBase
{
    private readonly IDataStore _store;
    public SuitesController(IDataStore store) => _store = store;

    private string CurrentUserDisplayName => User.FindFirstValue("displayName") ?? "Unknown";

    [HttpGet]
    public async Task<ActionResult<List<SuiteResponse>>> GetAll([FromQuery] int? companyId = null)
    {
        var effective = User.IsSuperAdmin() ? companyId : User.GetCompanyId();
        if (effective is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();
        return (await _store.GetSuitesAsync()).Where(s => s.CompanyId == effective).Select(SuiteResponse.From).ToList();
    }

    [HttpPost("static")]
    public async Task<ActionResult<SuiteResponse>> CreateStatic(CreateStaticSuiteRequest req, [FromQuery] int? companyId = null)
    {
        if (!User.CanManageSuites()) return Forbid();
        companyId = User.ResolveActingCompanyId(companyId);
        if (companyId is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Suite name is required.");
        var suite = new TestSuite { CompanyId = companyId.Value, Name = req.Name.Trim(), Description = req.Description ?? "", Kind = "Static", CreatedBy = CurrentUserDisplayName };
        suite.TestCaseIds = req.TestCaseIds ?? new();
        suite = await _store.CreateSuiteAsync(suite);
        return SuiteResponse.From(suite);
    }

    // Internal shape persisted in TestSuite.FilterJson — deliberately separate from
    // CreateDynamicSuiteRequest (which also carries Name/Description, not part of the filter
    // itself) so serialize/deserialize round-trips cleanly with no constructor-parameter drift.
    private record DynamicFilter(int? ModuleId, string? Layer, string? VerificationType, string? Status, string? Priority, string? Tag, string? Search);

    [HttpPost("dynamic")]
    public async Task<ActionResult<SuiteResponse>> CreateDynamic(CreateDynamicSuiteRequest req, [FromQuery] int? companyId = null)
    {
        if (!User.CanManageSuites()) return Forbid();
        companyId = User.ResolveActingCompanyId(companyId);
        if (companyId is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Suite name is required.");
        var filter = new DynamicFilter(req.ModuleId, req.Layer, req.VerificationType, req.Status, req.Priority, req.Tag, req.Search);
        var suite = new TestSuite
        {
            CompanyId = companyId.Value, Name = req.Name.Trim(), Description = req.Description ?? "", Kind = "Dynamic",
            FilterJson = System.Text.Json.JsonSerializer.Serialize(filter), CreatedBy = CurrentUserDisplayName
        };
        suite = await _store.CreateSuiteAsync(suite);
        return SuiteResponse.From(suite);
    }

    // Returns the test cases the suite CURRENTLY resolves to — for a Dynamic suite this is
    // re-computed every call, so it always reflects cases added/removed/re-tagged since the
    // suite was created.
    [HttpGet("{id:int}/resolve")]
    public async Task<ActionResult<List<TestCaseResponse>>> Resolve(int id)
    {
        var suite = await _store.GetSuiteAsync(id);
        if (suite is null) return NotFound();
        if (!User.HasCompanyAccess(suite.CompanyId)) return Forbid();

        if (suite.Kind == "Static")
        {
            var cases = new List<TestCaseResponse>();
            foreach (var tcId in suite.TestCaseIds)
            {
                var tc = await _store.GetTestCaseAsync(tcId);
                if (tc is not null) cases.Add(TestCaseResponse.From(tc));
            }
            return cases;
        }

        var f = System.Text.Json.JsonSerializer.Deserialize<DynamicFilter>(suite.FilterJson)!;
        var results = await _store.GetTestCasesAsync(new TestCaseFilter(f.ModuleId, f.Layer, f.VerificationType, f.Status, f.Priority, f.Search));
        if (!string.IsNullOrWhiteSpace(f.Tag)) results = results.Where(t => t.Tags.Contains(f.Tag)).ToList();
        return results.Select(TestCaseResponse.From).ToList();
    }
}
