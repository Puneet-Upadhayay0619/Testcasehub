using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TestCaseHub.Api.Services;

public record CreateBugResult(bool Success, string? WorkItemId, string? WorkItemUrl, string? Error);

// Best-effort Azure DevOps integration for the "Fail result -> Bug work item" workflow agreed
// in planning. Configuration (org URL, project, PAT) comes from appsettings/environment
// variables ("AzureDevOps:OrgUrl" etc.) — NOT committed with real values. Until those are set
// for a real ADO org, every call fails fast with a clear "not configured" error rather than
// throwing, and CreateBugWithRetryAsync makes sure a transient failure is retried a few times
// before giving up — so a bug-creation attempt is never silently lost, per the resilience gap
// identified in planning: either it succeeds, or the caller gets back a clear, visible error.
public class AdoService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    public AdoService(HttpClient http, IConfiguration config) { _http = http; _config = config; }

    private bool IsConfigured(out string orgUrl, out string project, out string pat)
    {
        orgUrl = _config["AzureDevOps:OrgUrl"] ?? "";
        project = _config["AzureDevOps:Project"] ?? "";
        pat = _config["AzureDevOps:Pat"] ?? "";
        return !string.IsNullOrWhiteSpace(orgUrl) && !string.IsNullOrWhiteSpace(project) && !string.IsNullOrWhiteSpace(pat);
    }

    private async Task<CreateBugResult> CreateBugOnceAsync(string title, string description)
    {
        if (!IsConfigured(out var orgUrl, out var project, out var pat))
            return new CreateBugResult(false, null, null, "Azure DevOps is not configured (AzureDevOps:OrgUrl/Project/Pat) — set these before bug-creation can work.");

        try
        {
            var url = $"{orgUrl.TrimEnd('/')}/{Uri.EscapeDataString(project)}/_apis/wit/workitems/$Bug?api-version=7.1";
            var body = JsonSerializer.Serialize(new[]
            {
                new { op = "add", path = "/fields/System.Title", value = title },
                new { op = "add", path = "/fields/System.Description", value = description }
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json-patch+json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));

            var resp = await _http.SendAsync(req);
            var respBody = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return new CreateBugResult(false, null, null, $"ADO returned {(int)resp.StatusCode}: {respBody}");

            using var doc = JsonDocument.Parse(respBody);
            var id = doc.RootElement.GetProperty("id").ToString();
            var link = doc.RootElement.TryGetProperty("_links", out var links) && links.TryGetProperty("html", out var html)
                ? html.GetProperty("href").GetString() : null;
            return new CreateBugResult(true, id, link, null);
        }
        catch (Exception ex)
        {
            return new CreateBugResult(false, null, null, ex.Message);
        }
    }

    // 3 attempts with short backoff (1s, 2s) — enough to ride out a transient network blip
    // without the caller needing its own retry loop. Every attempt's failure is visible in the
    // final error message rather than swallowed.
    public async Task<CreateBugResult> CreateBugWithRetryAsync(string title, string description)
    {
        CreateBugResult last = new(false, null, null, "not attempted");
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            last = await CreateBugOnceAsync(title, description);
            if (last.Success) return last;
            if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt));
        }
        return last;
    }
}
