using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TestCaseHub.Api.Dtos;
using TestCaseHub.Api.Models;
using TestCaseHub.Api.Services;
using TestCaseHub.Api.Storage;

namespace TestCaseHub.Api.Controllers;

// Second AI-generation path, added alongside the existing MCP-based one ("dono option rakhne
// hai" -- keep both). A company can plug in their own Anthropic API key here so Test Case Hub's
// backend can generate an automation script directly (see AutomationScriptsController.Generate)
// without needing an external MCP-connected chat session. Admin-only, same bar and same
// never-return-the-secret convention as every other credential this app stores.
[ApiController]
[Authorize]
[Route("api/ai-settings")]
public class CompanyAiSettingsController : ControllerBase
{
    private readonly IDataStore _store;
    private readonly SecretProtector _protector;
    public CompanyAiSettingsController(IDataStore store, SecretProtector protector) { _store = store; _protector = protector; }

    private string ActorDisplayName => User.FindFirstValue("displayName") ?? "Unknown";

    [HttpGet]
    public async Task<ActionResult<CompanyAiSettingsResponse>> Get([FromQuery] int? companyId = null)
    {
        var effective = User.ResolveActingCompanyId(companyId);
        if (effective is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();

        var settings = await _store.GetCompanyAiSettingsAsync(effective.Value);
        if (settings is null)
            return Ok(new CompanyAiSettingsResponse(effective.Value, "Anthropic", "claude-sonnet-5", false, false, "", DateTime.UtcNow, "", null));
        return Ok(CompanyAiSettingsResponse.From(settings));
    }

    [HttpPost]
    public async Task<ActionResult<CompanyAiSettingsResponse>> Save(SaveCompanyAiSettingsRequest req, [FromQuery] int? companyId = null)
    {
        if (!User.CanManageAiSettings()) return Forbid();
        var effective = User.ResolveActingCompanyId(companyId);
        if (effective is null) return User.IsSuperAdmin() ? BadRequest("SuperAdmin must specify ?companyId=.") : Forbid();

        var settings = new CompanyAiSettings
        {
            CompanyId = effective.Value,
            Provider = string.IsNullOrWhiteSpace(req.Provider) ? "Anthropic" : req.Provider,
            Model = string.IsNullOrWhiteSpace(req.Model) ? "claude-sonnet-5" : req.Model,
            Enabled = req.Enabled, CreatedBy = ActorDisplayName, UpdatedBy = ActorDisplayName
        };
        // Only rotate the key if a new one was actually supplied -- omitting it leaves whatever
        // was previously saved in place, same convention as every other secret field.
        if (!string.IsNullOrWhiteSpace(req.ApiKey)) settings.ApiKeyEncrypted = _protector.Protect(req.ApiKey);

        settings = await _store.UpsertCompanyAiSettingsAsync(settings);
        return Ok(CompanyAiSettingsResponse.From(settings));
    }
}
