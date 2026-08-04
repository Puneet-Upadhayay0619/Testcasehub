using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/testcases")]
public class TestCasesController : ControllerBase
{
    private readonly IDataStore _store;
    public TestCasesController(IDataStore store) => _store = store;

    private string CurrentUserDisplayName => User.FindFirstValue("displayName") ?? User.FindFirstValue(ClaimTypes.Email) ?? "Unknown";

    private static readonly Dictionary<string, string> LayerCodes = new()
    {
        ["Dashboard"] = "DSH", ["App-API"] = "API", ["App"] = "APP"
    };

    // The evidence-backed flag-integrity gate agreed in planning: automationReady can only be
    // (or remain) true if (a) the acting user is Lead/Admin, and (b) there's actual proof —
    // a script reference OR a filled-in structured API/DB config — attached. This runs on
    // every Create/Update, not just a UI checkbox, so the exact scenario from planning ("test
    // case not really automation-ready but the tag gets applied anyway") can't happen through
    // the API or MCP either.
    private string? ValidateAutomationGate(TestCaseCreateRequest req)
    {
        var cfg = req.AutomationConfig;
        if (!string.IsNullOrWhiteSpace(cfg?.DbQuery) && !DbQuerySafety.IsSelectOnly(cfg.DbQuery))
            return "The configured DB query must be a SELECT statement only (automation is not allowed to write to the target database).";

        if (req.AutomationReady)
        {
            if (!User.CanManageAutomationReady())
                return "Only Lead or Admin can set automationReady = true.";
            var hasScript = !string.IsNullOrWhiteSpace(req.AutomationScriptRef);
            var hasApiConfig = !string.IsNullOrWhiteSpace(cfg?.ApiEndpoint);
            var hasDbConfig = !string.IsNullOrWhiteSpace(cfg?.DbQuery);
            if (!hasScript && !hasApiConfig && !hasDbConfig)
                return "automationReady cannot be set to true without evidence — attach an automation script reference, or fill in the API/DB automation config, first.";
        }
        return null;
    }

    private static string SerializeAutomationConfig(AutomationConfigDto? cfg) =>
        System.Text.Json.JsonSerializer.Serialize(cfg ?? new AutomationConfigDto(null, null, null, null, null));

    [HttpGet]
    public async Task<ActionResult<List<TestCaseResponse>>> GetAll(
        [FromQuery] int? moduleId, [FromQuery] string? layer, [FromQuery] string? verificationType,
        [FromQuery] string? status, [FromQuery] string? priority, [FromQuery] string? search)
    {
        var results = await _store.GetTestCasesAsync(new TestCaseFilter(moduleId, layer, verificationType, status, priority, search));
        return results.Select(TestCaseResponse.From).ToList();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<TestCaseResponse>> GetOne(string id)
    {
        var tc = await _store.GetTestCaseAsync(id);
        if (tc is null) return NotFound();
        return TestCaseResponse.From(tc);
    }

    [HttpPost]
    public async Task<ActionResult> Create(TestCaseCreateRequest req)
    {
        var module = await _store.GetModuleAsync(req.ModuleId);
        var missing = TestCaseValidation.Validate(req, module is not null);
        if (missing.Count > 0) return BadRequest(new { missing });

        var gateError = ValidateAutomationGate(req);
        if (gateError is not null) return BadRequest(new { error = gateError });

        var layerCode = LayerCodes.GetValueOrDefault(req.Layer, "??");
        var prefix = $"TC-{module!.Code}-{layerCode}-";
        var existingCount = await _store.CountTestCasesWithPrefixAsync(prefix);
        var newId = $"{prefix}{(existingCount + 1):D3}";

        var attemptedSteps = req.Steps.Where(s => !string.IsNullOrWhiteSpace(s.Action) || !string.IsNullOrWhiteSpace(s.ExpectedResult)).ToList();

        var tc = new TestCase
        {
            Id = newId, ModuleId = req.ModuleId, Layer = req.Layer, VerificationType = req.VerificationType,
            Title = req.Title.Trim(), Preconditions = req.Preconditions ?? "",
            Priority = req.Priority, Type = req.Type, Status = req.Status,
            AutomationReady = req.AutomationReady, AutomationScriptRef = req.AutomationScriptRef ?? "",
            CreatedBy = CurrentUserDisplayName, UpdatedBy = CurrentUserDisplayName, Version = 1
        };
        tc.LinkedModuleIds = req.LinkedModuleIds ?? new();
        tc.Steps = attemptedSteps.Select((s, i) => new TestCaseStep { StepNo = i + 1, Action = s.Action, ExpectedResult = s.ExpectedResult }).ToList();
        tc.Tags = req.Tags ?? new();
        tc.AutomationConfigJson = SerializeAutomationConfig(req.AutomationConfig);
        tc.SelectorStability = req.SelectorStability ?? "";

        tc = await _store.CreateTestCaseAsync(tc);
        await _store.AddHistoryAsync(new TestCaseHistory
        {
            TestCaseId = tc.Id, ChangedBy = CurrentUserDisplayName, ChangeType = "Created",
            OldSnapshotJson = null, NewSnapshotJson = JsonSerializer.Serialize(TestCaseResponse.From(tc)),
            Comment = string.IsNullOrWhiteSpace(req.HistoryComment) ? "Created via Test Case Hub" : req.HistoryComment
        });
        return Ok(TestCaseResponse.From(tc));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> Update(string id, TestCaseCreateRequest req)
    {
        var tc = await _store.GetTestCaseAsync(id);
        if (tc is null) return NotFound();

        var module = await _store.GetModuleAsync(req.ModuleId);
        var missing = TestCaseValidation.Validate(req, module is not null);
        if (missing.Count > 0) return BadRequest(new { missing });

        var gateError = ValidateAutomationGate(req);
        if (gateError is not null) return BadRequest(new { error = gateError });

        var oldSnapshot = JsonSerializer.Serialize(TestCaseResponse.From(tc));

        var attemptedSteps = req.Steps.Where(s => !string.IsNullOrWhiteSpace(s.Action) || !string.IsNullOrWhiteSpace(s.ExpectedResult)).ToList();

        tc.ModuleId = req.ModuleId; tc.Layer = req.Layer; tc.VerificationType = req.VerificationType;
        tc.Title = req.Title.Trim(); tc.Preconditions = req.Preconditions ?? "";
        tc.Steps = attemptedSteps.Select((s, i) => new TestCaseStep { StepNo = i + 1, Action = s.Action, ExpectedResult = s.ExpectedResult }).ToList();
        tc.Priority = req.Priority; tc.Type = req.Type; tc.Status = req.Status;
        tc.Tags = req.Tags ?? new();
        tc.AutomationReady = req.AutomationReady; tc.AutomationScriptRef = req.AutomationScriptRef ?? "";
        tc.LinkedModuleIds = req.LinkedModuleIds ?? tc.LinkedModuleIds;
        if (req.AutomationConfig is not null) tc.AutomationConfigJson = SerializeAutomationConfig(req.AutomationConfig);
        if (req.SelectorStability is not null) tc.SelectorStability = req.SelectorStability;
        tc.UpdatedBy = CurrentUserDisplayName; tc.UpdatedAt = DateTime.UtcNow; tc.Version += 1;

        tc = await _store.UpdateTestCaseAsync(tc);
        await _store.AddHistoryAsync(new TestCaseHistory
        {
            TestCaseId = tc.Id, ChangedBy = CurrentUserDisplayName, ChangeType = "Updated",
            OldSnapshotJson = oldSnapshot, NewSnapshotJson = JsonSerializer.Serialize(TestCaseResponse.From(tc)),
            Comment = string.IsNullOrWhiteSpace(req.HistoryComment) ? "Edited via Test Case Hub" : req.HistoryComment
        });
        return Ok(TestCaseResponse.From(tc));
    }

    [HttpPost("{id}/deprecate")]
    public async Task<ActionResult> Deprecate(string id)
    {
        var tc = await _store.GetTestCaseAsync(id);
        if (tc is null) return NotFound();

        var oldSnapshot = JsonSerializer.Serialize(TestCaseResponse.From(tc));
        tc.Status = "Deprecated"; tc.UpdatedBy = CurrentUserDisplayName; tc.UpdatedAt = DateTime.UtcNow; tc.Version += 1;

        tc = await _store.UpdateTestCaseAsync(tc);
        await _store.AddHistoryAsync(new TestCaseHistory
        {
            TestCaseId = tc.Id, ChangedBy = CurrentUserDisplayName, ChangeType = "Deprecated",
            OldSnapshotJson = oldSnapshot, NewSnapshotJson = JsonSerializer.Serialize(TestCaseResponse.From(tc)),
            Comment = "Marked deprecated"
        });
        return Ok(TestCaseResponse.From(tc));
    }

    [HttpGet("{id}/history")]
    public async Task<ActionResult<List<HistoryResponse>>> GetHistory(string id)
    {
        var hist = await _store.GetHistoryAsync(id);
        return hist.Select(h => new HistoryResponse(h.Id, h.TestCaseId, h.ChangedBy, h.ChangedAt, h.ChangeType, h.OldSnapshotJson, h.NewSnapshotJson, h.Comment)).ToList();
    }
    // ---- Phase 4: duplicate detection, bulk edit, history diff ----

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        { "the","a","an","to","of","and","or","for","is","are","on","in","with","should","verify","that","test","case" };

    private static HashSet<string> TitleWords(string title) =>
        title.ToLowerInvariant().Split(new[] { ' ', '-', '_', '/', ',', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !StopWords.Contains(w)).ToHashSet();

    // Word-overlap (Jaccard) similarity on titles, scoped to the same module (+layer, if given)
    // — good enough to flag "you may have already written this" without any external NLP
    // dependency. Threshold of 0.6 was chosen to catch near-duplicates while avoiding false
    // positives between merely-related test cases.
    [HttpGet("duplicates")]
    public async Task<ActionResult<List<DuplicateGroup>>> FindDuplicates([FromQuery] int? moduleId, [FromQuery] string? layer)
    {
        var all = await _store.GetTestCasesAsync(new TestCaseFilter(moduleId, layer, null, null, null, null));
        var groups = new List<DuplicateGroup>();
        var consumed = new HashSet<string>();

        foreach (var byModule in all.GroupBy(t => t.ModuleId))
        {
            var list = byModule.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                if (consumed.Contains(list[i].Id)) continue;
                var wordsI = TitleWords(list[i].Title);
                if (wordsI.Count == 0) continue;
                var group = new List<TestCase> { list[i] };
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (consumed.Contains(list[j].Id)) continue;
                    var wordsJ = TitleWords(list[j].Title);
                    if (wordsJ.Count == 0) continue;
                    var overlap = (double)wordsI.Intersect(wordsJ).Count() / wordsI.Union(wordsJ).Count();
                    if (overlap >= 0.6) group.Add(list[j]);
                }
                if (group.Count > 1)
                {
                    foreach (var g in group) consumed.Add(g.Id);
                    groups.Add(new DuplicateGroup($"similar-title-module-{byModule.Key}", group.Select(TestCaseResponse.From).ToList()));
                }
            }
        }
        return groups;
    }

    // Applies Priority/Status/tag add-remove to many test cases in one call, writing exactly
    // one history entry per changed case so the audit trail stays intact.
    [HttpPost("bulk-edit")]
    public async Task<ActionResult<BulkEditResult>> BulkEdit(BulkEditRequest req)
    {
        var updated = new List<string>();
        var notFound = new List<string>();

        foreach (var id in req.Ids ?? new())
        {
            var tc = await _store.GetTestCaseAsync(id);
            if (tc is null) { notFound.Add(id); continue; }

            var oldSnapshot = System.Text.Json.JsonSerializer.Serialize(TestCaseResponse.From(tc));

            if (!string.IsNullOrWhiteSpace(req.Priority)) tc.Priority = req.Priority;
            if (!string.IsNullOrWhiteSpace(req.Status)) tc.Status = req.Status;
            if (req.AddTags is { Count: > 0 })
            {
                var tags = tc.Tags;
                foreach (var t in req.AddTags) if (!tags.Contains(t)) tags.Add(t);
                tc.Tags = tags;
            }
            if (req.RemoveTags is { Count: > 0 })
                tc.Tags = tc.Tags.Where(t => !req.RemoveTags.Contains(t)).ToList();

            tc.UpdatedBy = CurrentUserDisplayName; tc.UpdatedAt = DateTime.UtcNow; tc.Version += 1;
            tc = await _store.UpdateTestCaseAsync(tc);

            await _store.AddHistoryAsync(new TestCaseHistory
            {
                TestCaseId = tc.Id, ChangedBy = CurrentUserDisplayName, ChangeType = "BulkEdited",
                OldSnapshotJson = oldSnapshot, NewSnapshotJson = System.Text.Json.JsonSerializer.Serialize(TestCaseResponse.From(tc)),
                Comment = string.IsNullOrWhiteSpace(req.HistoryComment) ? "Bulk edited" : req.HistoryComment
            });
            updated.Add(id);
        }
        return new BulkEditResult(updated, notFound);
    }

    // Field-by-field diff between two history snapshots (both are serialized TestCaseResponse).
    // Steps/Tags/LinkedModuleIds are compared as whole values (not step-by-step) — good enough
    // to see AT A GLANCE what changed without re-deriving a full text-diff algorithm.
    [HttpGet("{id}/history/{historyId:int}/diff")]
    public async Task<ActionResult<List<HistoryDiffEntry>>> GetHistoryDiff(string id, int historyId)
    {
        var hist = await _store.GetHistoryAsync(id);
        var entry = hist.FirstOrDefault(h => h.Id == historyId);
        if (entry is null) return NotFound();

        var diffs = new List<HistoryDiffEntry>();
        System.Text.Json.JsonElement? oldDoc = entry.OldSnapshotJson is null ? null : System.Text.Json.JsonDocument.Parse(entry.OldSnapshotJson).RootElement;
        var newDoc = System.Text.Json.JsonDocument.Parse(entry.NewSnapshotJson).RootElement;

        var fieldNames = newDoc.EnumerateObject().Select(p => p.Name).ToList();
        foreach (var field in fieldNames)
        {
            string? oldVal = oldDoc?.TryGetProperty(field, out var ov) == true ? ov.ToString() : null;
            string? newVal = newDoc.TryGetProperty(field, out var nv) ? nv.ToString() : null;
            if (oldVal != newVal) diffs.Add(new HistoryDiffEntry(field, oldVal, newVal));
        }
        return diffs;
    }
}