using System.ComponentModel;
using System.Security.Claims;
using ModelContextProtocol.Server;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.McpTools;

// MCP mirror of CompanyAiSettingsController -- lets an Admin configure the company's own
// Anthropic key from a connected chat session too, not just the REST UI. Never returns the key
// itself, same masking convention as every other secret this app stores.
[McpServerToolType]
public class CompanyAiSettingsMcpTools
{
    private readonly IDataStore _store;
    private readonly SecretProtector _protector;
    public CompanyAiSettingsMcpTools(IDataStore store, SecretProtector protector) { _store = store; _protector = protector; }

    private static string DisplayNameOf(ClaimsPrincipal user) =>
        user.FindFirstValue("displayName") ?? user.FindFirstValue(ClaimsIdentity.DefaultNameClaimType) ?? user.FindFirstValue(ClaimTypes.Email) ?? "Unknown";

    [McpServerTool(Name = "get_ai_settings"), Description("Get the caller's company's AI-generation settings (provider, model, whether a key is set/enabled). Never returns the key itself.")]
    public async Task<object> GetAiSettings(ClaimsPrincipal user, int? companyId = null)
    {
        var effective = user.ResolveActingCompanyId(companyId);
        if (effective is null) return new { error = "SuperAdmin has no single company -- pass companyId explicitly." };
        var settings = await _store.GetCompanyAiSettingsAsync(effective.Value);
        return settings is null
            ? new CompanyAiSettingsResponse(effective.Value, "Anthropic", "claude-sonnet-5", false, false, "", DateTime.UtcNow, "", null)
            : CompanyAiSettingsResponse.From(settings);
    }

    [McpServerTool(Name = "save_ai_settings"), Description("Admin only. Configure the company's own Anthropic API key for direct (non-MCP) automation script generation. Omit apiKey to leave the previously saved key unchanged while updating model/enabled.")]
    public async Task<object> SaveAiSettings(ClaimsPrincipal user, string? apiKey, [Description("e.g. claude-sonnet-5")] string? model = null, bool enabled = true, int? companyId = null)
    {
        if (!user.CanManageAiSettings())
            return new { error = "You do not have permission to manage AI settings (Admin role or above required)." };
        var effective = user.ResolveActingCompanyId(companyId);
        if (effective is null) return new { error = "SuperAdmin has no single company -- pass companyId explicitly." };

        var settings = new CompanyAiSettings
        {
            CompanyId = effective.Value, Provider = "Anthropic",
            Model = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-5" : model,
            Enabled = enabled, CreatedBy = DisplayNameOf(user), UpdatedBy = DisplayNameOf(user)
        };
        if (!string.IsNullOrWhiteSpace(apiKey)) settings.ApiKeyEncrypted = _protector.Protect(apiKey);

        settings = await _store.UpsertCompanyAiSettingsAsync(settings);
        return CompanyAiSettingsResponse.From(settings);
    }
}
