using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Controllers;

// REST counterpart of the save_automation_script / get_automation_scripts MCP tools (see
// McpTools/AutomationScriptMcpTools.cs) -- same IDataStore, same rules. The whole point of this
// controller existing (per explicit instruction: "test case hub ka kya fayda?" otherwise) is
// that a generated script is stored HERE, in Test Case Hub's own database, retrievable
// company/module/suite-wise -- never pushed to the company's own repo.
[ApiController]
[Authorize]
[Route("api/automation-scripts")]
public class AutomationScriptsController : ControllerBase
{
    private readonly IDataStore _store;
    private readonly AutomationGenerationService _generation;
    private readonly ScriptExecutionService _execSvc;
    private readonly NotificationService _notify;
    public AutomationScriptsController(IDataStore store, AutomationGenerationService generation, ScriptExecutionService execSvc, NotificationService notify)
    { _store = store; _generation = generation; _execSvc = execSvc; _notify = notify; }

    private string ActorDisplayName => User.FindFirstValue("displayName") ?? "Unknown";

    [HttpGet]
    public async Task<ActionResult<List<AutomationScriptResponse>>> GetAll([FromQuery] int? companyId, [FromQuery] int? moduleId, [FromQuery] int? suiteId, [FromQuery] string? testCaseId)
    {
        var effective = User.ResolveActingCompanyId(companyId);
        if (effective is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();

        var scripts = await _store.GetAutomationScriptsAsync(effective.Value, moduleId, suiteId, testCaseId);
        return scripts.Select(AutomationScriptResponse.From).ToList();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<AutomationScriptResponse>> GetOne(int id)
    {
        var script = await _store.GetAutomationScriptAsync(id);
        if (script is null) return NotFound("Automation script not found.");
        if (!User.HasCompanyAccess(script.CompanyId)) return Forbid();
        return Ok(AutomationScriptResponse.From(script));
    }

    // Saving always creates a NEW versioned row rather than overwriting -- the storage layer
    // computes Version as (current max for this Company+Module+TestCase+FileName) + 1, so a bad
    // generation can always be compared against, or rolled back to, the previous one.
    [HttpPost]
    public async Task<ActionResult<AutomationScriptResponse>> Save(SaveAutomationScriptRequest req, [FromQuery] int? companyId = null)
    {
        if (!User.CanManageAutomationScripts()) return Forbid();

        var companyIdResolved = User.ResolveActingCompanyId(companyId);
        if (companyIdResolved is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();

        var module = await _store.GetModuleAsync(req.ModuleId);
        if (module is null) return NotFound("Module not found.");
        if (module.CompanyId != companyIdResolved) return BadRequest("ModuleId does not belong to the resolved company.");
        if (string.IsNullOrWhiteSpace(req.FileName)) return BadRequest("FileName is required.");
        if (string.IsNullOrWhiteSpace(req.Content)) return BadRequest("Content is required.");

        var script = new AutomationScript
        {
            CompanyId = companyIdResolved.Value, ModuleId = req.ModuleId, TestCaseId = req.TestCaseId, SuiteId = req.SuiteId,
            FileName = req.FileName.Trim(), Framework = req.Framework ?? "", Content = req.Content,
            GeneratedBy = string.IsNullOrWhiteSpace(req.GeneratedBy) ? ActorDisplayName : req.GeneratedBy,
            SourceRepoRefs = req.SourceRepoRefs ?? ""
        };
        script = await _store.SaveAutomationScriptAsync(script);
        return Ok(AutomationScriptResponse.From(script));
    }

    [HttpPatch("{id:int}/status")]
    public async Task<ActionResult<AutomationScriptResponse>> UpdateStatus(int id, UpdateAutomationScriptStatusRequest req)
    {
        if (!User.CanManageAutomationScripts()) return Forbid();

        var script = await _store.GetAutomationScriptAsync(id);
        if (script is null) return NotFound("Automation script not found.");
        if (!User.HasCompanyAccess(script.CompanyId)) return Forbid();
        if (!AutomationScriptStatus.All.Contains(req.Status)) return BadRequest("Status must be Draft, Reviewed, or Approved.");
        // Approving a script is the exact same significant action as flipping a test case's
        // automationReady flag (it does so automatically, right below) -- so it sits at the
        // same Lead-and-above bar as CanManageAutomationReady, not the lower Contributor bar
        // that covers Draft/Reviewed and every other script edit.
        if (req.Status == AutomationScriptStatus.Approved && !User.CanManageAutomationReady())
            return Forbid();

        script = await _store.UpdateAutomationScriptStatusAsync(id, req.Status);

        // Cascade: an Approved script for a real test case marks that test case
        // automation-ready and points its automationScriptRef at this exact script/version --
        // closing the loop the user asked for between "script is approved" and "test case
        // shows up as automated" instead of leaving the two hand-synced.
        if (req.Status == AutomationScriptStatus.Approved && !string.IsNullOrWhiteSpace(script.TestCaseId))
        {
            var tc = await _store.GetTestCaseAsync(script.TestCaseId);
            if (tc is not null && tc.ModuleId == script.ModuleId)
            {
                tc.AutomationReady = true;
                tc.AutomationScriptRef = $"{script.FileName} (v{script.Version}, AutomationScript #{script.Id})";
                await _store.UpdateTestCaseAsync(tc);
            }
        }

        return Ok(AutomationScriptResponse.From(script));
    }

    // Smoke/Sanity/Regression tagging (see TestTier) -- same Contributor+ bar as other script
    // edits, not the higher Approve bar, since this is just organizational metadata.
    [HttpPatch("{id:int}/tier")]
    public async Task<ActionResult<AutomationScriptResponse>> SetTier(int id, UpdateAutomationScriptTierRequest req)
    {
        if (!User.CanManageAutomationScripts()) return Forbid();
        var script = await _store.GetAutomationScriptAsync(id);
        if (script is null) return NotFound("Automation script not found.");
        if (!User.HasCompanyAccess(script.CompanyId)) return Forbid();
        if (!TestTier.All.Contains(req.TestTier)) return BadRequest("TestTier must be Smoke, Sanity, or Regression.");

        script = await _store.SetTestTierAsync(id, req.TestTier);
        return Ok(AutomationScriptResponse.From(script));
    }
// "Direct API" generation path (agreed alongside the existing MCP-based one): calls Claude
    // server-side using the company's own Anthropic key (CompanyAiSettingsController), grounded
    // in real source code fetched from the module's linked repo(s). Lead-and-above only -- this
    // spends the company's own paid API budget, a materially different bar than manually
    // pasting in a script you already wrote (CanManageAutomationScripts, Contributor+).
    [HttpPost("generate")]
    public async Task<ActionResult> Generate(GenerateAutomationScriptRequest req, [FromQuery] int? companyId = null)
    {
        if (!User.CanGenerateAutomationScript()) return Forbid();
        var effective = User.ResolveActingCompanyId(companyId);
        if (effective is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();

        var outcome = await _generation.GenerateAsync(effective.Value, req.ModuleId, req.TestCaseId, req.Framework, ActorDisplayName);
        if (!outcome.Success) return BadRequest(new { error = outcome.Error, warnings = outcome.Warnings });
        return Ok(new { script = AutomationScriptResponse.From(outcome.Script!), warnings = outcome.Warnings });
    }

    // Batch sibling of Generate, scoped to Module + Layer ("UWMC ka UI Layer" / "UWMC ka API
    // Layer" / "UWMC ka Database Layer" -- exact framing the company asked for instead of one
    // whole-module click). Capped at 5 test cases per call: each item is still a full Anthropic
    // call under the hood, so this bounds cost/blast-radius per request the same way the single
    // -generate path does, while removing the need to click Generate one test case at a time.
    [HttpPost("generate-batch")]
    public async Task<ActionResult<BatchGenerationResponse>> GenerateBatch(GenerateAutomationScriptsBatchRequest req, [FromQuery] int? companyId = null)
    {
        if (!User.CanGenerateAutomationScript()) return Forbid();
        var effective = User.ResolveActingCompanyId(companyId);
        if (effective is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();

        if (req.TestCaseIds is null || req.TestCaseIds.Count == 0)
            return BadRequest("Select at least one test case.");
        if (req.TestCaseIds.Count > 5)
            return BadRequest($"Batch generation is capped at 5 test cases per call (got {req.TestCaseIds.Count}) -- split into smaller groups, e.g. by Layer, then 5 at a time within that Layer.");

        var items = await _generation.GenerateBatchAsync(effective.Value, req.ModuleId, req.Layer, req.VerificationType, req.TestCaseIds, req.Framework, ActorDisplayName);
        var response = new BatchGenerationResponse(
            items.Count, items.Count(i => i.Success), items.Count(i => !i.Success),
            items.Select(i => new BatchGenerationItemResult(i.TestCaseId, i.Success, i.Error, i.Script is null ? null : AutomationScriptResponse.From(i.Script), i.Warnings)).ToList()
        );
        return Ok(response);
    }


    // Sets/replaces the native execution DSL for a script (see ScriptExecutionService) -- same
    // Contributor+ bar as saving/editing the script itself. Validated by attempting a parse
    // before it's stored, so a broken definition never silently sits there until someone hits
    // Run and gets a confusing error.
    [HttpPost("{id:int}/execution-definition")]
    public async Task<ActionResult<AutomationScriptResponse>> SetExecutionDefinition(int id, SetExecutionDefinitionRequest req)
    {
        if (!User.CanManageAutomationScripts()) return Forbid();
        var script = await _store.GetAutomationScriptAsync(id);
        if (script is null) return NotFound("Automation script not found.");
        if (!User.HasCompanyAccess(script.CompanyId)) return Forbid();

        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<ExecStep>>(req.ExecutionDefinitionJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            if (parsed is null || parsed.Count == 0) return BadRequest("ExecutionDefinitionJson must be a non-empty JSON array of steps.");
        }
        catch (Exception ex)
        {
            return BadRequest($"ExecutionDefinitionJson is not valid: {ex.Message}");
        }

        script = await _store.SetExecutionDefinitionAsync(id, req.ExecutionDefinitionJson);
        return Ok(AutomationScriptResponse.From(script));
    }

    // Runs a script NATIVELY, in-process, against a configured EnvironmentTarget -- the whole
    // point being "test case hub se hi complete testing", no external Playwright/Node process
    // and nothing the user has to download and run themselves. Same CanTriggerTestRun bar
    // (Lead+) as attaching a credential to a Test Run -- this is exactly that action, just
    // executed by the interpreter instead of a human/CI.
    [HttpPost("{id:int}/execute")]
    public async Task<ActionResult<ExecuteAutomationScriptResponse>> Execute(int id, ExecuteAutomationScriptRequest req)
    {
        if (!User.CanTriggerTestRun()) return Forbid();
        var script = await _store.GetAutomationScriptAsync(id);
        if (script is null) return NotFound("Automation script not found.");
        if (!User.HasCompanyAccess(script.CompanyId)) return Forbid();

        var env = await _store.GetEnvironmentTargetAsync(req.EnvironmentTargetId);
        if (env is null || env.CompanyId != script.CompanyId) return BadRequest("EnvironmentTargetId not found for this company.");

        EnvironmentCredential? cred = null;
        if (req.EnvironmentCredentialId is not null)
        {
            cred = await _store.GetEnvironmentCredentialAsync(req.EnvironmentCredentialId.Value);
            if (cred is null || cred.EnvironmentTargetId != env.Id) return BadRequest("EnvironmentCredentialId must belong to the selected EnvironmentTargetId.");
        }

        TestRun? run = null;
        if (req.TestRunId is not null)
        {
            run = await _store.GetTestRunAsync(req.TestRunId.Value);
            if (run is null || run.CompanyId != script.CompanyId) return BadRequest("TestRunId not found for this company.");
            // Same Production safety block as the CI-facing results/automated endpoint --
            // Test Case Hub's own native runner is held to the exact same rule.
            if (env.EnvironmentType == Models.EnvironmentType.Production)
                return BadRequest(new { error = $"'{env.Name}' is a Production environment -- automated execution results cannot be recorded against Production." });
        }

        var mode = ParseExecutionMode(req.Mode);
        var outcome = await _execSvc.ExecuteAsync(script, env, cred, mode);

        int? resultId = null;
        if (run is not null && !string.IsNullOrEmpty(script.TestCaseId))
        {
            var result = new TestRunResult
            {
                TestRunId = run.Id, TestCaseId = script.TestCaseId, Platform = null,
                Status = outcome.Status, IsAutomated = true, ExecutedBy = "Automated (Test Case Hub native runner)",
                Notes = outcome.Error ?? string.Join(" | ", outcome.Log.TakeLast(3)),
                RunAttemptKey = $"native:{script.Id}:v{script.Version}:{Guid.NewGuid():N}"
            };
            result = await _store.AddTestRunResultAsync(result);
            resultId = result.Id;
            if (outcome.Status == "Fail")
                await _notify.NotifyAdminsAndLeadsAsync("AutomationFailed", $"Native run failed for {script.TestCaseId} in run '{run.Name}'.");
        }

        return Ok(new ExecuteAutomationScriptResponse(outcome.Passed, outcome.Status, outcome.Log, outcome.Error, resultId));
    }

    // "Run tier" -- runs every Approved, execution-ready script in a Module+TestTier one after
    // another against the same EnvironmentTarget/credential/mode, so a Lead can pick "which
    // KIND of testing" (Smoke vs Sanity vs Regression, Real vs Mock) instead of clicking Run on
    // one script at a time. Same permission/safety bar as the single-script Execute above.
    [HttpPost("execute-batch")]
    public async Task<ActionResult<ExecuteBatchResponse>> ExecuteBatch(ExecuteBatchRequest req, [FromQuery] int? companyId = null)
    {
        if (!User.CanTriggerTestRun()) return Forbid();
        var effective = User.ResolveActingCompanyId(companyId);
        if (effective is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();
        if (!TestTier.All.Contains(req.TestTier)) return BadRequest("TestTier must be Smoke, Sanity, or Regression.");

        var env = await _store.GetEnvironmentTargetAsync(req.EnvironmentTargetId);
        if (env is null || env.CompanyId != effective.Value) return BadRequest("EnvironmentTargetId not found for this company.");

        EnvironmentCredential? cred = null;
        if (req.EnvironmentCredentialId is not null)
        {
            cred = await _store.GetEnvironmentCredentialAsync(req.EnvironmentCredentialId.Value);
            if (cred is null || cred.EnvironmentTargetId != env.Id) return BadRequest("EnvironmentCredentialId must belong to the selected EnvironmentTargetId.");
        }

        TestRun? run = null;
        if (req.TestRunId is not null)
        {
            run = await _store.GetTestRunAsync(req.TestRunId.Value);
            if (run is null || run.CompanyId != effective.Value) return BadRequest("TestRunId not found for this company.");
            if (env.EnvironmentType == Models.EnvironmentType.Production)
                return BadRequest(new { error = $"'{env.Name}' is a Production environment -- automated execution results cannot be recorded against Production." });
        }

        var mode = ParseExecutionMode(req.Mode);
        var candidates = (await _store.GetAutomationScriptsAsync(effective.Value, req.ModuleId, null, null))
            .Where(s => s.TestTier == req.TestTier && s.Status == AutomationScriptStatus.Approved && !string.IsNullOrWhiteSpace(s.ExecutionDefinitionJson))
            .ToList();

        var items = new List<ExecuteBatchItemResult>();
        foreach (var script in candidates)
        {
            var outcome = await _execSvc.ExecuteAsync(script, env, cred, mode);
            int? resultId = null;
            if (run is not null && !string.IsNullOrEmpty(script.TestCaseId))
            {
                var result = new TestRunResult
                {
                    TestRunId = run.Id, TestCaseId = script.TestCaseId, Platform = null,
                    Status = outcome.Status, IsAutomated = true, ExecutedBy = $"Automated (Test Case Hub native runner -- {req.TestTier} batch)",
                    Notes = outcome.Error ?? string.Join(" | ", outcome.Log.TakeLast(3)),
                    RunAttemptKey = $"native-batch:{script.Id}:v{script.Version}:{Guid.NewGuid():N}"
                };
                result = await _store.AddTestRunResultAsync(result);
                resultId = result.Id;
            }
            items.Add(new ExecuteBatchItemResult(script.Id, script.TestCaseId, script.FileName, outcome.Passed, outcome.Status, outcome.Error, resultId));
        }

        var failed = items.Count(i => !i.Passed);
        if (failed > 0)
            await _notify.NotifyAdminsAndLeadsAsync("AutomationFailed", $"{req.TestTier} batch run: {failed}/{items.Count} script(s) failed in module {req.ModuleId}.");

        return Ok(new ExecuteBatchResponse(items.Count, items.Count - failed, failed, 0, items));
    }

    private static ExecutionMode ParseExecutionMode(string? mode) =>
        string.Equals(mode, "Mock", StringComparison.OrdinalIgnoreCase) ? ExecutionMode.Mock : ExecutionMode.Real;
}
