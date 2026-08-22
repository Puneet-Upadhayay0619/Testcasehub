using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TestCaseHub.Api.Models;

namespace TestCaseHub.Api.Services;

public record RepoFile(string Path, string Content);

// Server-side counterpart of what an external MCP-connected chat session does by hand (search
// the linked repo, read a few real files) -- needed for the "company's own Anthropic key"
// generation path (AutomationScriptsController.Generate) since that path has no MCP session to
// lean on. Uses each ModuleRepoLink's own stored, read-only PAT -- never a shared/global token.
// Deliberately simple for a first pass: lists files directly under BasePath (one level, no
// recursive tree-walk) and fetches a bounded number of them, rather than a real code-search --
// good enough for a link that already points at a specific controller/service folder, which is
// exactly the granularity ModuleRepoLink.BasePath was designed for.
public class RepoContentService
{
    private readonly HttpClient _http;
    public RepoContentService(HttpClient http) => _http = http;

    private const int MaxFiles = 4;
    private const int MaxCharsPerFile = 12000;

    public async Task<List<RepoFile>> FetchFilesAsync(ModuleRepoLink link, string accessToken)
    {
        try
        {
            return link.RepoHost == RepoHost.GitHub
                ? await FetchGitHubFilesAsync(link, accessToken)
                : await FetchAdoFilesAsync(link, accessToken);
        }
        catch (Exception ex)
        {
            // Best-effort: a repo-fetch failure (bad token, repo renamed, network blip) should
            // surface as "generated without real source" rather than blow up the whole
            // generation request -- the caller decides what to do with an empty file list.
            return new List<RepoFile> { new($"(error fetching {link.RepoHost} {link.RepoName})", ex.Message) };
        }
    }

    private async Task<List<RepoFile>> FetchGitHubFilesAsync(ModuleRepoLink link, string token)
    {
        var basePath = (link.BasePath ?? "").Trim('/');
        var listUrl = $"https://api.github.com/repos/{link.OrgOrAccount}/{link.RepoName}/contents/{basePath}?ref={Uri.EscapeDataString(link.Branch)}";
        using var listReq = new HttpRequestMessage(HttpMethod.Get, listUrl);
        listReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        listReq.Headers.UserAgent.ParseAdd("TestCaseHub");
        listReq.Headers.Accept.ParseAdd("application/vnd.github+json");
        var listResp = await _http.SendAsync(listReq);
        if (!listResp.IsSuccessStatusCode)
            return new List<RepoFile> { new("(GitHub list failed)", $"{(int)listResp.StatusCode}: {await listResp.Content.ReadAsStringAsync()}") };

        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        // A basePath pointing directly at a single file returns an object, not an array.
        var entries = listDoc.RootElement.ValueKind == JsonValueKind.Array
            ? listDoc.RootElement.EnumerateArray().Where(e => e.GetProperty("type").GetString() == "file").Take(MaxFiles).ToList()
            : new List<JsonElement> { listDoc.RootElement };

        var files = new List<RepoFile>();
        foreach (var entry in entries)
        {
            var path = entry.GetProperty("path").GetString() ?? "";
            var fileUrl = $"https://api.github.com/repos/{link.OrgOrAccount}/{link.RepoName}/contents/{path}?ref={Uri.EscapeDataString(link.Branch)}";
            using var fileReq = new HttpRequestMessage(HttpMethod.Get, fileUrl);
            fileReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            fileReq.Headers.UserAgent.ParseAdd("TestCaseHub");
            var fileResp = await _http.SendAsync(fileReq);
            if (!fileResp.IsSuccessStatusCode) continue;
            using var fileDoc = JsonDocument.Parse(await fileResp.Content.ReadAsStringAsync());
            if (!fileDoc.RootElement.TryGetProperty("content", out var contentEl)) continue;
            var raw = contentEl.GetString() ?? "";
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(raw.Replace("\n", "")));
            files.Add(new RepoFile(path, Truncate(decoded)));
        }
        return files;
    }

    private async Task<List<RepoFile>> FetchAdoFilesAsync(ModuleRepoLink link, string pat)
    {
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
        var basePath = string.IsNullOrWhiteSpace(link.BasePath) ? "/" : link.BasePath;
        var listUrl = $"https://dev.azure.com/{link.OrgOrAccount}/{Uri.EscapeDataString(link.Project)}/_apis/git/repositories/{link.RepoName}/items"
            + $"?path={Uri.EscapeDataString(basePath)}&recursionLevel=OneLevel&versionDescriptor.version={Uri.EscapeDataString(link.Branch)}&api-version=7.1";
        using var listReq = new HttpRequestMessage(HttpMethod.Get, listUrl);
        listReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        var listResp = await _http.SendAsync(listReq);
        if (!listResp.IsSuccessStatusCode)
            return new List<RepoFile> { new("(Azure DevOps list failed)", $"{(int)listResp.StatusCode}: {await listResp.Content.ReadAsStringAsync()}") };

        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var entries = listDoc.RootElement.TryGetProperty("value", out var valueEl)
            ? valueEl.EnumerateArray().Where(e => !(e.TryGetProperty("isFolder", out var f) && f.GetBoolean())).Take(MaxFiles).ToList()
            : new List<JsonElement>();

        var files = new List<RepoFile>();
        foreach (var entry in entries)
        {
            var path = entry.GetProperty("path").GetString() ?? "";
            var fileUrl = $"https://dev.azure.com/{link.OrgOrAccount}/{Uri.EscapeDataString(link.Project)}/_apis/git/repositories/{link.RepoName}/items"
                + $"?path={Uri.EscapeDataString(path)}&versionDescriptor.version={Uri.EscapeDataString(link.Branch)}&api-version=7.1";
            using var fileReq = new HttpRequestMessage(HttpMethod.Get, fileUrl);
            fileReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            fileReq.Headers.Accept.ParseAdd("text/plain");
            var fileResp = await _http.SendAsync(fileReq);
            if (!fileResp.IsSuccessStatusCode) continue;
            var content = await fileResp.Content.ReadAsStringAsync();
            files.Add(new RepoFile(path, Truncate(content)));
        }
        return files;
    }

    private static string Truncate(string s) => s.Length <= MaxCharsPerFile ? s : s[..MaxCharsPerFile] + "\n... (truncated)";
}
