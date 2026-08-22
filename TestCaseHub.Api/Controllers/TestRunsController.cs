using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/testruns")]
public class TestRunsController : ControllerBase
{
    private readonly IDataStore _store;
    private readonly NotificationService _notify;
    private readonly ApiKeyService _apiKeys;
    private readonly AdoService _ado;
    public TestRunsController(IDataStore store, NotificationService notify, ApiKeyService apiKeys, AdoService ado)
    { _store = store; _notify = notify; _apiKeys = apiKeys; _ado = ado; }

    private string ActorDisplayName => User.FindFirstValue("displayName") ?? "Unknown";

    [HttpGet]
    public async Task<ActionResult<List<TestRunResponse>>> GetAll([FromQuery] int? releaseId, [FromQuery] int? companyId = null)
    {
        var effective = User.IsSuperAdmin() ? companyId : User.GetCompanyId();
        if (effective is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();
        return (await _store.GetTestRunsAsync(releaseId)).Where(r => r.CompanyId == effective).Select(TestRunResponse.From).ToList();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<TestRunResponse>> GetOne(int id)
    {
        var run = await _store.GetTestRunAsync(id);
        if (run is null) return NotFound();
        if (!User.HasCompanyAccess(run.CompanyId)) return Forbid();
        return TestRunResponse.From(run);
    }

    [HttpPost]
    public async Task<ActionResult<TestRunResponse>> Create(CreateTestRunRequest req, [FromQuery] int? companyId = null)
    {
        if (!User.IsAtLeast(Roles.Contributor)) return Forbid();
        // Attaching a named execution credential is the "trigger automation as this login"
        // action agreed in planning -- Company Admin AND Team Lead can do this (Contributor
        // cannot), even though a bare Contributor can still create a credential-less test run.
        if (req.EnvironmentCredentialId is not null && !User.CanTriggerTestRun()) return Forbid();
        companyId = User.ResolveActingCompanyId(companyId);
        if (companyId is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Test run name is required.");
        if (req.EnvironmentCredentialId is not null)
        {
            var cred = await _store.GetEnvironmentCredentialAsync(req.EnvironmentCredentialId.Value);
            if (cred is null || cred.EnvironmentTargetId != req.EnvironmentTargetId)
                return BadRequest("EnvironmentCredentialId must belong to the selected EnvironmentTargetId.");
        }
        var run = new TestRun
        {
            CompanyId = companyId.Value,
            ReleaseId = req.ReleaseId, SuiteId = req.SuiteId, Name = req.Name.Trim(),
            TargetEnvironment = req.TargetEnvironment ?? "", EnvironmentTargetId = req.EnvironmentTargetId,
            EnvironmentCredentialId = req.EnvironmentCredentialId, CreatedBy = ActorDisplayName
        };
        run = await _store.CreateTestRunAsync(run);
        return TestRunResponse.From(run);
    }

    [HttpGet("{id:int}/results")]
    public async Task<ActionResult<List<TestRunResultResponse>>> GetResults(int id)
    {
        var run = await _store.GetTestRunAsync(id);
        if (run is null) return NotFound();
        if (!User.HasCompanyAccess(run.CompanyId)) return Forbid();
        return (await _store.GetTestRunResultsAsync(id)).Select(TestRunResultResponse.From).ToList();
    }

    [HttpGet("{id:int}/rollup")]
    public async Task<ActionResult<TestRunRollup>> GetRollup(int id)
    {
        var run = await _store.GetTestRunAsync(id);
        if (run is null) return NotFound();
        if (!User.HasCompanyAccess(run.CompanyId)) return Forbid();
        return RollupCalculator.Compute(await _store.GetTestRunResultsAsync(id));
    }

    // Manual execution recording (Phase 5) — for whatever hasn't been automated yet, a tester
    // records Pass/Fail/Blocked/Skipped themselves. Every authenticated role can record a
    // result for a run they're doing (this is data entry, not a permission-sensitive action the
    // way editing a test case's definition is).
    [HttpPost("{id:int}/results/manual")]
    public async Task<ActionResult<TestRunResultResponse>> RecordManualResult(int id, RecordManualResultRequest req)
    {
        var run = await _store.GetTestRunAsync(id);
        if (run is null) return NotFound("Test run not found.");
        if (!User.HasCompanyAccess(run.CompanyId)) return Forbid();
        var tc = await _store.GetTestCaseAsync(req.TestCaseId);
        if (tc is null) return BadRequest("Test case not found.");

        var validStatuses = new[] { "Pass", "Fail", "Blocked", "Skipped", "NotRun" };
        if (!validStatuses.Contains(req.Status)) return BadRequest("Status must be one of Pass, Fail, Blocked, Skipped, NotRun.");

        var result = new TestRunResult
        {
            TestRunId = id, TestCaseId = req.TestCaseId, Platform = req.Platform,
            Status = req.Status, IsAutomated = false, ExecutedBy = ActorDisplayName, Notes = req.Notes ?? ""
        };
        result = await _store.AddTestRunResultAsync(result);

        if (req.Status == "Fail")
            await _notify.NotifyAdminsAndLeadsAsync("ManualResultFailed", $"{ActorDisplayName} recorded a FAIL for {req.TestCaseId} in run '{run.Name}'.");

        return TestRunResultResponse.From(result);
    }
    // Automated result reporting (Phase 6) — this is what a CI pipeline step calls after
    // running a Playwright/Appium/API/DB check. Accepts EITHER a normal JWT (a human testing
    // the endpoint) OR an `X-Api-Key` header (the CI service-account key from ApiKeysController)
    // — [AllowAnonymous] here just means "the standard JWT challenge doesn't auto-reject the
    // request", not that no credential is required; ExecutedBy below shows exactly which path
    // authenticated the call.
    [HttpPost("{id:int}/results/automated")]
    [AllowAnonymous]
    public async Task<ActionResult<TestRunResultResponse>> RecordAutomatedResult(int id, RecordAutomatedResultRequest req)
    {
        string executedBy;
        int callerCompanyId;
        if (User.Identity?.IsAuthenticated == true)
        {
            executedBy = $"Automated (via {ActorDisplayName})";
            var jwtCompanyId = User.GetCompanyId();
            if (jwtCompanyId is null) return Forbid();
            callerCompanyId = jwtCompanyId.Value;
        }
        else
        {
            var apiKey = await _apiKeys.ValidateAsync(Request.Headers["X-Api-Key"].FirstOrDefault() ?? "");
            if (apiKey is null) return Unauthorized("A valid JWT or X-Api-Key header is required.");
            executedBy = $"Automated (CI: {apiKey.Name})";
            callerCompanyId = apiKey.CompanyId;
        }

        var run = await _store.GetTestRunAsync(id);
        if (run is null) return NotFound("Test run not found.");
        if (run.CompanyId != callerCompanyId) return Forbid();

        // Test-data safety: automation is hard-blocked from posting results against a run
        // targeting a Production environment — this doesn't stop the automation from having
        // RUN there (that's an environment-access concern outside this API), but it refuses to
        // record/accept it as a legitimate automated Test Run result.
        if (run.EnvironmentTargetId is not null)
        {
            var env = await _store.GetEnvironmentTargetAsync(run.EnvironmentTargetId.Value);
            if (env is not null && env.EnvironmentType == Models.EnvironmentType.Production)
                return BadRequest(new { error = $"Test run targets a Production environment ('{env.Name}') — automated results cannot be recorded against Production." });
        }

        if (string.IsNullOrWhiteSpace(req.RunAttemptKey))
            return BadRequest("runAttemptKey is required (idempotency key — one per actual automation attempt).");

        // Idempotency: re-posting the SAME attempt (e.g. a retried webhook call) returns the
        // already-recorded result instead of creating a duplicate row.
        var existing = await _store.GetTestRunResultByAttemptKeyAsync(req.RunAttemptKey);
        if (existing is not null) return TestRunResultResponse.From(existing);

        var tc = await _store.GetTestCaseAsync(req.TestCaseId);
        if (tc is null) return BadRequest("Test case not found.");
        var validStatuses = new[] { "Pass", "Fail", "Blocked", "Skipped", "NotRun" };
        if (!validStatuses.Contains(req.Status)) return BadRequest("Status must be one of Pass, Fail, Blocked, Skipped, NotRun.");

        var result = new TestRunResult
        {
            TestRunId = id, TestCaseId = req.TestCaseId, Platform = req.Platform, Status = req.Status,
            IsAutomated = true, ExecutedBy = executedBy, Notes = req.Notes ?? "",
            RetryCount = req.RetryCount, RunAttemptKey = req.RunAttemptKey
        };
        result = await _store.AddTestRunResultAsync(result);

        if (req.Status == "Fail")
            await _notify.NotifyAdminsAndLeadsAsync("AutomationFailed", $"Automated run failed for {req.TestCaseId} in run '{run.Name}' (after {req.RetryCount} retries).");

        return TestRunResultResponse.From(result);
    }

    // Files an Azure DevOps Bug from a Fail/Blocked result. Resilient by design (3 retries
    // inside AdoService) — if it still fails after retries, the error is returned clearly
    // rather than the attempt being silently dropped.
    [HttpPost("{id:int}/results/{resultId:int}/create-bug")]
    public async Task<ActionResult<CreateBugFromResultResponse>> CreateBug(int id, int resultId)
    {
        if (!User.IsAtLeast(Roles.Contributor)) return Forbid();
        var run = await _store.GetTestRunAsync(id);
        if (run is null) return NotFound("Test run not found.");
        if (!User.HasCompanyAccess(run.CompanyId)) return Forbid();
        var results = await _store.GetTestRunResultsAsync(id);
        var result = results.FirstOrDefault(r => r.Id == resultId);
        if (result is null) return NotFound("Result not found.");
        if (!string.IsNullOrEmpty(result.BugWorkItemId))
            return Ok(new CreateBugFromResultResponse(true, result.BugWorkItemId, null, "A bug was already filed for this result."));

        var tc = await _store.GetTestCaseAsync(result.TestCaseId);
        var title = $"[{result.TestCaseId}] {tc?.Title ?? "Test case"} failed in Test Run #{id}";
        var description = $"Status: {result.Status}\nPlatform: {result.Platform}\nExecuted by: {result.ExecutedBy}\nNotes: {result.Notes}";

        var adoResult = await _ado.CreateBugWithRetryAsync(title, description);
        if (adoResult.Success)
        {
            result.BugWorkItemId = adoResult.WorkItemId;
            await _store.UpdateTestRunResultAsync(result); // enriching metadata on the existing row, not a new outcome -- ok to update in place
        }
        return Ok(new CreateBugFromResultResponse(adoResult.Success, adoResult.WorkItemId, adoResult.WorkItemUrl, adoResult.Error));
    }
}