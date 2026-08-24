using System.Text;
using System.Text.Json;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Services;

public record GenerationOutcome(bool Success, string? Error, AutomationScript? Script, List<string> Warnings);
public record BatchItemOutcome(string TestCaseId, bool Success, string? Error, AutomationScript? Script, List<string> Warnings);

// Ties together CompanyAiSettings (the company's own Anthropic key), ModuleRepoLink +
// RepoContentService (real source code), and AnthropicClient into the "direct API" generation
// path agreed alongside the existing MCP-based one. The result is saved through the exact same
// IDataStore.SaveAutomationScriptAsync the MCP tool and manual REST save use -- so a script
// generated this way is indistinguishable in storage/versioning from one saved by hand or via
// MCP, only GeneratedBy differs.
public class AutomationGenerationService
{
    private readonly IDataStore _store;
    private readonly SecretProtector _protector;
    private readonly RepoContentService _repoContent;
    private readonly AnthropicClient _anthropic;

    public AutomationGenerationService(IDataStore store, SecretProtector protector, RepoContentService repoContent, AnthropicClient anthropic)
    {
        _store = store; _protector = protector; _repoContent = repoContent; _anthropic = anthropic;
    }

    public async Task<GenerationOutcome> GenerateAsync(int companyId, int moduleId, string testCaseId, string? framework, string actorDisplayName)
    {
        var warnings = new List<string>();

        var settings = await _store.GetCompanyAiSettingsAsync(companyId);
        if (settings is null || !settings.Enabled || string.IsNullOrEmpty(settings.ApiKeyEncrypted))
            return new GenerationOutcome(false, "This company has not configured its own Anthropic API key yet (Admin: see AI Settings), or generation is disabled. Use the MCP-based generation path instead, or configure a key here.", null, warnings);

        var tc = await _store.GetTestCaseAsync(testCaseId);
        if (tc is null) return new GenerationOutcome(false, "Test case not found.", null, warnings);
        if (tc.ModuleId != moduleId) return new GenerationOutcome(false, "That test case does not belong to the specified module.", null, warnings);

        var links = await _store.GetModuleRepoLinksAsync(moduleId);
        var files = new List<RepoFile>();
        if (links.Count == 0)
        {
            warnings.Add("No repo linked to this module -- generating from the descriptive test case alone, with no real source code as grounding. Consider adding a Repo Link first for a materially better result.");
        }
        else
        {
            foreach (var link in links)
            {
                if (string.IsNullOrEmpty(link.AccessTokenEncrypted))
                {
                    warnings.Add($"Repo link for layer '{link.Layer}' has no access token saved -- skipped.");
                    continue;
                }
                var token = _protector.Unprotect(link.AccessTokenEncrypted);
                var fetched = await _repoContent.FetchFilesAsync(link, token);
                files.AddRange(fetched);
            }
        }

        var apiKey = _protector.Unprotect(settings.ApiKeyEncrypted);
        var prompt = BuildPrompt(tc, files, framework);
        var result = await _anthropic.GenerateAsync(apiKey, settings.Model, prompt);
        if (!result.Success)
            return new GenerationOutcome(false, $"Anthropic call failed: {result.Error}", null, warnings);

        var content = AnthropicClient.StripMarkdownFence(result.Text ?? "");
        if (string.IsNullOrWhiteSpace(content))
            return new GenerationOutcome(false, "Anthropic returned an empty response.", null, warnings);

        var script = new AutomationScript
        {
            CompanyId = companyId, ModuleId = moduleId, TestCaseId = testCaseId,
            FileName = $"{testCaseId}.spec.ts",
            Framework = string.IsNullOrWhiteSpace(framework) ? "Playwright-TypeScript" : framework,
            Content = content,
            GeneratedBy = $"AI (Claude {settings.Model}, company API key) -- requested by {actorDisplayName}",
            SourceRepoRefs = string.Join(",", links.Select(l => l.Id))
        };
        script = await _store.SaveAutomationScriptAsync(script);
        return new GenerationOutcome(true, null, script, warnings);
    }

    // Module+Layer scoped batch: caller (controller/MCP) has already capped TestCaseIds at 5.
    // Each item still gets its own full GenerateAsync call -- same per-item validation, same
    // warnings, same save -- this just loops instead of making the caller click 46 times. A
    // single item failing (bad test case id, repo fetch error, etc.) does not abort the rest of
    // the batch; its failure is reported back in that item's own result.
    public async Task<List<BatchItemOutcome>> GenerateBatchAsync(int companyId, int moduleId, string? layer, string? verificationType, List<string> testCaseIds, string? framework, string actorDisplayName)
    {
        var results = new List<BatchItemOutcome>();
        foreach (var testCaseId in testCaseIds)
        {
            if (!string.IsNullOrWhiteSpace(layer) || !string.IsNullOrWhiteSpace(verificationType))
            {
                var tc = await _store.GetTestCaseAsync(testCaseId);
                if (tc is null)
                {
                    results.Add(new BatchItemOutcome(testCaseId, false, "Test case not found.", null, new List<string>()));
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(layer) && !string.Equals(tc.Layer, layer, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new BatchItemOutcome(testCaseId, false, $"Test case's Layer is '{tc.Layer}', not '{layer}' -- skipped so it doesn't get grounded against the wrong repo link. Regroup it into its correct batch.", null, new List<string>()));
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(verificationType) && !string.Equals(tc.VerificationType, verificationType, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new BatchItemOutcome(testCaseId, false, $"Test case's VerificationType is '{tc.VerificationType}', not '{verificationType}' -- skipped. Regroup it into its correct batch.", null, new List<string>()));
                    continue;
                }
            }

            var outcome = await GenerateAsync(companyId, moduleId, testCaseId, framework, actorDisplayName);
            results.Add(new BatchItemOutcome(testCaseId, outcome.Success, outcome.Error, outcome.Script, outcome.Warnings));
        }
        return results;
    }

    private static string BuildPrompt(TestCase tc, List<RepoFile> files, string? framework)
    {
        var fw = string.IsNullOrWhiteSpace(framework) ? "Playwright with TypeScript" : framework;
        var sb = new StringBuilder();
        sb.AppendLine($"Write a complete, runnable {fw} automation test script for the following test case from a QA test management tool.");
        sb.AppendLine("Respond with ONLY the code for a single test file -- no explanation, no markdown fences, no commentary before or after.");
        sb.AppendLine();
        sb.AppendLine($"Test case ID: {tc.Id}");
        sb.AppendLine($"Title: {tc.Title}");
        sb.AppendLine($"Layer: {tc.Layer} | Verification type: {tc.VerificationType} | Priority: {tc.Priority}");
        sb.AppendLine($"Preconditions: {tc.Preconditions}");
        sb.AppendLine("Steps (action -> expected result):");
        foreach (var step in tc.Steps)
            sb.AppendLine($"  {step.StepNo}. {step.Action} -> {step.ExpectedResult}");
        if (tc.Tags.Count > 0) sb.AppendLine($"Tags: {string.Join(", ", tc.Tags)}");
        sb.AppendLine();

        if (files.Count > 0)
        {
            sb.AppendLine("Real source code from the system under test (use this to ground real endpoint routes, payload shapes, and validation behaviour -- do not invent an API contract that contradicts this code; if the code shows a validation rule the test case doesn't expect, or vice versa, still write the assertion to match the TEST CASE's expectation, and add a one-line comment flagging the discrepancy as a real finding rather than silently matching the code):");
            foreach (var f in files)
            {
                sb.AppendLine($"--- {f.Path} ---");
                sb.AppendLine(f.Content);
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("No source code was available for this module -- write the best test you can from the descriptive test case alone, using reasonable placeholder endpoint/selector names clearly marked as TODO for a human to confirm.");
        }

        return sb.ToString();
    }
}
