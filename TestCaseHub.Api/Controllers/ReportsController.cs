using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;
using TestCaseHub.Api.Models;

namespace TestCaseHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/reports")]
public class ReportsController : ControllerBase
{
    private readonly IDataStore _store;
    public ReportsController(IDataStore store) => _store = store;

    // Per module: what fraction of its (non-deprecated) test cases have EVER recorded at
    // least one Pass result, across all test runs ever. A simple, honest coverage number —
    // "has this actually been verified to pass at least once", not "does a script exist for it".
    [HttpGet("coverage")]
    public async Task<ActionResult<List<CoverageRow>>> Coverage([FromQuery] int? companyId = null)
    {
        var effective = User.IsSuperAdmin() ? companyId : User.GetCompanyId();
        if (effective is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();
        var modules = (await _store.GetModulesAsync()).Where(m => m.CompanyId == effective).ToList();
        var rows = new List<CoverageRow>();
        foreach (var m in modules)
        {
            var cases = await _store.GetTestCasesAsync(new TestCaseFilter(m.Id, null, null, null, null, null));
            var nonDeprecated = cases.Where(c => c.Status != "Deprecated").ToList();
            int withPass = 0;
            foreach (var c in nonDeprecated)
            {
                var results = await _store.GetResultsForTestCaseAsync(c.Id);
                if (results.Any(r => r.Status == "Pass")) withPass++;
            }
            var pct = nonDeprecated.Count == 0 ? 0 : Math.Round(100.0 * withPass / nonDeprecated.Count, 1);
            rows.Add(new CoverageRow(m.Id, m.Name, nonDeprecated.Count, withPass, pct));
        }
        return rows;
    }

    // Release-over-release pass-rate trend — "kaunsa module sabse zyada flaky/fail hota hai"
    // type analysis starts from this same per-release number.
    [HttpGet("trend")]
    public async Task<ActionResult<List<ReleaseTrendPoint>>> Trend([FromQuery] int? companyId = null)
    {
        var effective = User.IsSuperAdmin() ? companyId : User.GetCompanyId();
        if (effective is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();
        var releases = (await _store.GetReleasesAsync()).Where(r => r.CompanyId == effective).ToList();
        var points = new List<ReleaseTrendPoint>();
        foreach (var r in releases.OrderBy(r => r.CreatedAt))
        {
            var results = await _store.GetResultsForReleaseAsync(r.Id);
            if (results.Count == 0) continue;
            var rollup = RollupCalculator.Compute(results);
            points.Add(new ReleaseTrendPoint(r.Id, $"{r.Name} {r.Version}".Trim(), rollup.PassRatePercent, rollup.TotalCases));
        }
        return points;
    }
}
