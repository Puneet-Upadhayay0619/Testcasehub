using System.ComponentModel;
using System.Security.Claims;
using ModelContextProtocol.Server;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.McpTools;

// The AI-generation architecture agreed in planning, made concrete: generation itself happens
// OUTSIDE this API, in an MCP-connected Claude session that has BOTH Test Case Hub's own MCP
// (this file) AND the company's repo MCP (GitHub/Azure DevOps) open at once. This file's job is
// only the two ends of that flow -- (1) tell Claude WHERE the real code lives for a module
// (list_module_repo_links, read-only, never returns the access token) and (2) SAVE the script
// Claude produces back into Test Case Hub's own database (save_automation_script), never into
// the company's repo -- "test case hub ka kya fayda?" otherwise, per explicit instruction.
[McpServerToolType]
public class AutomationScriptMcpTools
{
    private readonly IDataStore _store;
    private readonly AutomationGenerationService _generation;
    public AutomationScriptMcpTools(IDataStore store, AutomationGenerationService generation) { _store = store; _generation = generation; }

    private static string DisplayNameOf(ClaimsPrincipal user) =>
        user.FindFirstValue("displayName") ?? user.FindFirstValue(ClaimsIdentity.DefaultNameClaimType) ?? user.FindFirstValue(ClaimTypes.Email) ?? "Unknown";

    [McpServerTool(Name = "list_module_repo_links"),
     Description("List the real GitHub/Azure DevOps repo(s) linked to a module -- host, org/project, repo name, branch, and base path -- so a connected repo MCP (GitHub or Azure DevOps) can be pointed at the right place instead of guessing. Never returns the stored access token; that PAT is used only by Test Case Hub's own backend, never exposed here.")]
    public async Task<object> ListModuleRepoLinks(ClaimsPrincipal user, int moduleId)
    {
        var module = await _store.GetModuleAsync(moduleId);
        if (module is null) return new { error = "Module not found." };
        if (!user.HasCompanyAccess(module.CompanyId)) return new { error = "You do not have access to this module's company." };

        var links = await _store.GetModuleRepoLinksAsync(moduleId);
        if (links.Count == 0)
            return new { error = $"No repo linked to module {moduleId} yet. Ask an Admin to add one via POST /api/modules/{moduleId}/repo-links before generating automation for this module." };
        return links.Select(ModuleRepoLinkResponse.From).ToList();
    }

    [McpServerTool(Name = "get_automation_scripts"),
     Description("List AI-generated automation scripts stored in Test Case Hub, filterable by module/suite/testCaseId. Use this before generating a new script to check whether one already exists for this test case -- generating one always adds a new version rather than overwriting, so check first to avoid duplicate versions of the same thing.")]
    public async Task<object> GetAutomationScripts(ClaimsPrincipal user, int? companyId = null, int? moduleId = null, int? suiteId = null, string? testCaseId = null)
    {
        var effective = user.ResolveActingCompanyId(companyId);
        if (effective is null) return new { error = "SuperAdmin has no single company -- pass companyId explicitly." };

        var scripts = await _store.GetAutomationScriptsAsync(effective.Value, moduleId, suiteId, testCaseId);
        return scripts.Select(AutomationScriptResponse.From).ToList();
    }

    [McpServerTool(Name = "save_automation_script"),
     Description("Save an AI-generated automation script into Test Case Hub's own database -- company/module/(optional suite)/test-case scoped, retrievable later via get_automation_scripts. Always creates a new version rather than overwriting an existing one for the same file. Requires Contributor role or above (same bar as editing test cases). This is the ONLY place a generated script should be written to -- never commit it to the company's own repo.")]
    public async Task<object> SaveAutomationScript(
        ClaimsPrincipal user,
        int moduleId,
        [Description("File name for the generated script, e.g. 'TC-A-visibility-toggle.spec.ts'")] string fileName,
        [Description("The full generated script content")] string content,
        [Description("Real Test Case Hub test case ID this script automates, e.g. 'TC-UWMC-DSH-012' (optional)")] string? testCaseId = null,
        [Description("Test suite this script belongs to, if any (optional)")] int? suiteId = null,
        [Description("e.g. 'Playwright-TypeScript' (optional)")] string? framework = null,
        [Description("Comma-separated ModuleRepoLink Ids that fed this generation (optional, for traceability)")] string? sourceRepoRefs = null,
        int? companyId = null)
    {
        if (!user.CanManageAutomationScripts())
            return new { error = "You do not have permission to save automation scripts (Contributor role or above required)." };

        var effective = user.ResolveActingCompanyId(companyId);
        if (effective is null) return new { error = "SuperAdmin has no single company -- pass companyId explicitly." };

        var module = await _store.GetModuleAsync(moduleId);
        if (module is null) return new { error = "Module not found." };
        if (module.CompanyId != effective) return new { error = "That module does not belong to the resolved company." };
        if (string.IsNullOrWhiteSpace(fileName)) return new { error = "fileName is required." };
        if (string.IsNullOrWhiteSpace(content)) return new { error = "content is required." };

        var script = new AutomationScript
        {
            CompanyId = effective.Value, ModuleId = moduleId, TestCaseId = testCaseId, SuiteId = suiteId,
            FileName = fileName.Trim(), Framework = framework ?? "", Content = content,
            GeneratedBy = $"AI (Claude via MCP) -- {DisplayNameOf(user)}", SourceRepoRefs = sourceRepoRefs ?? ""
        };
        script = await _store.SaveAutomationScriptAsync(script);
        return AutomationScriptResponse.From(script);
    }

    [McpServerTool(Name = "update_automation_script_status"),
     Description("Move a saved automation script through Draft -> Reviewed -> Approved. Requires Contributor role or above -- moving to Approved specifically requires Lead or above, since that also marks the linked test case automation-ready. Approving a script for a real test case automatically sets that test case's automationReady=true and automationScriptRef to point at this script/version.")]
    public async Task<object> UpdateAutomationScriptStatus(ClaimsPrincipal user, int scriptId, [Description("Draft, Reviewed, or Approved")] string status)
    {
        if (!user.CanManageAutomationScripts())
            return new { error = "You do not have permission to update automation scripts (Contributor role or above required)." };
        if (!AutomationScriptStatus.All.Contains(status))
            return new { error = "status must be Draft, Reviewed, or Approved." };
        // Same bar as Permissions.CanManageAutomationReady -- approving IS flipping that flag.
        if (status == AutomationScriptStatus.Approved && !user.CanManageAutomationReady())
            return new { error = "Moving a script to Approved requires Lead role or above (it also marks the linked test case automation-ready)." };

        var existing = await _store.GetAutomationScriptAsync(scriptId);
        if (existing is null) return new { error = "Automation script not found." };
        if (!user.HasCompanyAccess(existing.CompanyId)) return new { error = "You do not have access to this script's company." };

        var script = await _store.UpdateAutomationScriptStatusAsync(scriptId, status);

        string? testCaseUpdateNote = null;
        if (status == AutomationScriptStatus.Approved && !string.IsNullOrWhiteSpace(script.TestCaseId))
        {
            var tc = await _store.GetTestCaseAsync(script.TestCaseId);
            if (tc is not null && tc.ModuleId == script.ModuleId)
            {
                tc.AutomationReady = true;
                tc.AutomationScriptRef = $"{script.FileName} (v{script.Version}, AutomationScript #{script.Id})";
                await _store.UpdateTestCaseAsync(tc);
                testCaseUpdateNote = $"Test case {tc.Id} marked automationReady=true, automationScriptRef updated.";
            }
        }

        return new { script = AutomationScriptResponse.From(script), testCaseUpdate = testCaseUpdateNote };
    }
[McpServerTool(Name = "generate_automation_script"),
     Description("Second AI-generation path (alongside this same MCP being used directly by a connected Claude session): calls Claude SERVER-SIDE using the company's own configured Anthropic API key (see get_ai_settings/save_ai_settings), grounded in real source code fetched from the module's linked repo(s). Requires Lead role or above -- this spends the company's own paid API budget. Fails with a clear error if the company has not configured a key yet; use the MCP-based flow (list_module_repo_links + save_automation_script) instead in that case.")]
    public async Task<object> GenerateAutomationScript(ClaimsPrincipal user, int moduleId, string testCaseId, [Description("e.g. Playwright-TypeScript (optional)")] string? framework = null, int? companyId = null)
    {
        if (!user.CanGenerateAutomationScript())
            return new { error = "You do not have permission to generate automation scripts (Lead role or above required -- this uses the company's own paid Anthropic API key)." };

        var effective = user.ResolveActingCompanyId(companyId);
        if (effective is null) return new { error = "SuperAdmin has no single company -- pass companyId explicitly." };

        var outcome = await _generation.GenerateAsync(effective.Value, moduleId, testCaseId, framework, DisplayNameOf(user));
        if (!outcome.Success) return new { error = outcome.Error, warnings = outcome.Warnings };
        return new { script = AutomationScriptResponse.From(outcome.Script!), warnings = outcome.Warnings };
    }

    [McpServerTool(Name = "generate_automation_scripts_batch"),
     Description("Batch sibling of generate_automation_script, scoped to Module + Layer (e.g. a module's UI Layer, API Layer, or Database Layer test cases) so each item is grounded against the repo link for the layer it actually belongs to. Capped at 5 testCaseIds per call -- each is still a full Anthropic call under the hood, so this bounds cost and blast-radius the same way single-generate does; split larger groups into multiple calls of 5. Requires Lead role or above. A single item failing does not abort the rest of the batch.")]
    public async Task<object> GenerateAutomationScriptsBatch(
        ClaimsPrincipal user,
        int moduleId,
        [Description("Frontend, Backend, or Database -- when given, every testCaseId must have this exact Layer, otherwise that item is skipped with an explanatory error instead of being generated against the wrong repo link. Omit to skip the layer check.")] string? layer,
        [Description("Up to 5 real Test Case Hub test case IDs to generate scripts for")] List<string> testCaseIds,
        [Description("e.g. Playwright-TypeScript (optional)")] string? framework = null,
        int? companyId = null)
    {
        if (!user.CanGenerateAutomationScript())
            return new { error = "You do not have permission to generate automation scripts (Lead role or above required -- this uses the company's own paid Anthropic API key)." };

        var effective = user.ResolveActingCompanyId(companyId);
        if (effective is null) return new { error = "SuperAdmin has no single company -- pass companyId explicitly." };

        if (testCaseIds is null || testCaseIds.Count == 0)
            return new { error = "Provide at least one testCaseId." };
        if (testCaseIds.Count > 5)
            return new { error = $"Batch generation is capped at 5 test cases per call (got {testCaseIds.Count}) -- split into smaller groups, e.g. by Layer, then 5 at a time within that Layer." };

        var items = await _generation.GenerateBatchAsync(effective.Value, moduleId, layer, testCaseIds, framework, DisplayNameOf(user));
        return new
        {
            requested = items.Count,
            succeeded = items.Count(i => i.Success),
            failed = items.Count(i => !i.Success),
            items = items.Select(i => new { testCaseId = i.TestCaseId, success = i.Success, error = i.Error, script = i.Script is null ? null : AutomationScriptResponse.From(i.Script), warnings = i.Warnings })
        };
    }
}
