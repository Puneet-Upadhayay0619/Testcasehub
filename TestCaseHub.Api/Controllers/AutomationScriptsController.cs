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
    public AutomationScriptsController(IDataStore store, AutomationGenerationService generation) { _store = store; _generation = generation; }

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
}
