using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;

namespace TestCaseHub.Api.Services;

// TestRunResult is append-only (retries/re-runs add new rows rather than overwriting), so every
// rollup first has to reduce down to "the latest result per (TestCaseId, Platform)" before
// counting Pass/Fail/etc. — this is the one place that reduction happens, so trend/coverage/
// readiness-report numbers can never disagree with each other over how "latest" is defined.
public static class RollupCalculator
{
    public static List<TestRunResult> LatestPerCase(IEnumerable<TestRunResult> results) =>
        results
            .GroupBy(r => (r.TestCaseId, r.Platform))
            .Select(g => g.OrderByDescending(r => r.ExecutedAt).First())
            .ToList();

    public static TestRunRollup Compute(IEnumerable<TestRunResult> results)
    {
        var latest = LatestPerCase(results);
        int passed = latest.Count(r => r.Status == "Pass");
        int failed = latest.Count(r => r.Status == "Fail");
        int blocked = latest.Count(r => r.Status == "Blocked");
        int skipped = latest.Count(r => r.Status == "Skipped");
        int notRun = latest.Count(r => r.Status == "NotRun");
        int total = latest.Count;
        double passRate = total == 0 ? 0 : Math.Round(100.0 * passed / total, 1);
        return new TestRunRollup(total, passed, failed, blocked, skipped, notRun, passRate);
    }
}
