using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using ModelContextProtocol.Server;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.McpTools;

// One-to-one mirror of the REST controllers (same IDataStore, same TestCaseValidation, same
// history/versioning behaviour) but exposed as MCP tools instead of HTTP verbs+routes, so the
// Cowork artifact UI and/or Claude directly can call the exact same business logic. Every tool
// method takes a ClaimsPrincipal parameter -- the MCP SDK resolves this from the authenticated
// HTTP request (same JWT bearer scheme as the REST API), so CreatedBy/UpdatedBy attribution
// works exactly like it does today, without a separate login step inside the tool call itself.
[McpServerToolType]
public class TestCaseMcpTools
{
    private readonly IDataStore _store;
    public TestCaseMcpTools(IDataStore store) => _store = store;

    private static string DisplayNameOf(ClaimsPrincipal user) =>
        user.FindFirstValue("displayName") ?? user.FindFirstValue(ClaimsIdentity.DefaultNameClaimType) ?? user.FindFirstValue(ClaimTypes.Email) ?? "Unknown";

    private static readonly Dictionary<string, string> LayerCodes = new()
    {
        ["Dashboard"] = "DSH", ["App-API"] = "API", ["App"] = "APP"
    };

    // ---------- modules ----------

    [McpServerTool(Name = "list_modules"), Description("List every module (feature area) in the Test Case Hub, with how many non-deprecated test cases each has.")]
    public async Task<List<ModuleResponse>> ListModules()
    {
        var modules = await _store.GetModulesAsync();
        var counts = await _store.GetTestCaseCountsByModuleAsync();
        return modules.Select(m => new ModuleResponse(
            m.Id, m.Name, m.Code, m.Description, m.Owner, m.Status, m.CreatedAt,
            counts.TryGetValue(m.Id, out var c) ? c : 0
        )).ToList();
    }

    [McpServerTool(Name = "create_module"), Description("Create a new module (feature area). Code is a short uppercase identifier used in generated test case IDs, e.g. 'LOY' for Loyalty.")]
    public async Task<object> CreateModule(
        ClaimsPrincipal user,
        [Description("Module name, e.g. 'Loyalty Points'")] string name,
        [Description("Short uppercase code used in test case IDs, e.g. 'LOY'")] string code,
        [Description("Optional description of what this module covers")] string? description = null,
        [Description("Optional QA owner name")] string? owner = null,
        [Description("Active or Deprecated -- defaults to Active")] string? status = null)
    {
        // Same rule, same helper, as the REST /api/modules POST endpoint — Contributor and above.
        if (!user.CanCreateModule())
            return new { error = "You do not have permission to create modules (Contributor role or above required)." };

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(code))
            return new { error = "Module name and code are required." };

        var normalizedCode = code.Trim().ToUpperInvariant();
        if (await _store.ModuleCodeExistsAsync(normalizedCode))
            return new { error = $"A module with code '{normalizedCode}' already exists." };

        var module = new Module
        {
            Name = name.Trim(), Code = normalizedCode, Description = description ?? "",
            Owner = owner ?? "", Status = string.IsNullOrWhiteSpace(status) ? "Active" : status
        };
        module = await _store.CreateModuleAsync(module);
        return new ModuleResponse(module.Id, module.Name, module.Code, module.Description, module.Owner, module.Status, module.CreatedAt, 0);
    }

    [McpServerTool(Name = "add_task_link"), Description("Link a module to a real Dashboard/App-API/App task/ticket (e.g. an Azure DevOps item) for traceability.")]
    public async Task<object> AddTaskLink(
        int moduleId,
        [Description("Dashboard, App-API, or App")] string layer,
        [Description("Source project name, e.g. 'Dashboard project'")] string adoProject,
        string adoTaskId,
        string? adoTaskTitle = null,
        string? adoTaskUrl = null)
    {
        var module = await _store.GetModuleAsync(moduleId);
        if (module is null) return new { error = "Module not found." };
        if (string.IsNullOrWhiteSpace(adoTaskId)) return new { error = "Task ID is required." };

        var link = new TaskLink
        {
            ModuleId = moduleId, Layer = layer, AdoProject = adoProject,
            AdoTaskId = adoTaskId.Trim(), AdoTaskTitle = adoTaskTitle ?? "", AdoTaskUrl = adoTaskUrl ?? ""
        };
        link = await _store.CreateTaskLinkAsync(link);
        return new TaskLinkResponse(link.Id, link.ModuleId, link.Layer, link.AdoProject, link.AdoTaskId, link.AdoTaskTitle, link.AdoTaskUrl, link.LinkedAt);
    }

    [McpServerTool(Name = "list_task_links"), Description("List the linked tasks/tickets for a module.")]
    public async Task<List<TaskLinkResponse>> ListTaskLinks(int moduleId)
    {
        var links = await _store.GetTaskLinksAsync(moduleId);
        return links.Select(l => new TaskLinkResponse(l.Id, l.ModuleId, l.Layer, l.AdoProject, l.AdoTaskId, l.AdoTaskTitle, l.AdoTaskUrl, l.LinkedAt)).ToList();
    }

    // ---------- lookups ----------

    [McpServerTool(Name = "list_priorities"), Description("List all valid priority values (base P1-P4 plus any custom ones teammates have added).")]
    public Task<List<string>> ListPriorities() => _store.GetPrioritiesAsync();

    [McpServerTool(Name = "add_priority"), Description("Add a new custom priority value (e.g. 'P0') so it's usable by everyone from now on.")]
    public async Task<object> AddPriority(string value)
    {
        value = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value)) return new { error = "Value is required." };
        if (await _store.PriorityExistsAsync(value)) return new { error = "This priority already exists." };
        await _store.AddPriorityAsync(value);
        return new { ok = true };
    }

    [McpServerTool(Name = "list_statuses"), Description("List all valid status values (base Draft/Reviewed/Active/Deprecated plus any custom ones).")]
    public Task<List<string>> ListStatuses() => _store.GetStatusesAsync();

    [McpServerTool(Name = "add_status"), Description("Add a new custom status value so it's usable by everyone from now on.")]
    public async Task<object> AddStatus(string value)
    {
        value = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value)) return new { error = "Value is required." };
        if (await _store.StatusExistsAsync(value)) return new { error = "This status already exists." };
        await _store.AddStatusAsync(value);
        return new { ok = true };
    }

    // ---------- test cases ----------

    [McpServerTool(Name = "list_test_cases"), Description("List test cases, optionally filtered by module, task area (layer), verification type, status, priority, or a free-text search.")]
    public async Task<List<TestCaseResponse>> ListTestCases(
        int? moduleId = null,
        [Description("Dashboard, App-API, or App")] string? layer = null,
        [Description("UI, Database, or API-Contract")] string? verificationType = null,
        string? status = null,
        string? priority = null,
        string? search = null)
    {
        var results = await _store.GetTestCasesAsync(new TestCaseFilter(moduleId, layer, verificationType, status, priority, search));
        return results.Select(TestCaseResponse.From).ToList();
    }

    [McpServerTool(Name = "get_test_case"), Description("Get one test case by its ID (e.g. TC-LOY-DSH-001).")]
    public async Task<object> GetTestCase(string id)
    {
        var tc = await _store.GetTestCaseAsync(id);
        return tc is null ? new { error = "Not found." } : TestCaseResponse.From(tc);
    }

    [McpServerTool(Name = "create_test_case"), Description(
        "Create a test case. Every step needs BOTH a non-empty action and a non-empty expected_result -- " +
        "a step you can't assert can't be automated later, so incomplete steps are rejected rather than half-saved. " +
        "Returns the created test case including its generated ID (TC-<module code>-<area code>-###), or an object with an 'error'/'missing' field explaining what's wrong.")]
    public async Task<object> CreateTestCase(
        int moduleId,
        [Description("Dashboard, App-API, or App")] string layer,
        [Description("UI, Database, or API-Contract")] string verificationType,
        string title,
        string? preconditions,
        [Description("Each step needs both action and expected_result")] List<TestCaseStepDto> steps,
        [Description("P1-P4 or a custom priority added via add_priority")] string priority,
        [Description("Functional, Negative, Edge Case, or Regression")] string type,
        [Description("Draft, Reviewed, Active, Deprecated, or a custom status")] string status,
        List<string>? tags,
        bool automationReady,
        string? automationScriptRef,
        ClaimsPrincipal user,
        [Description("Optional note for the history log, e.g. 'Generated from backlog PBI-02'")] string? historyComment = null)
    {
        var req = new TestCaseCreateRequest(moduleId, layer, verificationType, title, preconditions ?? "",
            steps ?? new(), priority, type, status, tags ?? new(), automationReady, automationScriptRef ?? "", historyComment);
        var module = await _store.GetModuleAsync(req.ModuleId);
        var missing = TestCaseValidation.Validate(req, module is not null);
        if (missing.Count > 0) return new { missing };

        var layerCode = LayerCodes.GetValueOrDefault(req.Layer, "??");
        var prefix = $"TC-{module!.Code}-{layerCode}-";
        var existingCount = await _store.CountTestCasesWithPrefixAsync(prefix);
        var newId = $"{prefix}{(existingCount + 1):D3}";

        var actorName = DisplayNameOf(user);
        var attemptedSteps = req.Steps.Where(s => !string.IsNullOrWhiteSpace(s.Action) || !string.IsNullOrWhiteSpace(s.ExpectedResult)).ToList();

        var tc = new TestCase
        {
            Id = newId, ModuleId = req.ModuleId, Layer = req.Layer, VerificationType = req.VerificationType,
            Title = req.Title.Trim(), Preconditions = req.Preconditions ?? "",
            Priority = req.Priority, Type = req.Type, Status = req.Status,
            AutomationReady = req.AutomationReady, AutomationScriptRef = req.AutomationScriptRef ?? "",
            CreatedBy = actorName, UpdatedBy = actorName, Version = 1
        };
        tc.Steps = attemptedSteps.Select((s, i) => new TestCaseStep { StepNo = i + 1, Action = s.Action, ExpectedResult = s.ExpectedResult }).ToList();
        tc.Tags = req.Tags ?? new();

        tc = await _store.CreateTestCaseAsync(tc);
        await _store.AddHistoryAsync(new TestCaseHistory
        {
            TestCaseId = tc.Id, ChangedBy = actorName, ChangeType = "Created",
            OldSnapshotJson = null, NewSnapshotJson = JsonSerializer.Serialize(TestCaseResponse.From(tc)),
            Comment = string.IsNullOrWhiteSpace(req.HistoryComment) ? "Created via MCP" : req.HistoryComment
        });
        return TestCaseResponse.From(tc);
    }

    [McpServerTool(Name = "update_test_case"), Description("Update an existing test case by ID. Same validation as create_test_case -- every attempted step needs both action and expected_result.")]
    public async Task<object> UpdateTestCase(
        string id,
        int moduleId,
        string layer,
        string verificationType,
        string title,
        string? preconditions,
        List<TestCaseStepDto> steps,
        string priority,
        string type,
        string status,
        List<string>? tags,
        bool automationReady,
        string? automationScriptRef,
        ClaimsPrincipal user,
        string? historyComment = null)
    {
        var tc = await _store.GetTestCaseAsync(id);
        if (tc is null) return new { error = "Not found." };

        var req = new TestCaseCreateRequest(moduleId, layer, verificationType, title, preconditions ?? "",
            steps ?? new(), priority, type, status, tags ?? new(), automationReady, automationScriptRef ?? "", historyComment);
        var module = await _store.GetModuleAsync(req.ModuleId);
        var missing = TestCaseValidation.Validate(req, module is not null);
        if (missing.Count > 0) return new { missing };

        var actorName = DisplayNameOf(user);
        var oldSnapshot = JsonSerializer.Serialize(TestCaseResponse.From(tc));
        var attemptedSteps = req.Steps.Where(s => !string.IsNullOrWhiteSpace(s.Action) || !string.IsNullOrWhiteSpace(s.ExpectedResult)).ToList();

        tc.ModuleId = req.ModuleId; tc.Layer = req.Layer; tc.VerificationType = req.VerificationType;
        tc.Title = req.Title.Trim(); tc.Preconditions = req.Preconditions ?? "";
        tc.Steps = attemptedSteps.Select((s, i) => new TestCaseStep { StepNo = i + 1, Action = s.Action, ExpectedResult = s.ExpectedResult }).ToList();
        tc.Priority = req.Priority; tc.Type = req.Type; tc.Status = req.Status;
        tc.Tags = req.Tags ?? new();
        tc.AutomationReady = req.AutomationReady; tc.AutomationScriptRef = req.AutomationScriptRef ?? "";
        tc.UpdatedBy = actorName; tc.UpdatedAt = DateTime.UtcNow; tc.Version += 1;

        tc = await _store.UpdateTestCaseAsync(tc);
        await _store.AddHistoryAsync(new TestCaseHistory
        {
            TestCaseId = tc.Id, ChangedBy = actorName, ChangeType = "Updated",
            OldSnapshotJson = oldSnapshot, NewSnapshotJson = JsonSerializer.Serialize(TestCaseResponse.From(tc)),
            Comment = string.IsNullOrWhiteSpace(req.HistoryComment) ? "Edited via MCP" : req.HistoryComment
        });
        return TestCaseResponse.From(tc);
    }

    [McpServerTool(Name = "deprecate_test_case"), Description("Mark a test case as Deprecated.")]
    public async Task<object> DeprecateTestCase(string id, ClaimsPrincipal user)
    {
        var tc = await _store.GetTestCaseAsync(id);
        if (tc is null) return new { error = "Not found." };

        var actorName = DisplayNameOf(user);
        var oldSnapshot = JsonSerializer.Serialize(TestCaseResponse.From(tc));
        tc.Status = "Deprecated"; tc.UpdatedBy = actorName; tc.UpdatedAt = DateTime.UtcNow; tc.Version += 1;

        tc = await _store.UpdateTestCaseAsync(tc);
        await _store.AddHistoryAsync(new TestCaseHistory
        {
            TestCaseId = tc.Id, ChangedBy = actorName, ChangeType = "Deprecated",
            OldSnapshotJson = oldSnapshot, NewSnapshotJson = JsonSerializer.Serialize(TestCaseResponse.From(tc)),
            Comment = "Marked deprecated via MCP"
        });
        return TestCaseResponse.From(tc);
    }

    [McpServerTool(Name = "get_test_case_history"), Description("Get the full change history (old/new snapshots) for a test case.")]
    public async Task<List<HistoryResponse>> GetTestCaseHistory(string id)
    {
        var hist = await _store.GetHistoryAsync(id);
        return hist.Select(h => new HistoryResponse(h.Id, h.TestCaseId, h.ChangedBy, h.ChangedAt, h.ChangeType, h.OldSnapshotJson, h.NewSnapshotJson, h.Comment)).ToList();
    }
}
