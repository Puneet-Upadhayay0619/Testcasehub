using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TestCaseHub.Api.Services;

public record AnthropicResult(bool Success, string? Text, string? Error);

// Thin wrapper around Anthropic's Messages API, called with the COMPANY'S OWN key
// (CompanyAiSettings.ApiKeyEncrypted, decrypted just-in-time by the caller) -- Test Case Hub
// itself never holds or bills against a shared/global Anthropic key. This is the "direct API"
// generation path that sits alongside (not instead of) the MCP-based one.
public class AnthropicClient
{
    private readonly HttpClient _http;
    public AnthropicClient(HttpClient http) => _http = http;

    public async Task<AnthropicResult> GenerateAsync(string apiKey, string model, string prompt, int maxTokens = 4096)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                model,
                max_tokens = maxTokens,
                messages = new[] { new { role = "user", content = prompt } }
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");

            var resp = await _http.SendAsync(req);
            var respBody = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return new AnthropicResult(false, null, $"Anthropic returned {(int)resp.StatusCode}: {respBody}");

            using var doc = JsonDocument.Parse(respBody);
            var text = doc.RootElement.GetProperty("content")
                .EnumerateArray()
                .Where(b => b.GetProperty("type").GetString() == "text")
                .Select(b => b.GetProperty("text").GetString() ?? "")
                .FirstOrDefault() ?? "";
            return new AnthropicResult(true, text, null);
        }
        catch (Exception ex)
        {
            return new AnthropicResult(false, null, ex.Message);
        }
    }

    // Claude very often wraps generated code in a ```language fenced block plus a sentence of
    // preamble/postamble even when asked not to -- strip that so what gets saved as the script
    // is just the code, matching what a human pasting a hand-written script would have saved.
    public static string StripMarkdownFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```")) return trimmed;
        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0) return trimmed;
        var withoutOpenFence = trimmed[(firstNewline + 1)..];
        var closeFenceIndex = withoutOpenFence.LastIndexOf("```", StringComparison.Ordinal);
        return closeFenceIndex >= 0 ? withoutOpenFence[..closeFenceIndex].TrimEnd() : withoutOpenFence.TrimEnd();
    }
}
